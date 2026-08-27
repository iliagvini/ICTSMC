using System;
using System.Collections.Generic;
using System.Linq;
using ATAS.Indicators;

namespace ICTSMC
{
    public partial class ICTSMCStrategy
    {
        /// <summary>
        /// Two zones of the same kind count as duplicates only when they cover
        /// substantially the same territory. A mere edge touch does NOT suppress the
        /// newer zone: consecutive FVGs inside one impulse leg routinely graze each
        /// other and, in ICT, a stacked cluster is a STRONGER draw, not a duplicate.
        /// </summary>
        private const decimal ZoneDuplicateOverlap = 0.7m;

        /// <summary>
        /// How similar in HEIGHT two overlapping zones must be to count as duplicates.
        /// Below this the smaller zone is a materially tighter version of the same level and
        /// is kept on its own merits.
        /// </summary>
        private const decimal ZoneDuplicateSizeRatio = 0.6m;

        /// <summary>
        /// Runs once per finalized candle. All pattern DETECTION happens here;
        /// touch/sweep/mitigation REACTION happens intrabar in ProcessIntrabar.
        /// </summary>
        private void OnBarComplete(int bar)
        {
            if (bar < 1)
                return;

            // Measured first, and unconditionally: the chart's own timeframe is a property
            // of the chart, not of the HTF feature that used to own it.
            UpdateChartTimeframe(bar);

            UpdateAtr(bar);
            ClassifyFinishedSweeps(bar);
            ConfirmSwings(bar);
            DetectStructureBreak(bar);
            UpdateLegExtreme(bar);
            DetectFvg(bar);
            ApplyBodyCloseMitigation(bar);
            UpdateSessionLevels(bar);
            UpdateHtf(bar);
            UpdateOpenSignals(bar);
            CheckOpenSignalThreats(bar);
            Prune(bar);
            FlushJournalBuffers();

            // Everything above can mutate the engine state the renderer mirrors.
            MarkRenderDirty();
        }

        #region ATR

        /// <summary>
        /// Wilder ATR, seeded from a full <see cref="AtrPeriod"/>-bar simple average
        /// rather than a single true range. Seeding from one bar made the first ~14
        /// bars of every recalculation wildly noisy, which in turn distorted the FVG
        /// size filter and the OB displacement filter exactly where the engine has
        /// the least context.
        /// </summary>
        private void UpdateAtr(int bar)
        {
            var candle = GetCandle(bar);
            var prev = GetCandle(bar - 1);

            var tr = Math.Max(candle.High - candle.Low,
                Math.Max(Math.Abs(candle.High - prev.Close), Math.Abs(candle.Low - prev.Close)));

            if (_atrSamples < AtrPeriod)
            {
                _atrSeedSum += tr;
                _atrSamples++;
                _atr = _atrSeedSum / _atrSamples;
                return;
            }

            _atr += (tr - _atr) / AtrPeriod; // Wilder smoothing
        }

        #endregion

        #region Swings & liquidity creation

        /// <summary>
        /// A fractal swing at pivot p is confirmed once SwingPeriod bars have closed
        /// after it, i.e. while finalizing bar = p + SwingPeriod.
        /// </summary>
        private void ConfirmSwings(int bar)
        {
            var p = bar - SwingPeriod;
            if (p < SwingPeriod)
                return;

            var pivot = GetCandle(p);

            var isHigh = true;
            var isLow = true;

            for (var j = p - SwingPeriod; j <= p + SwingPeriod; j++)
            {
                if (j == p)
                    continue;

                var c = GetCandle(j);
                if (c.High > pivot.High)
                    isHigh = false;
                if (c.Low < pivot.Low)
                    isLow = false;
                if (!isHigh && !isLow)
                    return;
            }

            if (isHigh && (_swingHighs.Count == 0 || _swingHighs[^1].Bar != p))
            {
                var swing = new SwingPoint { Bar = p, Price = pivot.High };
                _swingHighs.Add(swing);
                AdoptProtectedHigh(swing);
                RegisterLiquidity(swing, buySide: true);
            }

            if (isLow && (_swingLows.Count == 0 || _swingLows[^1].Bar != p))
            {
                var swing = new SwingPoint { Bar = p, Price = pivot.Low };
                _swingLows.Add(swing);
                AdoptProtectedLow(swing);
                RegisterLiquidity(swing, buySide: false);
            }
        }

        /// <summary>
        /// Chooses which swing high the structure tracker defends.
        ///
        /// Taking the most RECENT pivot unconditionally (the previous behaviour) meant
        /// that a lower high printed during a pullback replaced the real, still-unbroken
        /// structural high above it — so breaking that minor high registered a BoS/MSS,
        /// created an order block, re-anchored the dealing range and flipped the trend,
        /// none of which is a structure break in ICT terms. That is INTERNAL structure.
        ///
        /// With protected swings on, the defended high is only replaced WITHIN a leg when
        /// the old one has actually been broken, or when the new pivot is higher (price
        /// already traded through the old one, so it protects nothing).
        ///
        /// The leg boundary matters: on an opposite-direction break the counter-side pivot
        /// is re-anchored to the most recent unbroken swing (see ReanchorCounterSide).
        /// Without that, "keep the more extreme unbroken high" degenerates into "keep the
        /// ALL-TIME high" — a bullish break would require taking out the highest high of
        /// the entire series, so bullish structure, every MSS, and with it the whole armed
        /// entry model could never fire.
        /// </summary>
        private void AdoptProtectedHigh(SwingPoint swing)
        {
            if (!UseProtectedSwings || _lastSwingHigh == null || _lastSwingHigh.Broken ||
                swing.Price > _lastSwingHigh.Price)
            {
                _lastSwingHigh = swing;
            }
        }

        private void AdoptProtectedLow(SwingPoint swing)
        {
            if (!UseProtectedSwings || _lastSwingLow == null || _lastSwingLow.Broken ||
                swing.Price < _lastSwingLow.Price)
            {
                _lastSwingLow = swing;
            }
        }

        /// <summary>
        /// Every confirmed swing high leaves buy-stops resting above it (BSL),
        /// every swing low leaves sell-stops below (SSL). Near-equal levels are
        /// merged into a single, stronger "equal highs/lows" pool.
        /// </summary>
        private void RegisterLiquidity(SwingPoint swing, bool buySide)
        {
            var tolerance = EqualLevelTicks * InstrumentTickSize;

            var existing = _liquidity.FirstOrDefault(l =>
                !l.Swept && !l.IsSessionLevel && l.BuySide == buySide &&
                Math.Abs(l.Price - swing.Price) <= tolerance);

            if (existing != null)
            {
                existing.IsEqual = true;
                // Anchor the pool at the extreme of the cluster.
                existing.Price = buySide ? Math.Max(existing.Price, swing.Price)
                                         : Math.Min(existing.Price, swing.Price);
                return;
            }

            _liquidity.Add(new LiquidityLevel
            {
                Price = swing.Price,
                StartBar = swing.Bar,
                BuySide = buySide
            });

            // Keep only the most recent unswept SWING levels per side. Session
            // extremes (PDH/PDL/PWH/PWL) are the strongest draws on the chart and are
            // deliberately exempt from this cull.
            var side = _liquidity.Where(l => l.BuySide == buySide && !l.Swept && !l.IsSessionLevel)
                                 .OrderByDescending(l => l.StartBar)
                                 .ToList();

            foreach (var stale in side.Skip(MaxLiquidityPerSide))
                _liquidity.Remove(stale);
        }

        /// <summary>
        /// Previous-day and previous-week highs and lows — the canonical ICT draws on
        /// liquidity, and the levels every retail stop sits against. Buckets follow the
        /// same anchoring as the HTF aggregator (so DailyAnchorMinutes shifts them to a
        /// futures session open), and only the most recent PDH/PDL/PWH/PWL per side is
        /// kept: an older one is history, not a draw.
        /// </summary>
        private void UpdateSessionLevels(int bar)
        {
            if (!SessionLevelsEnabled)
                return;

            var c = GetCandle(bar);

            var day = GetBucketStart(c.Time, 1440);
            if (!_dayOpen || day > _currentDayBucket)
            {
                if (_dayOpen)
                {
                    RegisterSessionLevel(_dayHigh, bar, buySide: true, LiquidityOrigin.PreviousDay);
                    RegisterSessionLevel(_dayLow, bar, buySide: false, LiquidityOrigin.PreviousDay);
                }

                _currentDayBucket = day;
                _dayHigh = c.High;
                _dayLow = c.Low;
                _dayOpen = true;
            }
            else
            {
                if (c.High > _dayHigh) _dayHigh = c.High;
                if (c.Low < _dayLow) _dayLow = c.Low;
            }

            var week = GetBucketStart(c.Time, 10080);
            if (!_weekOpen || week > _currentWeekBucket)
            {
                if (_weekOpen)
                {
                    RegisterSessionLevel(_weekHigh, bar, buySide: true, LiquidityOrigin.PreviousWeek);
                    RegisterSessionLevel(_weekLow, bar, buySide: false, LiquidityOrigin.PreviousWeek);
                }

                _currentWeekBucket = week;
                _weekHigh = c.High;
                _weekLow = c.Low;
                _weekOpen = true;
            }
            else
            {
                if (c.High > _weekHigh) _weekHigh = c.High;
                if (c.Low < _weekLow) _weekLow = c.Low;
            }
        }

        private void RegisterSessionLevel(decimal price, int bar, bool buySide, LiquidityOrigin origin)
        {
            if (price <= 0m)
                return;

            // Only the newest session extreme per side/origin is a live draw.
            _liquidity.RemoveAll(l => l.Origin == origin && l.BuySide == buySide && !l.Swept);

            _liquidity.Add(new LiquidityLevel
            {
                Price = price,
                StartBar = bar,
                BuySide = buySide,
                Origin = origin
            });

            JournalEvent(bar, "SessionLevel", buySide ? "BuySide" : "SellSide", null, price,
                origin == LiquidityOrigin.PreviousDay ? "Previous day extreme" : "Previous week extreme");
        }

        #endregion

        #region Structure (BoS / MSS) + order blocks

        private void DetectStructureBreak(int bar)
        {
            var close = GetCandle(bar).Close;

            if (_lastSwingHigh is { Broken: false } && close > _lastSwingHigh.Price)
            {
                _lastSwingHigh.Broken = true;
                var isMss = ClassifyBreak(bar, bullish: true, reversal: _trend == -1);
                _trend = 1;

                var evt = new StructureEvent
                {
                    Bar = bar,
                    FromBar = _lastSwingHigh.Bar,
                    Level = _lastSwingHigh.Price,
                    Bullish = true,
                    IsMss = isMss
                };
                _structure.Add(evt);
                ReanchorCounterSide(bullish: true);
                AnchorLeg(evt);
                OnStructureEvent(evt);

                // Detection is never gated on a DISPLAY toggle: hiding order blocks
                // must not silently change which signals the engine produces.
                CreateOrderBlock(bar, bullish: true);
            }
            // Mutually exclusive by construction (a close cannot be both above the protected
            // high and below the protected low unless the two have crossed), and written as
            // an else so a degenerate anchor state can never produce two structure events,
            // two trend flips, two order blocks and two leg re-anchors on one bar.
            else if (_lastSwingLow is { Broken: false } && close < _lastSwingLow.Price)
            {
                _lastSwingLow.Broken = true;
                var isMss = ClassifyBreak(bar, bullish: false, reversal: _trend == 1);
                _trend = -1;

                var evt = new StructureEvent
                {
                    Bar = bar,
                    FromBar = _lastSwingLow.Bar,
                    Level = _lastSwingLow.Price,
                    Bullish = false,
                    IsMss = isMss
                };
                _structure.Add(evt);
                ReanchorCounterSide(bullish: false);
                AnchorLeg(evt);
                OnStructureEvent(evt);

                CreateOrderBlock(bar, bullish: false);
            }
        }

        /// <summary>
        /// Decides whether a break counts as a Market Structure Shift.
        ///
        /// A reversal-direction break is the NECESSARY condition, and used to be the only
        /// one — which made "MSS" a synonym for "this break went the other way to the last
        /// one". In a range that is every oscillation between the same two extremes, and
        /// because an MSS is what arms the entry model, the machine was most active exactly
        /// where the book says to stand aside.
        ///
        /// With <see cref="RequireDisplacementForMss"/> a reversal must also DISPLACE to earn
        /// the label. The break is otherwise recorded as an ordinary BoS: it still flips the
        /// tracked trend and still produces its order block, it simply does not arm a
        /// reversal setup.
        /// </summary>
        private bool ClassifyBreak(int bar, bool bullish, bool reversal)
        {
            if (!reversal || !RequireDisplacementForMss)
                return reversal;

            if (BreakDisplaced(bar, bullish, out var impulse, out var required, out var hasImbalance))
                return true;

            JournalEvent(bar, "MssDemoted", bullish ? "Bull" : "Bear", null, GetCandle(bar).Close,
                $"reversal break did not displace — recorded as BoS, entry model not armed; " +
                $"impulse {Num(impulse)} vs ATR×{DisplacementAtrFactor} = {Num(required)}; " +
                $"imbalance in leg: {(hasImbalance ? "yes" : "no")}");

            return false;
        }

        /// <summary>
        /// The two proofs of displacement, applied to a structure break's own leg — the same
        /// pair <see cref="CreateOrderBlock"/> demands, for the same reason: raw distance is
        /// not displacement without the velocity that leaves an imbalance behind.
        /// </summary>
        private bool BreakDisplaced(int breakBar, bool bullish, out decimal impulse, out decimal required,
            out bool hasImbalance)
        {
            var from = Math.Max(1, breakBar - ObLookback);
            var close = GetCandle(breakBar).Close;

            // Origin of the leg into the break: its most extreme counter-side price.
            var originBar = from;
            var originPrice = bullish ? GetCandle(from).Low : GetCandle(from).High;

            for (var i = from; i <= breakBar; i++)
            {
                var c = GetCandle(i);
                if (bullish)
                {
                    if (c.Low < originPrice) { originPrice = c.Low; originBar = i; }
                }
                else
                {
                    if (c.High > originPrice) { originPrice = c.High; originBar = i; }
                }
            }

            impulse = bullish ? close - originPrice : originPrice - close;
            required = _atr * DisplacementAtrFactor;

            // The scan must always reach the break bar's OWN three-candle window. When the
            // leg's origin IS the break bar (a single engulfing candle that both made the
            // low and closed through the protected high — the most violent displacement
            // there is) a scan starting at origin+2 examines nothing at all and would report
            // "no imbalance" for the clearest possible case.
            hasImbalance = LegHasImbalance(Math.Max(0, Math.Min(originBar, breakBar - 2)), breakBar, bullish);

            if (_atr > 0 && impulse < required)
                return false;

            return hasImbalance;
        }

        /// <summary>
        /// A structure break ends the previous leg, so the pivot the OPPOSITE side
        /// defends is re-anchored to the most recent unbroken swing on that side —
        /// the origin of the leg that just started. Breaking it later is the CHoCH
        /// that flips the trend and produces an MSS.
        ///
        /// This is what keeps "protected swing" meaning *protected within the current
        /// leg* rather than *the most extreme pivot ever seen*: without it a downtrend
        /// pins the defended high at the all-time high and no bullish break can occur.
        /// </summary>
        private void ReanchorCounterSide(bool bullish)
        {
            if (!UseProtectedSwings)
                return;

            if (bullish)
            {
                var low = _swingLows.LastOrDefault(l => !l.Broken);
                if (low != null)
                    _lastSwingLow = low;
            }
            else
            {
                var high = _swingHighs.LastOrDefault(h => !h.Broken);
                if (high != null)
                    _lastSwingHigh = high;
            }
        }

        /// <summary>
        /// Re-anchors the dealing range to the CURRENT impulse leg on every
        /// structure break. A bearish break measures from the origin high of the
        /// down-move (the most recent swing high, extended to any unconfirmed
        /// higher high between it and the break) down to the running low; bullish
        /// mirror. This keeps equilibrium structural and current instead of
        /// hanging from a stale pre-break extreme — which was misclassifying
        /// valid post-MSS retrace zones as "in discount" and vetoing them.
        /// Only completed bars feed the scan, so the range is fully deterministic
        /// across live ticks and history replay.
        /// </summary>
        private void AnchorLeg(StructureEvent evt)
        {
            var b = evt.Bar;

            if (evt.Bullish)
            {
                var from = Math.Max(1, _lastSwingLow?.Bar ?? b - SwingPeriod * 4);

                var anchorBar = from;
                var anchorPrice = GetCandle(from).Low;
                for (var i = from; i <= b; i++)
                {
                    var lo = GetCandle(i).Low;
                    if (lo < anchorPrice) { anchorPrice = lo; anchorBar = i; }
                }

                var extremeBar = anchorBar;
                var extremePrice = GetCandle(anchorBar).High;
                for (var i = anchorBar; i <= b; i++)
                {
                    var hi = GetCandle(i).High;
                    if (hi >= extremePrice) { extremePrice = hi; extremeBar = i; }
                }

                _legDirection = 1;
                _legAnchor = new SwingPoint { Bar = anchorBar, Price = anchorPrice };
                _legExtreme = new SwingPoint { Bar = extremeBar, Price = extremePrice };
            }
            else
            {
                var from = Math.Max(1, _lastSwingHigh?.Bar ?? b - SwingPeriod * 4);

                var anchorBar = from;
                var anchorPrice = GetCandle(from).High;
                for (var i = from; i <= b; i++)
                {
                    var hi = GetCandle(i).High;
                    if (hi > anchorPrice) { anchorPrice = hi; anchorBar = i; }
                }

                var extremeBar = anchorBar;
                var extremePrice = GetCandle(anchorBar).Low;
                for (var i = anchorBar; i <= b; i++)
                {
                    var lo = GetCandle(i).Low;
                    if (lo <= extremePrice) { extremePrice = lo; extremeBar = i; }
                }

                _legDirection = -1;
                _legAnchor = new SwingPoint { Bar = anchorBar, Price = anchorPrice };
                _legExtreme = new SwingPoint { Bar = extremeBar, Price = extremePrice };
            }

            var legHigh = _legDirection == 1 ? _legExtreme.Price : _legAnchor.Price;
            var legLow = _legDirection == 1 ? _legAnchor.Price : _legExtreme.Price;
            JournalEvent(b, "RangeAnchored", evt.Bullish ? "Bull" : "Bear", null, (legHigh + legLow) / 2m,
                $"{(evt.IsMss ? "MSS" : "BoS")} re-anchor: LegHigh={legHigh} (bar {(_legDirection == 1 ? _legExtreme.Bar : _legAnchor.Bar)}), " +
                $"LegLow={legLow} (bar {(_legDirection == 1 ? _legAnchor.Bar : _legExtreme.Bar)}), EQ={(legHigh + legLow) / 2m}");
        }

        /// <summary>Extends the current leg's running extreme as new bars complete.</summary>
        private void UpdateLegExtreme(int bar)
        {
            if (_legDirection == 0 || _legExtreme == null)
                return;

            var candle = GetCandle(bar);

            if (_legDirection == 1 && candle.High >= _legExtreme.Price)
            {
                _legExtreme.Price = candle.High;
                _legExtreme.Bar = bar;
            }
            else if (_legDirection == -1 && candle.Low <= _legExtreme.Price)
            {
                _legExtreme.Price = candle.Low;
                _legExtreme.Bar = bar;
            }
        }

        /// <summary>
        /// Book rule: the OB is the LAST OPPOSITE-COLORED candle before the big move.
        /// We only accept it when the move actually DISPLACED — which needs two
        /// independent proofs, because raw distance alone is not displacement:
        ///
        ///  1. magnitude — the impulse from the OB candle's extreme to the breaking
        ///     close is at least ATR × DisplacementAtrFactor;
        ///  2. velocity  — the leg left a genuine imbalance (a 3-candle FVG) behind.
        ///
        /// Without (2), a 15-bar grind that happens to cover 1.5 × ATR passes exactly
        /// like a single violent displacement candle, and the longer the lookback the
        /// EASIER the filter becomes — precisely backwards. An unfilled gap is ICT's
        /// own definition of a move too fast for the book to keep up with.
        /// </summary>
        private void CreateOrderBlock(int breakBar, bool bullish)
        {
            var breakClose = GetCandle(breakBar).Close;

            // The OB precedes the displacement, so the breaking candle itself is never
            // a candidate — the move IS that candle.
            for (var i = breakBar - 1; i >= Math.Max(1, breakBar - ObLookback); i--)
            {
                var c = GetCandle(i);
                var isOpposite = bullish ? c.Close < c.Open : c.Close > c.Open;
                if (!isOpposite)
                    continue;

                // Magnitude: the impulse away from the OB candle must be meaningful.
                var impulse = bullish ? breakClose - c.Low : c.High - breakClose;
                if (_atr > 0 && impulse < _atr * DisplacementAtrFactor)
                    return;

                // Velocity: the leg must have left an unfilled imbalance behind.
                if (RequireImbalanceForOb && !LegHasImbalance(i, breakBar, bullish))
                {
                    JournalEvent(breakBar, "ObRejected", bullish ? "Bull" : "Bear", null, breakClose,
                        $"no imbalance in the displacement leg (bars {i}-{breakBar}); distance {Num(impulse)} " +
                        $"vs ATR×{DisplacementAtrFactor} = {Num(_atr * DisplacementAtrFactor)} — drift, not displacement");
                    return;
                }

                decimal top, bottom;
                if (ObStyle == ObZoneStyle.Body)
                {
                    top = Math.Max(c.Open, c.Close);
                    bottom = Math.Min(c.Open, c.Close);
                }
                else
                {
                    top = c.High;
                    bottom = c.Low;
                }

                AddZone(new Zone
                {
                    Type = bullish ? ZoneType.BullOrderBlock : ZoneType.BearOrderBlock,
                    StartBar = i,
                    Top = top,
                    Bottom = bottom
                }, breakBar);
                return;
            }
        }

        /// <summary>
        /// True when the leg between the order-block candle and the break contains a
        /// 3-candle imbalance in the direction of the move — the footprint of real
        /// displacement.
        /// </summary>
        private bool LegHasImbalance(int obBar, int breakBar, bool bullish)
        {
            var minGap = MinFvgTicks * InstrumentTickSize;

            for (var b = obBar + 2; b <= breakBar; b++)
            {
                if (b - 2 < 0)
                    continue;

                var a = GetCandle(b - 2);
                var c = GetCandle(b);

                if (bullish)
                {
                    if (c.Low > a.High && c.Low - a.High >= minGap)
                        return true;
                }
                else
                {
                    if (c.High < a.Low && a.Low - c.High >= minGap)
                        return true;
                }
            }

            return false;
        }

        #endregion

        #region FVG

        /// <summary>
        /// 3-candle imbalance finalized at <paramref name="bar"/>:
        /// bullish when Low[bar] gaps above High[bar-2], bearish when High[bar]
        /// gaps below Low[bar-2].
        ///
        /// Detection is intentionally NOT gated on the ShowFvg display toggle: the
        /// entry model, confluence scoring and the journal all consume these zones,
        /// so hiding them on the chart must not change what the strategy does.
        /// </summary>
        private void DetectFvg(int bar)
        {
            if (bar < 2)
                return;

            var c0 = GetCandle(bar - 2);
            var c2 = GetCandle(bar);

            var minSize = Math.Max(MinFvgTicks * InstrumentTickSize, _atr * MinFvgAtrFraction);

            if (c2.Low > c0.High && c2.Low - c0.High >= minSize)
            {
                AddZone(new Zone
                {
                    Type = ZoneType.BullFvg,
                    StartBar = bar - 1,
                    Top = c2.Low,
                    Bottom = c0.High
                }, bar);
            }
            else if (c2.High < c0.Low && c0.Low - c2.High >= minSize)
            {
                AddZone(new Zone
                {
                    Type = ZoneType.BearFvg,
                    StartBar = bar - 1,
                    Top = c0.Low,
                    Bottom = c2.High
                }, bar);
            }
        }

        #endregion

        #region Zone bookkeeping

        /// <summary>
        /// Fraction of the SMALLER zone that the two zones share. 1 = one fully
        /// contains the other, 0 = disjoint.
        /// </summary>
        private static decimal OverlapRatio(Zone a, Zone b)
        {
            var overlap = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
            if (overlap < 0m)
                return 0m;

            var smaller = Math.Min(a.Height, b.Height);
            if (smaller <= 0m)
                return 1m; // a degenerate (zero-height) zone sitting inside another

            return overlap / smaller;
        }

        /// <summary>
        /// Two zones of the same kind are duplicates when they cover substantially the same
        /// territory AND are of comparable size.
        ///
        /// The size test matters because the overlap ratio is measured against the SMALLER
        /// zone, so a tight gap fully contained inside a wide one always scored 1.0 and was
        /// discarded. That is backwards: a narrow zone inside a broad one is the better
        /// entry — a tighter stop off the same level — not a redundant copy of it.
        /// </summary>
        private static bool IsDuplicateZone(Zone candidate, Zone existing)
        {
            if (OverlapRatio(candidate, existing) < ZoneDuplicateOverlap)
                return false;

            var larger = Math.Max(candidate.Height, existing.Height);
            if (larger <= 0m)
                return true;

            var smaller = Math.Min(candidate.Height, existing.Height);
            return smaller / larger >= ZoneDuplicateSizeRatio;
        }

        /// <param name="bar">
        /// The chart bar the zone became KNOWN at, which is not the bar its geometry starts
        /// at — an HTF zone is anchored to the first chart bar of its own candle but cannot
        /// be detected until that candle closes. Everything that asks "is this zone fresh?"
        /// (the exit-warning radar, the journal timestamp) needs the former.
        /// </param>
        private void AddZone(Zone zone, int bar)
        {
            // Suppress only genuine duplicates — an active zone of the same kind
            // covering substantially the same territory at a comparable size. Zones that
            // merely graze each other are kept: stacked imbalances in one impulse leg are a
            // stronger draw in ICT, not noise, and silently dropping them under-counted the
            // confluence stack the entry model reports.
            var duplicate = _zones.Any(z =>
                z.State != ZoneState.Mitigated &&
                z.Type == zone.Type &&
                z.IsHtf == zone.IsHtf &&
                z.HtfLabel == zone.HtfLabel &&
                IsDuplicateZone(zone, z));

            if (duplicate)
                return;

            zone.Id = ++_nextZoneId;
            zone.CreatedBar = bar;
            _zones.Add(zone);
            OnZoneCreated(zone);
            JournalEvent(bar, "ZoneCreated", zone.IsBullish ? "Bull" : "Bear", zone, 0m, "");
            MarkRenderDirty();

            var sameType = _zones.Where(z => z.Type == zone.Type && z.IsHtf == zone.IsHtf &&
                                             z.HtfLabel == zone.HtfLabel && z.State != ZoneState.Mitigated)
                                 .OrderByDescending(z => z.StartBar)
                                 .ToList();

            var cap = zone.IsHtf ? MaxHtfZones : MaxZonesPerType;
            foreach (var stale in sameType.Skip(cap))
                _zones.Remove(stale);
        }

        /// <summary>
        /// Body-close logic can only be judged on a finalized candle. Three jobs here:
        /// BodyClose-rule mitigation; Inversion-FVG creation — a candle body closing
        /// through a fair value gap flips its polarity (failed bullish gap becomes
        /// resistance, failed bearish gap becomes support); and Breaker creation — an
        /// order block that price closes decisively through has FAILED, and the
        /// trapped participants inside it defend it from the other side on the retest.
        /// The trapped traders are the fuel of the new zone in both cases.
        /// </summary>
        private void ApplyBodyCloseMitigation(int bar)
        {
            var candle = GetCandle(bar);
            var bodyLow = Math.Min(candle.Open, candle.Close);
            var bodyHigh = Math.Max(candle.Open, candle.Close);

            var flipped = new List<Zone>();

            foreach (var zone in _zones)
            {
                if (zone.StartBar >= bar)
                    continue;

                // HTF zones are judged on HTF candle bodies, in ApplyHtfBodyClose.
                // Using the chart candle here meant a 4H gap could be inverted by a
                // single 15-minute body close - 16 chances per 4H candle instead of one,
                // and a transient dip a real 4H candle would have absorbed as a wick.
                if (zone.IsHtf)
                    continue;

                ApplyBodyCloseToZone(zone, bodyLow, bodyHigh, bar, bar - 3, flipped);
            }

            CommitFlippedZones(flipped, bar, "");

        }

        /// <summary>
        /// Body-close semantics for one zone: BodyClose mitigation, FVG inversion and
        /// order-block breakers. Shared by the chart-timeframe pass and the per-layer HTF
        /// pass so both apply identical rules - the only difference is WHOSE candle body
        /// is handed in.
        /// </summary>
        private void ApplyBodyCloseToZone(Zone zone, decimal bodyLow, decimal bodyHigh, int bar,
            int flipFloorBar, List<Zone> flipped)
        {
                var rule = zone.IsOrderBlock ? ObMitigation : FvgMitigation;
                if (zone.State != ZoneState.Mitigated && rule == MitigationRule.BodyClose)
                {
                    if (zone.IsBullish && bodyLow < zone.Bottom)
                        Mitigate(zone, bar);
                    else if (!zone.IsBullish && bodyHigh > zone.Top)
                        Mitigate(zone, bar);
                }

                // IFVG: only plain FVGs invert (an inversion never re-inverts), and only
                // around the moment they are actually broken — a body close through the
                // gap now, or within a few bars of a wick-based mitigation.
                if (IfvgEnabled && !zone.Inverted &&
                    zone.Type is ZoneType.BullFvg or ZoneType.BearFvg &&
                    (zone.State != ZoneState.Mitigated || (zone.EndBar.HasValue && zone.EndBar.Value >= flipFloorBar)))
                {
                    if (zone.Type == ZoneType.BullFvg && bodyLow < zone.Bottom)
                    {
                        zone.Inverted = true;
                        if (zone.State != ZoneState.Mitigated)
                            Mitigate(zone, bar);

                        flipped.Add(BuildFlip(zone, ZoneType.BearIfvg, bar));
                    }
                    else if (zone.Type == ZoneType.BearFvg && bodyHigh > zone.Top)
                    {
                        zone.Inverted = true;
                        if (zone.State != ZoneState.Mitigated)
                            Mitigate(zone, bar);

                        flipped.Add(BuildFlip(zone, ZoneType.BullIfvg, bar));
                    }
                }

                // BREAKER: a violated order block flips polarity the same way. A bullish
                // OB that price closes below stops being support and becomes resistance
                // on the retest (and mirrored) — the classic breaker-block reversal the
                // entry model's trap-arming already assumes exists.
                if (BreakerBlocksEnabled && !zone.BreakerSpawned &&
                    zone.Type is ZoneType.BullOrderBlock or ZoneType.BearOrderBlock &&
                    (zone.State != ZoneState.Mitigated || (zone.EndBar.HasValue && zone.EndBar.Value >= flipFloorBar)))
                {
                    if (zone.Type == ZoneType.BullOrderBlock && bodyLow < zone.Bottom)
                    {
                        zone.BreakerSpawned = true;
                        if (zone.State != ZoneState.Mitigated)
                            Mitigate(zone, bar);

                        flipped.Add(BuildFlip(zone, ZoneType.BearBreaker, bar));
                    }
                    else if (zone.Type == ZoneType.BearOrderBlock && bodyHigh > zone.Top)
                    {
                        zone.BreakerSpawned = true;
                        if (zone.State != ZoneState.Mitigated)
                            Mitigate(zone, bar);

                        flipped.Add(BuildFlip(zone, ZoneType.BullBreaker, bar));
                    }
                }
        }

        /// <summary>Commits polarity flips produced by a body-close pass and journals them.</summary>
        private void CommitFlippedZones(List<Zone> flipped, int bar, string layerNote)
        {
            foreach (var zone in flipped)
            {
                AddZone(zone, bar);

                var isBreaker = zone.Type is ZoneType.BullBreaker or ZoneType.BearBreaker;
                JournalEvent(bar, isBreaker ? "ZoneBroken" : "ZoneInverted", zone.IsBullish ? "Bull" : "Bear", zone, 0m,
                    (isBreaker
                        ? "Order block violated - flipped into a breaker"
                        : "FVG flipped polarity (body close through)") + layerNote);
            }
        }

        /// <summary>
        /// Body-close pass for ONE higher-timeframe layer, run when that layer's candle
        /// closes and judged on THAT candle's body.
        ///
        /// A 4H fair value gap is broken when a 4H candle body closes through it - not
        /// when some 15-minute candle does. Running the chart-timeframe pass over HTF
        /// zones gave a 4H gap sixteen inversion opportunities per 4H candle on an M15
        /// chart, and let a brief dip that a real 4H candle would have absorbed as a wick
        /// flip the zone permanently. That is why an M15 chart showed 4H iFVGs a genuine
        /// H4 chart never produced.
        /// </summary>
        /// <param name="chartBar">
        /// The chart bar at which this layer's candle became known to be closed. Passing
        /// <c>_lastSeenBar</c> instead was correct on the forward path and wrong during the
        /// retro-feed, where the aggregators are replayed across the whole history while that
        /// field sits at the configuration bar: every HTF zone mitigated in the replay was
        /// stamped with EndBar = the present, so it rendered to the live edge and Prune never
        /// retired it.
        /// </param>
        private void ApplyHtfBodyClose(HtfAggregator agg, int chartBar)
        {
            var n = agg.Candles.Count;
            if (n == 0)
                return;

            var closed = agg.Candles[n - 1];
            var bodyLow = Math.Min(closed.Open, closed.Close);
            var bodyHigh = Math.Max(closed.Open, closed.Close);

            // The "recently wick-mitigated" window is the same 4-candle inclusive span the
            // chart-timeframe rule uses (bar-3 .. bar), but anchored to REAL candle
            // boundaries of this layer rather than approximated as bars-per-candle. The
            // approximation drifted whenever a zone was wick-filled part-way through a
            // candle, which let an HTF gap invert on a candle a native chart of that
            // timeframe would not have counted.
            var flipFloorBar = agg.Candles[Math.Max(0, n - 4)].FirstChartBar;
            var flipped = new List<Zone>();

            foreach (var zone in _zones)
            {
                if (!zone.IsHtf || zone.HtfLabel != agg.Label)
                    continue;

                // Never judged by the candle it was born from.
                if (zone.StartBar >= closed.FirstChartBar)
                    continue;

                ApplyBodyCloseToZone(zone, bodyLow, bodyHigh, chartBar, flipFloorBar, flipped);
            }

            CommitFlippedZones(flipped, chartBar, $" [{agg.Label} candle close]");
        }

        /// <summary>
        /// The HTF equivalent of LegHasImbalance: the displacement leg on the HTF series
        /// must itself have left an unfilled 3-candle gap.
        /// </summary>
        private bool HtfLegHasImbalance(List<HtfCandle> candles, int obIndex, int lastIndex, bool bullish)
        {
            var minGap = MinFvgTicks * InstrumentTickSize;

            for (var b = obIndex + 2; b <= lastIndex; b++)
            {
                if (b - 2 < 0)
                    continue;

                var a = candles[b - 2];
                var c = candles[b];

                if (bullish)
                {
                    if (c.Low > a.High && c.Low - a.High >= minGap)
                        return true;
                }
                else
                {
                    if (c.High < a.Low && a.Low - c.High >= minGap)
                        return true;
                }
            }

            return false;
        }

        /// <summary>A flipped copy of <paramref name="source"/> covering the same territory.</summary>
        private static Zone BuildFlip(Zone source, ZoneType type, int bar) => new()
        {
            Type = type,
            IsHtf = source.IsHtf,
            HtfLabel = source.HtfLabel,
            HtfMinutes = source.HtfMinutes,
            StartBar = bar,
            Top = source.Top,
            Bottom = source.Bottom
        };

        /// <summary>
        /// Classifies sweeps that finished on this candle: closed back inside the level =
        /// TRAP (the manipulation ICT trades), closed through = RUN (a real breakout).
        ///
        /// Runs BEFORE structure detection on purpose. The entry model consults this
        /// classification when it decides whether a sweep may arm a reversal, and a sweep
        /// and the MSS that follows it can land on the SAME candle - so the verdict has to
        /// exist before DetectStructureBreak asks for it.
        /// </summary>
        private void ClassifyFinishedSweeps(int bar)
        {
            var close = GetCandle(bar).Close;

            foreach (var level in _liquidity)
            {
                if (!level.Swept || level.SweptBar != bar || level.WasTrap != null)
                    continue;

                level.WasTrap = level.BuySide ? close < level.Price : close > level.Price;
            }
        }

        private void Mitigate(Zone zone, int bar)
        {
            if (zone.State == ZoneState.Mitigated)
                return;

            zone.State = ZoneState.Mitigated;
            zone.EndBar = bar;
            MarkRenderDirty();
            JournalEvent(bar, "ZoneMitigated", zone.IsBullish ? "Bull" : "Bear", zone, 0m, "");

            // Position-management alert: the zone behind a still-open signal just
            // died — whoever entered off it should know the structural basis is gone.
            // Fires once per zone (this method is idempotent) and only in realtime.
            if (AlertOnSignalZoneInvalidated)
            {
                var affected = _openSignals.FirstOrDefault(s => !s.Resolved && s.TriggerZoneId == zone.Id);
                if (affected != null)
                    Fire($"❌ Signal zone invalidated — {zone.Tag}\n" +
                         $"📍 Zone: {FormatPrice(zone.Bottom)}–{FormatPrice(zone.Top)}\n" +
                         $"⚠️ The zone behind the open {(affected.Long ? "LONG" : "SHORT")} (signal #{affected.Id}, {affected.Tier}) has been consumed\n" +
                         $"👋 If you're still in the trade: structural basis is gone — consider exiting or tightening the stop");
            }
        }

        /// <summary>
        /// How long a mitigated zone stays in the data. HTF zones are retained for at least
        /// four candles OF THEIR OWN LAYER, so the layer's body-close pass can still reach
        /// them (flip window is three).
        /// </summary>
        private int MitigatedRetentionBars(Zone zone)
        {
            if (!zone.IsHtf || zone.HtfMinutes <= 0 || _chartMinutes <= 0)
                return KeepMitigatedBars;

            var barsPerHtfCandle = Math.Max(1, zone.HtfMinutes / _chartMinutes);
            return Math.Max(KeepMitigatedBars, 4 * barsPerHtfCandle);
        }

        private void Prune(int bar)
        {
            // Mitigated zones vanish from RENDERING immediately (unless ShowMitigated),
            // but stay in the data for KeepMitigatedBars — the iFVG/breaker engine needs
            // the broken zone for its flip window, and the journal needs the id.
            //
            // HTF zones need a LONGER stay, measured in their own candles. KeepMitigatedBars
            // is counted in CHART bars: on an M15 chart that is 10 bars = 2.5 hours, while a
            // 4H candle only closes every 16 bars. A filled 4H gap was therefore pruned
            // before its own layer's candle had a chance to close on it, so it could never
            // invert and 4H iFVGs simply never appeared.
            // A zone an unresolved signal was built on is never pruned: Mitigate() matches
            // open signals by TriggerZoneId, so dropping the zone silently disabled the
            // "signal zone invalidated" alert for exactly the trade that needed it. Signals
            // always resolve within SignalTimeoutBars, so this cannot accumulate.
            _zones.RemoveAll(z =>
                z.State == ZoneState.Mitigated &&
                bar - (z.EndBar ?? bar) > MitigatedRetentionBars(z) &&
                !IsTriggerOfOpenSignal(z.Id));

            if (_structure.Count > 150)
                _structure.RemoveRange(0, _structure.Count - 150);

            if (_swingHighs.Count > 300)
                _swingHighs.RemoveRange(0, _swingHighs.Count - 300);

            if (_swingLows.Count > 300)
                _swingLows.RemoveRange(0, _swingLows.Count - 300);

            // Swept levels must outlive their RENDER retention, or the user-facing
            // "keep swept levels visible" setting silently caps at KeepMitigatedBars.
            var sweptRetention = Math.Max(KeepMitigatedBars, SweptRetentionBars);
            _liquidity.RemoveAll(l => l.Swept && l.SweptBar.HasValue && bar - l.SweptBar.Value > sweptRetention);
        }

        private bool IsTriggerOfOpenSignal(int zoneId)
        {
            foreach (var s in _openSignals)
            {
                if (!s.Resolved && s.TriggerZoneId == zoneId)
                    return true;
            }

            return false;
        }

        #endregion

        #region Higher-timeframe bias

        /// <summary>
        /// Compact description of every configured layer's structural bias ("4H↑ D↓"),
        /// recorded on every signal whether or not the bias filter is enabled — so its
        /// predictive value is measurable from the journal before it is trusted to veto.
        /// </summary>
        private string HtfBiasText()
        {
            if (_htfAggregators.Count == 0)
                return "n/a";

            var parts = new List<string>(_htfAggregators.Count);
            foreach (var agg in _htfAggregators)
                parts.Add(agg.Label + agg.BiasGlyph);

            return string.Join(" ", parts);
        }

        /// <summary>
        /// True unless some configured HTF layer's own structure points against the trade.
        /// Layers that have not yet established a bias are neutral and never veto.
        /// </summary>
        private bool HtfBiasAllows(bool longSide, out string opposing)
        {
            opposing = "";

            if (_htfAggregators.Count == 0)
                return true;

            var want = longSide ? 1 : -1;

            foreach (var agg in _htfAggregators)
            {
                var trend = agg.Structure.Trend;
                if (trend != 0 && trend != want)
                {
                    opposing = $"{agg.Label} is {(trend == 1 ? "bullish" : "bearish")}";
                    return false;
                }
            }

            return true;
        }

        #endregion

        #region Higher timeframe framework

        /// <summary>Standard institutional HTF ladder used by Auto mode.</summary>
        private static readonly int[] HtfRungs = { 15, 60, 240, 1440, 10080 };

        /// <summary>
        /// Aggregates chart candles into HTF buckets by open time and runs the same
        /// institutional detection (FVG + displacement order blocks) on that series.
        /// HTF zones carry more weight — the book: "OBs are more powerful when
        /// aligned with higher timeframes".
        ///
        /// In Auto mode the chart timeframe is MEASURED from the data itself (mode of
        /// bar-open time deltas — no reliance on platform strings), then the HTF
        /// layer(s) are chosen from the institutional ladder 15m → 1H → 4H → D → W.
        /// Once configured, the aggregators are retro-fed the full history so HTF
        /// zones exist from the very first chart bar.
        /// </summary>
        /// <summary>
        /// Measures the chart timeframe from the data. Runs on every bar-close regardless of
        /// whether HTF mapping is enabled, because the result is needed for the alert
        /// identity ("GC 1H"), the /shot chart names and the mitigated-zone retention maths —
        /// none of which are HTF features.
        /// </summary>
        private void UpdateChartTimeframe(int bar)
        {
            if (_chartTfResolved)
                return;

            CollectBarDeltaSample(bar);

            // Resolve once we have a solid sample — or at the end of a short history.
            if (_barDeltaSamples.Count < 30 && bar < CurrentBar - 2)
                return;

            _chartMinutes = EstimateChartMinutes(out _chartTfRegular, out _chartTfApproxMinutes, out _chartTfSeconds);
            _chartTfLabel = _chartTfRegular ? DurationToLabel(_chartTfSeconds) : MinutesToLabel(_chartMinutes);
            _chartTfResolved = true;

            // The measured chart TF doubles as the alert identity and registers this chart
            // with the Telegram command hub (/shot).
            TelegramHub.Register(this);
        }

        private void UpdateHtf(int bar)
        {
            if (!HtfEnabled || !_chartTfResolved)
                return;

            if (!_htfConfigured)
            {
                ConfigureHtfLayers();
                _htfConfigured = true;

                for (var i = 1; i <= bar; i++)
                    foreach (var agg in _htfAggregators)
                        FeedAggregator(agg, i);

                return;
            }

            foreach (var agg in _htfAggregators)
                FeedAggregator(agg, bar);
        }

        private void CollectBarDeltaSample(int bar)
        {
            if (bar < 2)
                return;

            var delta = GetCandle(bar).Time - GetCandle(bar - 1).Time;
            var seconds = (long)Math.Round(delta.TotalSeconds);
            if (seconds <= 0)
                return;

            _barDeltaSamples.Add(seconds);
            _barDeltaCounts.TryGetValue(seconds, out var n);
            _barDeltaCounts[seconds] = n + 1;
        }

        /// <summary>
        /// Estimates the chart timeframe from measured bar durations.
        /// Time-based charts produce one dominant delta (session gaps are outvoted);
        /// tick/volume/range charts have irregular deltas, so the median duration is
        /// rounded UP to the next standard timeframe as a conservative basis.
        /// <paramref name="seconds"/> carries the raw measurement so sub-minute charts
        /// can still be labelled honestly instead of collapsing to "1m".
        /// </summary>
        private int EstimateChartMinutes(out bool regular, out double approxMinutes, out long seconds)
        {
            regular = true;
            approxMinutes = 1;
            seconds = 60;

            if (_barDeltaSamples.Count == 0)
                return 1;

            long modeKey = 0;
            var modeCount = 0;
            foreach (var kv in _barDeltaCounts)
            {
                if (kv.Value > modeCount)
                {
                    modeCount = kv.Value;
                    modeKey = kv.Key;
                }
            }

            if (modeCount >= _barDeltaSamples.Count / 2)
            {
                seconds = modeKey;
                approxMinutes = modeKey / 60.0;
                return Math.Max(1, (int)Math.Round(approxMinutes));
            }

            // Irregular (tick/volume/range/renko) chart.
            regular = false;
            var sorted = _barDeltaSamples.OrderBy(x => x).ToList();
            seconds = sorted[sorted.Count / 2];
            approxMinutes = seconds / 60.0;

            foreach (var std in new[] { 1, 2, 3, 5, 10, 15, 30, 60, 240, 1440 })
            {
                if (approxMinutes <= std)
                    return std;
            }

            return 1440;
        }

        /// <summary>Chart minutes → (primary, secondary) HTF from the institutional ladder.</summary>
        private static (int Primary, int Secondary) AutoLadder(int chartMinutes)
        {
            var primary = chartMinutes switch
            {
                <= 1 => 15,     // 1m  → 15m (+1H)
                <= 5 => 60,     // 2-5m → 1H (+4H)
                <= 60 => 240,   // 6m-1H → 4H (+D)
                <= 240 => 1440, // 2H-4H → D (+W)
                _ => 10080      // D+ → W
            };

            // Guarantee the HTF sits strictly above the chart timeframe.
            var idx = Array.IndexOf(HtfRungs, primary);
            while (primary <= chartMinutes && idx < HtfRungs.Length - 1)
                primary = HtfRungs[++idx];

            if (primary <= chartMinutes)
                return (0, 0);

            var secondary = idx < HtfRungs.Length - 1 ? HtfRungs[idx + 1] : 0;
            return (primary, secondary);
        }

        private static string MinutesToLabel(int minutes) => minutes switch
        {
            >= 10080 when minutes % 10080 == 0 => minutes == 10080 ? "W" : $"{minutes / 10080}W",
            >= 1440 when minutes % 1440 == 0 => minutes == 1440 ? "D" : $"{minutes / 1440}D",
            >= 60 when minutes % 60 == 0 => $"{minutes / 60}H",
            _ => $"{minutes}m"
        };

        /// <summary>Label for a MEASURED bar duration — sub-minute charts keep their seconds.</summary>
        private static string DurationToLabel(long seconds) =>
            seconds > 0 && seconds < 60 ? $"{seconds}s" : MinutesToLabel(Math.Max(1, (int)Math.Round(seconds / 60.0)));

        private void ConfigureHtfLayers()
        {
            _htfAggregators.Clear();

            // The chart timeframe is already measured by UpdateChartTimeframe.
            var chartMinutes = _chartMinutes;
            var regular = _chartTfRegular;
            var approx = _chartTfApproxMinutes;
            var chartSeconds = _chartTfSeconds;
            var layers = new List<int>();

            if (HtfMode == HtfSelectionMode.Manual)
            {
                if (HtfMinutes > chartMinutes)
                    layers.Add(HtfMinutes);
            }
            else
            {
                var (primary, secondary) = AutoLadder(chartMinutes);
                if (primary > 0)
                    layers.Add(primary);
                if (AutoSecondLayer && secondary > 0)
                    layers.Add(secondary);
            }

            foreach (var minutes in layers)
                _htfAggregators.Add(new HtfAggregator { Minutes = minutes, Label = MinutesToLabel(minutes) });

            var layerText = _htfAggregators.Count == 0
                ? "none (chart TF too high)"
                : string.Join(" + ", _htfAggregators.Select(a => a.Label));

            var measured = DurationToLabel(chartSeconds);
            var chartText = regular
                ? measured
                : $"~{approx:0.#}m/bar (irregular → {MinutesToLabel(chartMinutes)})";

            var anchorNote = DailyAnchorMode == SessionAnchorMode.Manual
                ? $"day={DailyAnchorMinutes / 60:00}:{DailyAnchorMinutes % 60:00} (manual)"
                : (EffectiveDailyAnchorMinutes >= 0 && !string.IsNullOrEmpty(_dailyAnchorInfo)
                    ? _dailyAnchorInfo
                    : "day=calendar");

            var weekNote = $"week={EffectiveWeeklyAnchorDay}" +
                           (WeeklyAnchorMode == WeekAnchorMode.Manual ? " (manual)" : " (auto)");

            _htfInfo = HtfMode == HtfSelectionMode.Manual
                ? $"HTF manual: {layerText} · chart {chartText} · {anchorNote} · {weekNote}"
                : $"HTF auto: {layerText} · chart {chartText} · {anchorNote} · {weekNote}";

            JournalEvent(_lastSeenBar, "SessionAnchor", "", null, 0m,
                $"{anchorNote}; {weekNote}; intraday anchor {IntradayAnchorMinutes}m; layers {layerText}");
        }

        /// <summary>
        /// <summary>
        /// The daily/weekly bucket anchor actually in force. In Auto mode this is measured
        /// from the data once per recalculation and cached.
        /// </summary>
        private int EffectiveDailyAnchorMinutes
        {
            get
            {
                if (DailyAnchorMode == SessionAnchorMode.Manual)
                    return DailyAnchorMinutes;

                if (_dailyAnchorResolved < 0)
                    _dailyAnchorResolved = DetectSessionAnchorMinutes();

                return _dailyAnchorResolved;
            }
        }

        /// <summary>
        /// The weekday the weekly bucket opens on.
        ///
        /// Auto infers it from whether the instrument has a session at all: a detected daily
        /// session gap means a futures-style contract, whose week opens Sunday evening;
        /// no gap means a 24/7 or cash instrument, which keeps the calendar (Monday) week.
        /// </summary>
        private DayOfWeek EffectiveWeeklyAnchorDay =>
            WeeklyAnchorMode == WeekAnchorMode.Manual
                ? WeeklyAnchorDay
                : EffectiveDailyAnchorMinutes > 0 ? DayOfWeek.Sunday : DayOfWeek.Monday;

        /// <summary>
        /// Finds where the trading day starts by looking for the recurring gap in bar
        /// timestamps — the daily maintenance break every futures contract has (GC halts
        /// 16:00–17:00 Chicago). The bar that opens immediately AFTER that gap opens the
        /// session, and its time-of-day is the anchor.
        ///
        /// Why measure instead of configure: the right value is not constant. GC's session
        /// opens 17:00 Chicago, which on a UTC+2 chart is 00:00 in US summer and 01:00 in US
        /// winter. A hand-set anchor is silently an hour wrong for half the year, and every
        /// PDH/PDL/PWH/PWL with it.
        ///
        /// Only RECENT history is scanned (about 30 days), because a longer window would
        /// straddle a daylight-saving change and mix two different boundaries together.
        ///
        /// Deliberately conservative: it needs several gaps that agree, and returns 0 (the
        /// calendar day, i.e. the previous behaviour) whenever the evidence is thin — a
        /// 24/7 instrument with no session break correctly yields 0.
        /// </summary>
        private int DetectSessionAnchorMinutes()
        {
            var last = CurrentBar - 1;
            if (last < 20)
                return 0;

            try
            {
                // Bar duration measured locally: this runs before the HTF layer has
                // measured the chart timeframe.
                var deltas = new List<double>();
                var from = Math.Max(1, last - 2000);
                for (var i = from; i <= last; i++)
                {
                    var d = (GetCandle(i).Time - GetCandle(i - 1).Time).TotalMinutes;
                    if (d > 0)
                        deltas.Add(d);
                }

                if (deltas.Count < 20)
                    return 0;

                deltas.Sort();
                var barMinutes = deltas[deltas.Count / 2];
                if (barMinutes <= 0)
                    return 0;

                // ~30 days of bars, so the window stays inside one daylight-saving regime.
                var perDay = Math.Max(1, (int)(1440.0 / barMinutes));
                var scanFrom = Math.Max(1, last - perDay * 30);
                var threshold = barMinutes * 2.0;

                var counts = new Dictionary<int, int>();
                var total = 0;

                for (var i = scanFrom; i <= last; i++)
                {
                    var open = GetCandle(i).Time;
                    if ((open - GetCandle(i - 1).Time).TotalMinutes < threshold)
                        continue;

                    // Weekend and holiday gaps reinforce the same answer: the week reopens
                    // at the session time too.
                    var minuteOfDay = open.Hour * 60 + open.Minute;
                    counts.TryGetValue(minuteOfDay, out var n);
                    counts[minuteOfDay] = n + 1;
                    total++;
                }

                if (total < 3)
                {
                    _dailyAnchorInfo = "day=calendar (no recurring session gap found)";
                    return 0;
                }

                var bestMinute = 0;
                var bestCount = 0;
                foreach (var kv in counts)
                {
                    if (kv.Value > bestCount)
                    {
                        bestCount = kv.Value;
                        bestMinute = kv.Key;
                    }
                }

                // A real session boundary dominates its gaps; scattered gaps do not.
                if (bestCount * 2 < total)
                {
                    _dailyAnchorInfo = $"day=calendar (session gaps inconsistent: best {bestCount}/{total})";
                    return 0;
                }

                _dailyAnchorInfo = bestMinute == 0
                    ? $"day=00:00 (session gap, {bestCount}/{total})"
                    : $"day={bestMinute / 60:00}:{bestMinute % 60:00} (session gap, {bestCount}/{total})";
                return bestMinute;
            }
            catch
            {
                // Series not fully available - fall back to the calendar day.
                _dailyAnchorInfo = "day=calendar (bar scan unavailable)";
                return 0;
            }
        }

        /// Bucket start for a candle open time.
        ///
        /// Bucket PHASE is not cosmetic: measured on identical price data, shifting the 4H
        /// buckets by two hours changed 100% of the detected 4H FVG boundaries. The buckets
        /// have to land where the platform's own HTF candles land.
        ///
        /// Intraday layers (15m/1H/4H) and daily-and-above layers get SEPARATE anchors,
        /// because an instrument can have clock-aligned intraday bars and a session-based
        /// daily at the same time - ATAS opens 4H candles at 00/04/08/12/16/20 regardless of
        /// the futures session. A single shared anchor would force one to break the other.
        ///
        /// Both default to 0 = clock-aligned, matching ATAS. Weekly buckets align to Monday
        /// 00:00 at anchor 0 because .NET tick zero (0001-01-01) is a Monday.
        /// </summary>
        private DateTime GetBucketStart(DateTime time, int minutes)
        {
            var anchorMinutes = minutes >= 1440 ? EffectiveDailyAnchorMinutes : IntradayAnchorMinutes;
            var anchorTicks = TimeSpan.FromMinutes(anchorMinutes).Ticks;

            // Weekly and larger buckets need a WEEKDAY phase as well as a minute-of-day one.
            // .NET tick zero (0001-01-01) is a Monday, so without this the week always opened
            // Monday at the daily anchor — which folded roughly an extra day of the current
            // week into "last week" for any instrument whose week opens Sunday evening.
            if (minutes >= 10080)
            {
                var dayOffset = ((int)EffectiveWeeklyAnchorDay - (int)DayOfWeek.Monday + 7) % 7;
                anchorTicks += TimeSpan.FromDays(dayOffset).Ticks;
            }

            var span = TimeSpan.FromMinutes(minutes).Ticks;
            var shifted = time.Ticks - anchorTicks;

            if (shifted < 0)
                shifted = 0;

            return new DateTime(shifted - shifted % span + anchorTicks);
        }

        private void FeedAggregator(HtfAggregator agg, int bar)
        {
            var candle = GetCandle(bar);
            var bucketStart = GetBucketStart(candle.Time, agg.Minutes);

            if (agg.Current == null || bucketStart > agg.Current.BucketStart)
            {
                if (agg.Current != null)
                {
                    agg.Candles.Add(agg.Current);
                    OnHtfCandleClosed(agg, bar);
                }

                agg.Current = new HtfCandle
                {
                    BucketStart = bucketStart,
                    FirstChartBar = bar,
                    Open = candle.Open,
                    High = candle.High,
                    Low = candle.Low,
                    Close = candle.Close
                };
            }
            else
            {
                agg.Current.High = Math.Max(agg.Current.High, candle.High);
                agg.Current.Low = Math.Min(agg.Current.Low, candle.Low);
                agg.Current.Close = candle.Close;
            }

            if (agg.Candles.Count > 400)
            {
                var removed = agg.Candles.Count - 400;
                agg.Candles.RemoveRange(0, removed);
                RebaseHtfSwings(agg.Structure, removed);
            }
        }

        /// <summary>
        /// Shifts this layer's stored swing pivots to follow a trim of its candle buffer.
        ///
        /// In the HTF path <see cref="SwingPoint.Bar"/> is an INDEX into
        /// <see cref="HtfAggregator.Candles"/>, not an absolute bar number as it is on the
        /// chart timeframe — so trimming the buffer silently invalidated every stored index.
        /// The pivot de-duplication check then compared a fresh index against a stale one and
        /// could suppress a genuine pivot or admit a duplicate. On a 15m layer the buffer
        /// fills in roughly four days, so this was reached in normal use.
        ///
        /// Each SwingPoint is shifted exactly once even though LastHigh/LastLow usually alias
        /// entries in the lists, and pivots whose candle is gone are dropped.
        /// </summary>
        private static void RebaseHtfSwings(HtfStructure st, int removed)
        {
            if (removed <= 0)
                return;

            var shifted = new HashSet<SwingPoint>();

            ShiftAll(st.Highs, removed, shifted);
            ShiftAll(st.Lows, removed, shifted);

            if (st.LastHigh != null && shifted.Add(st.LastHigh))
                st.LastHigh.Bar -= removed;

            if (st.LastLow != null && shifted.Add(st.LastLow))
                st.LastLow.Bar -= removed;

            static void ShiftAll(List<SwingPoint> points, int removed, HashSet<SwingPoint> shifted)
            {
                for (var i = points.Count - 1; i >= 0; i--)
                {
                    var p = points[i];

                    if (shifted.Add(p))
                        p.Bar -= removed;

                    if (p.Bar < 0)
                        points.RemoveAt(i);
                }
            }
        }

        private void OnHtfCandleClosed(HtfAggregator agg, int chartBar)
        {
            var candles = agg.Candles;
            var n = candles.Count;

            // The order below mirrors OnBarComplete exactly - ATR, swings, structure
            // break (and the order block it produces), then fair value gaps, then
            // body-close settlement. Same rules, same sequence, different series.
            UpdateHtfStructure(agg, chartBar);

            if (HtfFvgEnabled && n >= 3)
            {
                var a = candles[n - 3];
                var c = candles[n - 2];
                var b = candles[n - 1];

                // Scale the noise filter to THIS LAYER'S own range, never the chart-TF
                // ATR. A 4H gap measured against 0.15 × a 5m ATR is no filter at all —
                // every micro-imbalance qualified as an "HTF FVG", and because HTF zones
                // drive the A+/A++ confluence tier, that inflated the tiering the whole
                // analytics pipeline is built to compare.
                var layerScale = agg.AverageRange(20);
                var minSize = Math.Max(MinFvgTicks * InstrumentTickSize, layerScale * MinFvgAtrFraction);

                if (b.Low > a.High && b.Low - a.High >= minSize)
                {
                    AddZone(new Zone
                    {
                        Type = ZoneType.BullFvg,
                        IsHtf = true,
                        HtfLabel = agg.Label,
                        HtfMinutes = agg.Minutes,
                        StartBar = c.FirstChartBar,
                        Top = b.Low,
                        Bottom = a.High
                    }, chartBar);
                }
                else if (b.High < a.Low && a.Low - b.High >= minSize)
                {
                    AddZone(new Zone
                    {
                        Type = ZoneType.BearFvg,
                        IsHtf = true,
                        HtfLabel = agg.Label,
                        HtfMinutes = agg.Minutes,
                        StartBar = c.FirstChartBar,
                        Top = a.Low,
                        Bottom = b.High
                    }, chartBar);
                }
            }

            ApplyHtfBodyClose(agg, chartBar);
        }

        /// <summary>
        /// Runs the REAL market-structure engine on one higher-timeframe series.
        ///
        /// This replaces a "displacement proxy" that asked whether the last candle's range
        /// beat its 10-candle average and whether its close exceeded the highest high of
        /// the previous few candles. That second test is a Donchian rolling-extreme
        /// breakout, NOT a structure break: in any steady grind every new candle exceeds
        /// the prior five, so it fired where no swing pivot existed at all and produced
        /// order blocks a native chart of that timeframe would never draw.
        ///
        /// What runs here instead is exactly what the chart timeframe runs - Wilder ATR
        /// seeded over a full period, fractal swing confirmation with SwingPeriod bars on
        /// both sides, protected-swing tracking with counter-side re-anchoring, a close
        /// beyond the protected swing, and CreateHtfOrderBlock's magnitude + imbalance
        /// proofs - just against this layer's candles and this layer's own state.
        /// </summary>
        private void UpdateHtfStructure(HtfAggregator agg, int chartBar)
        {
            var c = agg.Candles;
            var n = c.Count;
            if (n < 2)
                return;

            var st = agg.Structure;
            var bar = n - 1;

            // --- ATR of THIS series (identical formulation to UpdateAtr) ---
            var tr = Math.Max(c[bar].High - c[bar].Low,
                Math.Max(Math.Abs(c[bar].High - c[bar - 1].Close), Math.Abs(c[bar].Low - c[bar - 1].Close)));

            if (st.AtrSamples < AtrPeriod)
            {
                st.AtrSeedSum += tr;
                st.AtrSamples++;
                st.Atr = st.AtrSeedSum / st.AtrSamples;
            }
            else
            {
                st.Atr += (tr - st.Atr) / AtrPeriod;
            }

            // --- fractal swing confirmation (identical to ConfirmSwings) ---
            var p = bar - SwingPeriod;
            if (p >= SwingPeriod)
            {
                var pivot = c[p];
                var isHigh = true;
                var isLow = true;

                for (var j = p - SwingPeriod; j <= p + SwingPeriod; j++)
                {
                    if (j == p)
                        continue;

                    if (c[j].High > pivot.High)
                        isHigh = false;
                    if (c[j].Low < pivot.Low)
                        isLow = false;
                    if (!isHigh && !isLow)
                        break;
                }

                if (isHigh && (st.Highs.Count == 0 || st.Highs[^1].Bar != p))
                {
                    var swing = new SwingPoint { Bar = p, Price = pivot.High };
                    st.Highs.Add(swing);
                    if (!UseProtectedSwings || st.LastHigh == null || st.LastHigh.Broken ||
                        swing.Price > st.LastHigh.Price)
                        st.LastHigh = swing;
                }

                if (isLow && (st.Lows.Count == 0 || st.Lows[^1].Bar != p))
                {
                    var swing = new SwingPoint { Bar = p, Price = pivot.Low };
                    st.Lows.Add(swing);
                    if (!UseProtectedSwings || st.LastLow == null || st.LastLow.Broken ||
                        swing.Price < st.LastLow.Price)
                        st.LastLow = swing;
                }
            }

            // --- structure break -> order block (identical to DetectStructureBreak) ---
            var close = c[bar].Close;

            if (st.LastHigh is { Broken: false } && close > st.LastHigh.Price)
            {
                st.LastHigh.Broken = true;

                // This layer's own structural bias. It was previously computed implicitly and
                // discarded, so the HTF engine could say a great deal about WHERE to trade
                // and nothing at all about WHICH WAY.
                st.Trend = 1;

                if (UseProtectedSwings)
                {
                    var low = st.Lows.LastOrDefault(l => !l.Broken);
                    if (low != null)
                        st.LastLow = low;
                }

                if (HtfObEnabled)
                    CreateHtfOrderBlock(agg, bar, bullish: true, chartBar);
            }
            else if (st.LastLow is { Broken: false } && close < st.LastLow.Price)
            {
                st.LastLow.Broken = true;
                st.Trend = -1;

                if (UseProtectedSwings)
                {
                    var high = st.Highs.LastOrDefault(h => !h.Broken);
                    if (high != null)
                        st.LastHigh = high;
                }

                if (HtfObEnabled)
                    CreateHtfOrderBlock(agg, bar, bullish: false, chartBar);
            }

            if (st.Highs.Count > 300)
                st.Highs.RemoveRange(0, st.Highs.Count - 300);
            if (st.Lows.Count > 300)
                st.Lows.RemoveRange(0, st.Lows.Count - 300);
        }

        /// <summary>
        /// CreateOrderBlock for a higher-timeframe series: last opposite candle before the
        /// break, magnitude against THIS layer's ATR, velocity proved by an imbalance in
        /// the leg. Same three conditions, same lookback setting, same zone construction.
        /// </summary>
        private void CreateHtfOrderBlock(HtfAggregator agg, int breakBar, bool bullish, int chartBar)
        {
            var c = agg.Candles;
            var st = agg.Structure;
            var breakClose = c[breakBar].Close;

            for (var i = breakBar - 1; i >= Math.Max(1, breakBar - ObLookback); i--)
            {
                var cand = c[i];
                var isOpposite = bullish ? cand.Close < cand.Open : cand.Close > cand.Open;
                if (!isOpposite)
                    continue;

                var impulse = bullish ? breakClose - cand.Low : cand.High - breakClose;
                if (st.Atr > 0 && impulse < st.Atr * DisplacementAtrFactor)
                    return;

                if (RequireImbalanceForOb && !HtfLegHasImbalance(c, i, breakBar, bullish))
                    return;

                AddZone(new Zone
                {
                    Type = bullish ? ZoneType.BullOrderBlock : ZoneType.BearOrderBlock,
                    IsHtf = true,
                    HtfLabel = agg.Label,
                    HtfMinutes = agg.Minutes,
                    StartBar = cand.FirstChartBar,
                    Top = ObStyle == ObZoneStyle.Body ? Math.Max(cand.Open, cand.Close) : cand.High,
                    Bottom = ObStyle == ObZoneStyle.Body ? Math.Min(cand.Open, cand.Close) : cand.Low
                }, chartBar);
                return;
            }
        }

        #endregion
    }
}
