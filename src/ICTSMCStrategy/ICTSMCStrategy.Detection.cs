using System;
using System.Collections.Generic;
using System.Linq;
using ATAS.Indicators;

namespace ICTSMC
{
    public partial class ICTSMCStrategy
    {
        /// <summary>
        /// Runs once per finalized candle. All pattern DETECTION happens here;
        /// touch/sweep/mitigation REACTION happens intrabar in ProcessIntrabar.
        /// </summary>
        private void OnBarComplete(int bar, int nextBar)
        {
            if (bar < 1)
                return;

            UpdateAtr(bar);
            ConfirmSwings(bar);
            ConfirmExternalSwings(bar);
            FinalizeLiquidityEvents(bar);
            DetectStructureBreak(bar);
            DetectExternalStructureBreak(bar);
            UpdateLegExtreme(bar);
            DetectFvg(bar);
            ApplyBodyCloseMitigation(bar);
            UpdateHtf(bar, nextBar);
            UpdateOpenSignals(bar);
            CheckOpenSignalThreats(bar);
            Prune(bar);
            FlushJournalBuffers();
        }

        #region ATR

        private void UpdateAtr(int bar)
        {
            var candle = GetCandle(bar);
            var prev = GetCandle(bar - 1);

            var tr = Math.Max(candle.High - candle.Low,
                Math.Max(Math.Abs(candle.High - prev.Close), Math.Abs(candle.Low - prev.Close)));

            if (!_atrSeeded)
            {
                _atr = tr;
                _atrSeeded = true;
            }
            else
            {
                _atr += (tr - _atr) / AtrPeriod; // Wilder smoothing
            }
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
                _lastSwingHigh = swing;
                RegisterLiquidity(swing, buySide: true);
            }

            if (isLow && (_swingLows.Count == 0 || _swingLows[^1].Bar != p))
            {
                var swing = new SwingPoint { Bar = p, Price = pivot.Low };
                _swingLows.Add(swing);
                _lastSwingLow = swing;
                RegisterLiquidity(swing, buySide: false);
            }
        }

        /// <summary>
        /// Every confirmed swing high leaves buy-stops resting above it (BSL),
        /// every swing low leaves sell-stops below (SSL). Near-equal levels are
        /// merged into a single, stronger "equal highs/lows" pool.
        /// </summary>
        private void RegisterLiquidity(SwingPoint swing, bool buySide)
        {
            var tolerance = EqualLevelTicks * TickSize;

            var existing = _liquidity.FirstOrDefault(l =>
                !l.Swept && l.BuySide == buySide && Math.Abs(l.Price - swing.Price) <= tolerance);

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
                Id = ++_nextLiquidityId,
                Price = swing.Price,
                StartBar = swing.Bar,
                BuySide = buySide
            });

            // Keep only the most recent unswept levels per side.
            var side = _liquidity.Where(l => l.BuySide == buySide && !l.Swept)
                                 .OrderByDescending(l => l.StartBar)
                                 .ToList();

            foreach (var stale in side.Skip(MaxLiquidityPerSide))
                _liquidity.Remove(stale);
        }

        /// <summary>
        /// External swings are intentionally slower than the visual/internal swing
        /// layer. Only breaks of these protected levels may arm the strict model.
        /// The existing internal layer remains intact for the familiar chart labels.
        /// </summary>
        private void ConfirmExternalSwings(int bar)
        {
            var p = bar - ExternalSwingPeriod;
            if (p < ExternalSwingPeriod)
                return;

            var pivot = GetCandle(p);
            var isHigh = true;
            var isLow = true;

            for (var j = p - ExternalSwingPeriod; j <= p + ExternalSwingPeriod; j++)
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

            if (isHigh && (_externalSwingHighs.Count == 0 || _externalSwingHighs[^1].Bar != p))
            {
                var swing = new SwingPoint { Bar = p, Price = pivot.High };
                _externalSwingHighs.Add(swing);
                _lastExternalSwingHigh = swing;
            }

            if (isLow && (_externalSwingLows.Count == 0 || _externalSwingLows[^1].Bar != p))
            {
                var swing = new SwingPoint { Bar = p, Price = pivot.Low };
                _externalSwingLows.Add(swing);
                _lastExternalSwingLow = swing;
            }
        }

        /// <summary>
        /// A wick through liquidity is only an observation. The completed candle
        /// decides whether it reclaimed the level (trap) or accepted beyond it
        /// (run); runs can never arm a strict reversal setup.
        /// </summary>
        private void FinalizeLiquidityEvents(int bar)
        {
            var candle = GetCandle(bar);

            foreach (var evt in _liquidityEvents.Where(e =>
                         e.Disposition == LiquidityDisposition.TakenPendingClose && e.TakenBar == bar).ToList())
            {
                var level = _liquidity.FirstOrDefault(l => l.Id == evt.LiquidityLevelId);
                if (level == null)
                {
                    evt.Disposition = LiquidityDisposition.Indeterminate;
                    evt.ClassifiedBar = bar;
                    continue;
                }

                evt.MaximumPenetration = evt.BuySide
                    ? Math.Max(0m, candle.High - evt.Level)
                    : Math.Max(0m, evt.Level - candle.Low);
                evt.Disposition = StrictRules.ClassifyLiquidity(evt.BuySide, candle.High, candle.Low, candle.Close,
                    evt.Level, TickSize, MinimumSweepPenetrationTicks, SweepReclaimTicks);
                evt.ClassifiedBar = bar;
                level.WasTrap = evt.Disposition == LiquidityDisposition.ConfirmedTrap
                    ? true
                    : evt.Disposition == LiquidityDisposition.Run ? false : null;

                var kind = evt.Disposition switch
                {
                    LiquidityDisposition.ConfirmedTrap => "Trap",
                    LiquidityDisposition.Run => "Run",
                    _ => "Indeterminate"
                };
                JournalEvent(bar, "LiquidityClassified", evt.BuySide ? "BuySide" : "SellSide", null, evt.Level,
                    $"{kind}; penetration={FormatPrice(evt.MaximumPenetration)}; close={FormatPrice(candle.Close)}; " +
                    $"reclaim={SweepReclaimTicks} ticks");

                if (evt.Disposition == LiquidityDisposition.ConfirmedTrap)
                    CreateAwaitingStrictSetup(evt, bar);
            }
        }

        private void CreateAwaitingStrictSetup(LiquidityEvent evt, int bar)
        {
            var existing = evt.LongSetup ? _bullStrictSetup : _bearStrictSetup;
            if (existing is { Status: SetupStatus.AwaitingMss or SetupStatus.Armed })
            {
                existing.Status = SetupStatus.Invalidated;
                existing.InvalidationReason = "Superseded by newer confirmed liquidity trap";
                JournalEvent(bar, "SetupInvalidated", existing.Long ? "Bull" : "Bear", null, evt.Level,
                    $"setup #{existing.Id} superseded by confirmed trap event #{evt.Id}");
            }

            var setup = new StrictSetup
            {
                Id = ++_nextStrictSetupId,
                Long = evt.LongSetup,
                LiquidityEventId = evt.Id,
                CreatedBar = bar,
                ArmedBar = -1,
                ExpiresBar = bar + SweepToMssWindow,
                Status = SetupStatus.AwaitingMss
            };

            if (setup.Long)
                _bullStrictSetup = setup;
            else
                _bearStrictSetup = setup;

            JournalEvent(bar, "SetupAwaitingMSS", setup.Long ? "Bull" : "Bear", null, evt.Level,
                $"setup #{setup.Id}; trap event #{evt.Id}; expires at bar {setup.ExpiresBar}");
        }

        private LiquidityEvent FindLatestConfirmedTrap(bool longSide, int bar) =>
            _liquidityEvents.Where(e => e.LongSetup == longSide &&
                                        e.Disposition == LiquidityDisposition.ConfirmedTrap &&
                                        bar - e.TakenBar <= SweepToMssWindow)
                            .OrderByDescending(e => e.TakenBar)
                            .FirstOrDefault();

        #endregion

        #region Structure (BoS / MSS) + order blocks

        private void DetectStructureBreak(int bar)
        {
            var close = GetCandle(bar).Close;

            if (_lastSwingHigh is { Broken: false } && close > _lastSwingHigh.Price)
            {
                _lastSwingHigh.Broken = true;
                var isMss = _trend == -1;
                _trend = 1;

                var evt = new StructureEvent
                {
                    Id = ++_nextStructureEventId,
                    Bar = bar,
                    FromBar = _lastSwingHigh.Bar,
                    Level = _lastSwingHigh.Price,
                    Bullish = true,
                    IsMss = isMss,
                    Scope = StructureScope.Internal
                };
                _structure.Add(evt);
                AnchorLeg(evt);
                OnStructureEvent(evt);

                if (DetectLtfOb)
                    CreateOrderBlock(bar, bullish: true);
            }

            if (_lastSwingLow is { Broken: false } && close < _lastSwingLow.Price)
            {
                _lastSwingLow.Broken = true;
                var isMss = _trend == 1;
                _trend = -1;

                var evt = new StructureEvent
                {
                    Id = ++_nextStructureEventId,
                    Bar = bar,
                    FromBar = _lastSwingLow.Bar,
                    Level = _lastSwingLow.Price,
                    Bullish = false,
                    IsMss = isMss,
                    Scope = StructureScope.Internal
                };
                _structure.Add(evt);
                AnchorLeg(evt);
                OnStructureEvent(evt);

                if (DetectLtfOb)
                    CreateOrderBlock(bar, bullish: false);
            }
        }

        /// <summary>
        /// Strict entries use a second, slower structure layer. Internal labels keep
        /// their V1 chart role, while an external displaced break of a protected
        /// swing is the only event allowed to arm a strict reversal setup.
        /// </summary>
        private void DetectExternalStructureBreak(int bar)
        {
            var candle = GetCandle(bar);
            var buffer = ExternalBreakBufferTicks * TickSize;
            var displaced = _atr <= 0m || candle.High - candle.Low >= _atr * DisplacementAtrFactor;
            if (!displaced)
                return;

            if (_lastExternalSwingHigh is { Broken: false } && candle.Close > _lastExternalSwingHigh.Price + buffer)
            {
                _lastExternalSwingHigh.Broken = true;
                var evt = new StructureEvent
                {
                    Id = ++_nextStructureEventId,
                    Bar = bar,
                    FromBar = _lastExternalSwingHigh.Bar,
                    Level = _lastExternalSwingHigh.Price,
                    Bullish = true,
                    IsMss = _externalTrend == -1,
                    Scope = StructureScope.External
                };
                _externalTrend = 1;
                _externalStructure.Add(evt);
                OnExternalStructureEvent(evt);
            }

            if (_lastExternalSwingLow is { Broken: false } && candle.Close < _lastExternalSwingLow.Price - buffer)
            {
                _lastExternalSwingLow.Broken = true;
                var evt = new StructureEvent
                {
                    Id = ++_nextStructureEventId,
                    Bar = bar,
                    FromBar = _lastExternalSwingLow.Bar,
                    Level = _lastExternalSwingLow.Price,
                    Bullish = false,
                    IsMss = _externalTrend == 1,
                    Scope = StructureScope.External
                };
                _externalTrend = -1;
                _externalStructure.Add(evt);
                OnExternalStructureEvent(evt);
            }
        }

        private void OnExternalStructureEvent(StructureEvent evt)
        {
            JournalEvent(evt.Bar, evt.IsMss ? "ExternalMSS" : "ExternalBoS",
                evt.Bullish ? "Bull" : "Bear", null, evt.Level,
                $"protected swing from bar {evt.FromBar}; displacement-qualified");

            var opposite = evt.Bullish ? _bearStrictSetup : _bullStrictSetup;
            if (CancelOnOppositeMss && evt.IsMss && opposite is { Status: SetupStatus.AwaitingMss or SetupStatus.Armed })
            {
                opposite.Status = SetupStatus.Invalidated;
                opposite.InvalidationReason = "Opposite external structure break";
                JournalEvent(evt.Bar, "SetupInvalidated", opposite.Long ? "Bull" : "Bear", null, evt.Level,
                    $"setup #{opposite.Id}; opposite external {(evt.IsMss ? "MSS" : "BoS")}");
            }

            if (!EntryModelEnabled || !evt.IsMss)
                return;

            var setup = evt.Bullish ? _bullStrictSetup : _bearStrictSetup;
            var trap = FindLatestConfirmedTrap(evt.Bullish, evt.Bar);
            if (RequireSweepForEntry && (setup == null || setup.Status != SetupStatus.AwaitingMss || trap == null))
            {
                JournalEvent(evt.Bar, "ArmRejected", evt.Bullish ? "Bull" : "Bear", null, evt.Level,
                    "Strict model requires a still-valid confirmed liquidity trap before external MSS");
                return;
            }

            if (setup == null || setup.Status != SetupStatus.AwaitingMss)
            {
                setup = new StrictSetup
                {
                    Id = ++_nextStrictSetupId,
                    Long = evt.Bullish,
                    LiquidityEventId = trap?.Id ?? 0,
                    CreatedBar = evt.Bar,
                    Status = SetupStatus.AwaitingMss
                };
                if (setup.Long) _bullStrictSetup = setup; else _bearStrictSetup = setup;
            }

            if (!TryGetExternalDealingRange(evt, out var high, out var low, out var highBar, out var lowBar))
            {
                if (RequireConfirmedRangeForEntry)
                {
                    setup.Status = SetupStatus.Invalidated;
                    setup.InvalidationReason = "No confirmed external dealing range";
                    JournalEvent(evt.Bar, "ArmRejected", evt.Bullish ? "Bull" : "Bear", null, evt.Level,
                        "Strict model requires a confirmed external dealing range");
                    return;
                }

                // Relaxed mode remains explicit: use the current displaced candle
                // rather than quietly accepting a missing/unbounded range.
                var candle = GetCandle(evt.Bar);
                high = candle.High;
                low = candle.Low;
                highBar = evt.Bar;
                lowBar = evt.Bar;
            }

            setup.Status = SetupStatus.Armed;
            setup.ArmedBar = evt.Bar;
            setup.ExpiresBar = evt.Bar + ArmWindowBars;
            setup.MssStructureEventId = evt.Id;
            setup.RangeHigh = high;
            setup.RangeLow = low;
            setup.RangeHighBar = highBar;
            setup.RangeLowBar = lowBar;

            // The only strict LTF POIs eligible immediately are zones objectively
            // confirmed by this same displacement bar. FVGs formed in the next two
            // completed candles are linked by AddZone while the setup remains armed.
            foreach (var zone in _zones.Where(z => z.ConfirmedBar == evt.Bar && z.IsBullish == evt.Bullish && !z.IsHtf))
            {
                zone.ConfirmingStructureBar = evt.Id;
                setup.EligiblePoiIds.Add(zone.Id);
            }

            JournalEvent(evt.Bar, "Armed", evt.Bullish ? "Bull" : "Bear", null, evt.Level,
                $"strict setup #{setup.Id}; external MSS; trap event #{setup.LiquidityEventId}; " +
                $"range={FormatPrice(low)}-{FormatPrice(high)}; expires at bar {setup.ExpiresBar}");
        }

        private bool TryGetExternalDealingRange(StructureEvent evt, out decimal high, out decimal low, out int highBar, out int lowBar)
        {
            high = 0m;
            low = 0m;
            highBar = 0;
            lowBar = 0;

            var anchor = evt.Bullish ? _lastExternalSwingLow : _lastExternalSwingHigh;
            if (anchor == null || anchor.Bar >= evt.Bar)
                return false;

            high = GetCandle(anchor.Bar).High;
            low = GetCandle(anchor.Bar).Low;
            highBar = anchor.Bar;
            lowBar = anchor.Bar;

            for (var i = anchor.Bar; i <= evt.Bar; i++)
            {
                var candle = GetCandle(i);
                if (candle.High >= high) { high = candle.High; highBar = i; }
                if (candle.Low <= low) { low = candle.Low; lowBar = i; }
            }

            return high > low;
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
        /// We only accept it when the move actually displaced (broke structure with
        /// range ≥ ATR × factor), which filters out weak, low-quality blocks.
        /// </summary>
        private void CreateOrderBlock(int breakBar, bool bullish)
        {
            var breakClose = GetCandle(breakBar).Close;

            for (var i = breakBar - 1; i >= Math.Max(1, breakBar - ObLookback); i--)
            {
                var c = GetCandle(i);
                var isOpposite = bullish ? c.Close < c.Open : c.Close > c.Open;
                if (!isOpposite)
                    continue;

                // Displacement filter: the impulse away from the OB candle must be meaningful.
                var impulse = bullish ? breakClose - c.Low : c.High - breakClose;
                if (_atr > 0 && impulse < _atr * DisplacementAtrFactor)
                    return;

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
                    ConfirmedBar = breakBar,
                    EligibleFromBar = breakBar + 1,
                    Top = top,
                    Bottom = bottom
                });
                return;
            }
        }

        #endregion

        #region FVG

        /// <summary>
        /// 3-candle imbalance finalized at <paramref name="bar"/>:
        /// bullish when Low[bar] gaps above High[bar-2], bearish when High[bar]
        /// gaps below Low[bar-2].
        /// </summary>
        private void DetectFvg(int bar)
        {
            if (!DetectLtfFvg || bar < 2)
                return;

            var c0 = GetCandle(bar - 2);
            var c2 = GetCandle(bar);

            var minSize = Math.Max(MinFvgTicks * TickSize, _atr * MinFvgAtrFraction);

            if (c2.Low > c0.High && c2.Low - c0.High >= minSize)
            {
                AddZone(new Zone
                {
                    Type = ZoneType.BullFvg,
                    StartBar = bar - 1,
                    ConfirmedBar = bar,
                    EligibleFromBar = bar + 1,
                    Top = c2.Low,
                    Bottom = c0.High
                });
            }
            else if (c2.High < c0.Low && c0.Low - c2.High >= minSize)
            {
                AddZone(new Zone
                {
                    Type = ZoneType.BearFvg,
                    StartBar = bar - 1,
                    ConfirmedBar = bar,
                    EligibleFromBar = bar + 1,
                    Top = c0.Low,
                    Bottom = c2.High
                });
            }
        }

        #endregion

        #region Zone bookkeeping

        private Zone AddZone(Zone zone)
        {
            if (zone.ConfirmedBar <= 0)
                zone.ConfirmedBar = zone.StartBar;
            if (zone.EligibleFromBar <= 0)
                zone.EligibleFromBar = zone.ConfirmedBar + 1;

            // Skip duplicates: an active zone of the same type covering the same territory.
            var existing = _zones.FirstOrDefault(z =>
                z.State != ZoneState.Mitigated &&
                z.Type == zone.Type &&
                z.IsHtf == zone.IsHtf &&
                z.HtfLabel == zone.HtfLabel &&
                zone.Top >= z.Bottom && zone.Bottom <= z.Top);

            if (existing != null)
            {
                // A later objective confirmation can only improve the existing
                // decision metadata; geometry and visual origin remain untouched.
                existing.ConfirmedBar = Math.Min(existing.ConfirmedBar, zone.ConfirmedBar);
                existing.EligibleFromBar = Math.Min(existing.EligibleFromBar, zone.EligibleFromBar);
                LinkZoneToArmedSetup(existing);
                return existing;
            }

            zone.Id = ++_nextZoneId;
            _zones.Add(zone);
            OnZoneCreated(zone);
            JournalEvent(zone.ConfirmedBar, "ZoneCreated", zone.IsBullish ? "Bull" : "Bear", zone, 0m,
                $"origin bar {zone.StartBar}; eligible from bar {zone.EligibleFromBar}");

            var sameType = (zone.IsHtf
                    ? _zones.Where(z => z.IsHtf && z.HtfLabel == zone.HtfLabel && z.State != ZoneState.Mitigated)
                    : _zones.Where(z => z.Type == zone.Type && !z.IsHtf && z.State != ZoneState.Mitigated))
                .OrderByDescending(z => z.ConfirmedBar)
                .ToList();

            var cap = zone.IsHtf ? MaxHtfZones : MaxZonesPerType;
            foreach (var stale in sameType.Skip(cap))
                _zones.Remove(stale);

            LinkZoneToArmedSetup(zone);
            return zone;
        }

        private void LinkZoneToArmedSetup(Zone zone)
        {
            var setup = zone.IsBullish ? _bullStrictSetup : _bearStrictSetup;
            if (setup is not { Status: SetupStatus.Armed })
                return;
            if (zone.ConfirmedBar < setup.ArmedBar || zone.ConfirmedBar > setup.ArmedBar + 2)
                return;
            if (zone.IsHtf && !HtfZonesAsExecutionPoi)
                return;
            if (!zone.IsHtf && !IsChartZoneFamilyAllowedForStrictEntry(zone))
                return;

            zone.ConfirmingStructureBar = setup.MssStructureEventId;
            setup.EligiblePoiIds.Add(zone.Id);
            JournalEvent(zone.ConfirmedBar, "PoiLinked", zone.IsBullish ? "Bull" : "Bear", zone, 0m,
                $"strict setup #{setup.Id}; external MSS #{setup.MssStructureEventId}");
        }

        /// <summary>
        /// Body-close logic can only be judged on a finalized candle. Two jobs here:
        /// BodyClose-rule mitigation, and Inversion-FVG creation — a candle body
        /// closing through a fair value gap flips its polarity (failed bullish gap
        /// becomes resistance, failed bearish gap becomes support). The trapped
        /// traders inside the broken gap are the fuel of the new zone.
        /// </summary>
        private void ApplyBodyCloseMitigation(int bar)
        {
            var candle = GetCandle(bar);
            var inversions = new List<Zone>();

            foreach (var zone in _zones)
            {
                if (zone.ConfirmedBar >= bar)
                    continue;

                // HTF BodyClose and IFVG conversion are evaluated only on the
                // source timeframe in OnHtfCandleClosed. LTF touch rules may still
                // mitigate an HTF POI intrabar when explicitly selected.
                if (zone.IsHtf)
                    continue;

                var rule = zone.IsOrderBlock ? ObMitigation : FvgMitigation;
                if (zone.State != ZoneState.Mitigated && rule == MitigationRule.BodyClose)
                {
                    if (StrictRules.IsBodyCloseInvalidated(zone.IsBullish, candle.Close, zone.Top, zone.Bottom))
                        Mitigate(zone, bar, "BodyClose");
                }

                // IFVG: only plain FVGs invert (an inversion never re-inverts), and only
                // around the moment they are actually broken — a body close through the
                // gap now, or within a few bars of a wick-based mitigation.
                if (IfvgEnabled && !zone.Inverted &&
                    zone.Type is ZoneType.BullFvg or ZoneType.BearFvg &&
                    (zone.State != ZoneState.Mitigated || (zone.EndBar.HasValue && bar - zone.EndBar.Value <= 3)))
                {
                    if (zone.Type == ZoneType.BullFvg && StrictRules.IsBodyCloseInvalidated(true, candle.Close, zone.Top, zone.Bottom))
                    {
                        zone.Inverted = true;
                        if (zone.State != ZoneState.Mitigated)
                            Mitigate(zone, bar, "IFVG body-close conversion");

                        inversions.Add(new Zone
                        {
                            Type = ZoneType.BearIfvg,
                            IsHtf = zone.IsHtf,
                            HtfLabel = zone.HtfLabel,
                            HtfMinutes = zone.HtfMinutes,
                            StartBar = bar,
                            ConfirmedBar = bar,
                            EligibleFromBar = bar + 1,
                            Top = zone.Top,
                            Bottom = zone.Bottom
                        });
                    }
                    else if (zone.Type == ZoneType.BearFvg && StrictRules.IsBodyCloseInvalidated(false, candle.Close, zone.Top, zone.Bottom))
                    {
                        zone.Inverted = true;
                        if (zone.State != ZoneState.Mitigated)
                            Mitigate(zone, bar, "IFVG body-close conversion");

                        inversions.Add(new Zone
                        {
                            Type = ZoneType.BullIfvg,
                            IsHtf = zone.IsHtf,
                            HtfLabel = zone.HtfLabel,
                            HtfMinutes = zone.HtfMinutes,
                            StartBar = bar,
                            Top = zone.Top,
                            Bottom = zone.Bottom
                        });
                    }
                }
            }

            foreach (var inversion in inversions)
            {
                AddZone(inversion);
                JournalEvent(bar, "ZoneInverted", inversion.IsBullish ? "Bull" : "Bear", inversion, 0m,
                    "FVG flipped polarity (body close through)");
            }

        }

        private void Mitigate(Zone zone, int bar, string reason = "")
        {
            if (zone.State == ZoneState.Mitigated)
                return;

            zone.State = ZoneState.Mitigated;
            zone.EndBar = bar;
            zone.MitigationReason = reason;
            JournalEvent(bar, "ZoneMitigated", zone.IsBullish ? "Bull" : "Bear", zone, 0m, reason);

            // Position-management alert: the zone behind a still-open signal just
            // died — whoever entered off it should know the structural basis is gone.
            // Fires once per zone (this method is idempotent) and only in realtime.
            if (AlertOnSignalZoneInvalidated)
            {
                // A first-touch entry and an AnyTouch/Midline consumption can be
                // one valid atomic event. Do not immediately warn that the just-
                // filled signal's own trigger "died" on its fill bar.
                var affected = _openSignals.FirstOrDefault(s => !s.Resolved && s.TriggerZoneId == zone.Id &&
                                                                 s.FillBar < bar);
                if (affected != null)
                    Fire($"❌ Signal zone invalidated — {zone.Tag}\n" +
                         $"📍 Zone: {FormatPrice(zone.Bottom)}–{FormatPrice(zone.Top)}\n" +
                         $"⚠️ The zone behind the open {(affected.Long ? "LONG" : "SHORT")} (signal #{affected.Id}, {affected.Tier}) has been consumed\n" +
                         $"👋 If you're still in the trade: structural basis is gone — consider exiting or tightening the stop");
            }
        }

        private void Prune(int bar)
        {
            // Mitigated zones vanish from RENDERING immediately (unless ShowMitigated),
            // but stay in the data for KeepMitigatedBars — the iFVG engine needs the
            // broken gap for its 3-bar inversion window, and the journal needs the id.
            _zones.RemoveAll(z =>
                z.State == ZoneState.Mitigated &&
                bar - (z.EndBar ?? bar) > KeepMitigatedBars);

            if (_structure.Count > 150)
                _structure.RemoveRange(0, _structure.Count - 150);

            if (_swingHighs.Count > 300)
                _swingHighs.RemoveRange(0, _swingHighs.Count - 300);

            if (_swingLows.Count > 300)
                _swingLows.RemoveRange(0, _swingLows.Count - 300);

            if (_externalStructure.Count > 150)
                _externalStructure.RemoveRange(0, _externalStructure.Count - 150);

            if (_externalSwingHighs.Count > 300)
                _externalSwingHighs.RemoveRange(0, _externalSwingHighs.Count - 300);

            if (_externalSwingLows.Count > 300)
                _externalSwingLows.RemoveRange(0, _externalSwingLows.Count - 300);

            _liquidity.RemoveAll(l => l.Swept && l.SweptBar.HasValue && bar - l.SweptBar.Value > SweptRetentionBars);

            var retainEventsFor = Math.Max(SweptRetentionBars, SweepToMssWindow + ArmWindowBars + 5);
            var protectedEventIds = new HashSet<int>(new[]
            {
                _bullStrictSetup?.LiquidityEventId ?? 0,
                _bearStrictSetup?.LiquidityEventId ?? 0
            });
            _liquidityEvents.RemoveAll(e => bar - e.TakenBar > retainEventsFor && !protectedEventIds.Contains(e.Id));
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
        private void UpdateHtf(int bar, int nextBar)
        {
            if (!HtfEnabled)
                return;

            if (!_htfConfigured)
            {
                CollectBarDeltaSample(bar);

                // Configure once we have a solid sample — or at the end of a short history.
                if (_barDeltaSamples.Count >= 30 || bar >= CurrentBar - 2)
                {
                    ConfigureHtfLayers();
                    _htfConfigured = true;

                    // During initial configuration the chart-TF engine has already
                    // processed earlier bars. Rebuild HTF strictly in chronological
                    // order, inject each closed bucket at its confirmation bar, and
                    // reconcile every historical HTF POI before it can become live.
                    for (var i = 1; i <= bar; i++)
                    {
                        var followingBar = i < bar ? i + 1 : nextBar;
                        foreach (var agg in _htfAggregators)
                            FeedAggregator(agg, i, followingBar);
                        ReconcileHistoricalHtfZonesThrough(i);
                    }
                }

                return;
            }

            foreach (var agg in _htfAggregators)
                FeedAggregator(agg, bar, nextBar);
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
        /// Estimates the chart timeframe in minutes from measured bar durations.
        /// Time-based charts produce one dominant delta (session gaps are outvoted);
        /// tick/volume/range charts have irregular deltas, so the median duration is
        /// rounded UP to the next standard timeframe as a conservative basis.
        /// </summary>
        private int EstimateChartMinutes(out bool regular, out double approxMinutes)
        {
            regular = true;
            approxMinutes = 1;

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
                approxMinutes = modeKey / 60.0;
                return Math.Max(1, (int)Math.Round(approxMinutes));
            }

            // Irregular (tick/volume/range/renko) chart.
            regular = false;
            var sorted = _barDeltaSamples.OrderBy(x => x).ToList();
            approxMinutes = sorted[sorted.Count / 2] / 60.0;

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
                <= 60 => 240,   // 15m-1H → 4H (+D)
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

        private void ConfigureHtfLayers()
        {
            _htfAggregators.Clear();
            _htfSyntheticUnsafe = false;

            var chartMinutes = EstimateChartMinutes(out var regular, out var approx);
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

            // A synthetic HTF candle is only trustworthy when every chart candle
            // lies wholly inside exactly one HTF bucket. Do not silently generate
            // signal-driving POIs from range/tick/volume charts or non-divisible
            // timeframes unless the user explicitly accepts that approximation.
            if (!regular && !AllowSyntheticHtfOnIrregularCharts)
            {
                _htfSyntheticUnsafe = true;
                layers.Clear();
            }

            foreach (var minutes in layers.Distinct())
            {
                if (minutes <= chartMinutes)
                    continue;

                if (!AllowSyntheticHtfOnIrregularCharts && minutes % chartMinutes != 0)
                {
                    _htfSyntheticUnsafe = true;
                    continue;
                }

                _htfAggregators.Add(new HtfAggregator { Minutes = minutes, Label = MinutesToLabel(minutes) });
            }

            var layerText = _htfAggregators.Count == 0
                ? "none (chart TF too high)"
                : string.Join(" + ", _htfAggregators.Select(a => a.Label));

            var chartText = regular
                ? MinutesToLabel(chartMinutes)
                : $"~{approx:0.#}m/bar (irregular → {MinutesToLabel(chartMinutes)})";

            _htfInfo = HtfMode == HtfSelectionMode.Manual
                ? $"HTF manual: {layerText} · chart {chartText}"
                : $"HTF auto: {layerText} · chart {chartText}";

            if (_htfSyntheticUnsafe)
                _htfInfo += " · synthetic HTF disabled (irregular/non-aligned chart)";

            // The measured chart TF doubles as the alert identity ("GC 1H") and
            // registers this chart with the Telegram command hub (/shot).
            _chartTfLabel = MinutesToLabel(chartMinutes);
            TelegramHub.Register(this);
        }

        /// <summary>
        /// Bucket start for a candle open time. Buckets are anchored to midnight
        /// (plus the configurable session anchor for daily-and-above layers, e.g.
        /// 18:00 ET futures session opens). Weekly buckets align to Monday 00:00
        /// because .NET tick zero (0001-01-01) is a Monday.
        /// </summary>
        private DateTime GetBucketStart(DateTime time, int minutes)
        {
            // A daily session offset is meaningful for daily buckets only. Weekly
            // buckets remain Monday-aligned and are not silently shifted by a
            // daily futures-session setting.
            var anchorTicks = minutes >= 1440 && minutes < 10080
                ? TimeSpan.FromMinutes(DailyAnchorMinutes).Ticks
                : 0L;
            var span = TimeSpan.FromMinutes(minutes).Ticks;
            var shifted = time.Ticks - anchorTicks;

            if (shifted < 0)
                shifted = 0;

            return new DateTime(shifted - shifted % span + anchorTicks);
        }

        /// <summary>
        /// Feeds one completed chart candle. The following chart bar is supplied so
        /// an HTF bucket is closed and injected before the first observation of the
        /// new chart bar, instead of one chart bar late.
        /// </summary>
        private void FeedAggregator(HtfAggregator agg, int bar, int nextBar)
        {
            var candle = GetCandle(bar);
            var bucketStart = GetBucketStart(candle.Time, agg.Minutes);

            if (agg.Current == null)
            {
                agg.Current = NewHtfCandle(bucketStart, bar, candle.Open, candle.High, candle.Low, candle.Close);
            }
            else if (bucketStart != agg.Current.BucketStart)
            {
                // Defensive path for a discontinuity discovered while replaying.
                // The current completed bar is the first reliable confirmation
                // point available in that case.
                CloseHtfCurrent(agg, bar);
                agg.Current = NewHtfCandle(bucketStart, bar, candle.Open, candle.High, candle.Low, candle.Close);
            }
            else
            {
                agg.Current.High = Math.Max(agg.Current.High, candle.High);
                agg.Current.Low = Math.Min(agg.Current.Low, candle.Low);
                agg.Current.Close = candle.Close;
                agg.Current.LastChartBar = bar;
            }

            if (nextBar <= bar)
                return;

            var nextBucket = GetBucketStart(GetCandle(nextBar).Time, agg.Minutes);
            if (nextBucket != agg.Current.BucketStart)
                CloseHtfCurrent(agg, nextBar);
        }

        private static HtfCandle NewHtfCandle(DateTime bucketStart, int chartBar,
            decimal open, decimal high, decimal low, decimal close) => new()
        {
            BucketStart = bucketStart,
            FirstChartBar = chartBar,
            LastChartBar = chartBar,
            Open = open,
            High = high,
            Low = low,
            Close = close
        };

        private void CloseHtfCurrent(HtfAggregator agg, int confirmedChartBar)
        {
            if (agg.Current == null)
                return;

            agg.Candles.Add(agg.Current);
            agg.Current = null;
            OnHtfCandleClosed(agg, confirmedChartBar);
            TrimHtfHistory(agg);
        }

        private void TrimHtfHistory(HtfAggregator agg)
        {
            const int maxCandles = 500;
            if (agg.Candles.Count <= maxCandles)
                return;

            var removed = agg.Candles.Count - maxCandles;
            agg.Candles.RemoveRange(0, removed);
            agg.SwingHighs.RemoveAll(s => s.Bar < removed);
            agg.SwingLows.RemoveAll(s => s.Bar < removed);
            foreach (var swing in agg.SwingHighs)
                swing.Bar -= removed;
            foreach (var swing in agg.SwingLows)
                swing.Bar -= removed;
            agg.LastSwingHigh = agg.SwingHighs.LastOrDefault();
            agg.LastSwingLow = agg.SwingLows.LastOrDefault();
        }

        private void OnHtfCandleClosed(HtfAggregator agg, int confirmedChartBar)
        {
            UpdateHtfAtr(agg);
            ConfirmHtfSwings(agg);
            var structure = DetectHtfStructureBreak(agg);
            if (structure != null)
            {
                JournalEvent(confirmedChartBar, structure.IsMss ? "HTF MSS" : "HTF BoS",
                    structure.Bullish ? "Bull" : "Bear", null, structure.Level,
                    $"{agg.Label}; protected HTF swing {structure.FromBar}; displacement-qualified");
            }
            DetectHtfFvg(agg, confirmedChartBar);
            if (structure != null)
                CreateHtfOrderBlock(agg, structure, confirmedChartBar);
            ApplyHtfBodyCloseMitigation(agg, confirmedChartBar);
        }

        private void UpdateHtfAtr(HtfAggregator agg)
        {
            var candles = agg.Candles;
            if (candles.Count == 0)
                return;

            var last = candles[^1];
            var tr = last.High - last.Low;
            if (candles.Count > 1)
            {
                var previousClose = candles[^2].Close;
                tr = Math.Max(tr, Math.Max(Math.Abs(last.High - previousClose), Math.Abs(last.Low - previousClose)));
            }

            if (!agg.AtrSeeded)
            {
                agg.Atr = tr;
                agg.AtrSeeded = true;
            }
            else
            {
                agg.Atr += (tr - agg.Atr) / AtrPeriod;
            }
        }

        private void ConfirmHtfSwings(HtfAggregator agg)
        {
            var candles = agg.Candles;
            var p = candles.Count - 1 - HtfSwingPeriod;
            if (p < HtfSwingPeriod)
                return;

            var pivot = candles[p];
            var high = true;
            var low = true;
            for (var j = p - HtfSwingPeriod; j <= p + HtfSwingPeriod; j++)
            {
                if (j == p)
                    continue;

                if (candles[j].High > pivot.High)
                    high = false;
                if (candles[j].Low < pivot.Low)
                    low = false;
                if (!high && !low)
                    return;
            }

            if (high && (agg.SwingHighs.Count == 0 || agg.SwingHighs[^1].Bar != p))
            {
                var swing = new SwingPoint { Bar = p, Price = pivot.High };
                agg.SwingHighs.Add(swing);
                agg.LastSwingHigh = swing;
            }

            if (low && (agg.SwingLows.Count == 0 || agg.SwingLows[^1].Bar != p))
            {
                var swing = new SwingPoint { Bar = p, Price = pivot.Low };
                agg.SwingLows.Add(swing);
                agg.LastSwingLow = swing;
            }
        }

        private StructureEvent DetectHtfStructureBreak(HtfAggregator agg)
        {
            if (agg.Candles.Count == 0)
                return null;

            var last = agg.Candles[^1];
            var displaced = agg.Atr <= 0m || last.High - last.Low >= agg.Atr * HtfDisplacementFactor;
            if (!displaced)
                return null;

            if (agg.LastSwingHigh is { Broken: false } high && last.Close > high.Price)
            {
                high.Broken = true;
                var evt = new StructureEvent
                {
                    Id = ++_nextStructureEventId,
                    Bar = agg.Candles.Count - 1,
                    FromBar = high.Bar,
                    Level = high.Price,
                    Bullish = true,
                    IsMss = agg.Trend == -1,
                    Scope = StructureScope.External
                };
                agg.Trend = 1;
                return evt;
            }

            if (agg.LastSwingLow is { Broken: false } low && last.Close < low.Price)
            {
                low.Broken = true;
                var evt = new StructureEvent
                {
                    Id = ++_nextStructureEventId,
                    Bar = agg.Candles.Count - 1,
                    FromBar = low.Bar,
                    Level = low.Price,
                    Bullish = false,
                    IsMss = agg.Trend == 1,
                    Scope = StructureScope.External
                };
                agg.Trend = -1;
                return evt;
            }

            return null;
        }

        private void DetectHtfFvg(HtfAggregator agg, int confirmedChartBar)
        {
            if (!HtfFvgEnabled || agg.Candles.Count < 3)
                return;

            var candles = agg.Candles;
            var a = candles[^3];
            var middle = candles[^2];
            var b = candles[^1];
            var minSize = Math.Max(MinFvgTicks * TickSize, agg.Atr * MinFvgAtrFraction);

            if (b.Low > a.High && b.Low - a.High >= minSize)
            {
                RegisterHtfZone(new Zone
                {
                    Type = ZoneType.BullFvg,
                    IsHtf = true,
                    HtfLabel = agg.Label,
                    HtfMinutes = agg.Minutes,
                    StartBar = middle.FirstChartBar,
                    ConfirmedBar = confirmedChartBar,
                    EligibleFromBar = confirmedChartBar,
                    Top = b.Low,
                    Bottom = a.High
                }, confirmedChartBar);
            }
            else if (b.High < a.Low && a.Low - b.High >= minSize)
            {
                RegisterHtfZone(new Zone
                {
                    Type = ZoneType.BearFvg,
                    IsHtf = true,
                    HtfLabel = agg.Label,
                    HtfMinutes = agg.Minutes,
                    StartBar = middle.FirstChartBar,
                    ConfirmedBar = confirmedChartBar,
                    EligibleFromBar = confirmedChartBar,
                    Top = a.Low,
                    Bottom = b.High
                }, confirmedChartBar);
            }
        }

        private void CreateHtfOrderBlock(HtfAggregator agg, StructureEvent structure, int confirmedChartBar)
        {
            if (!HtfObEnabled || agg.Candles.Count < 2)
                return;

            var candles = agg.Candles;
            var start = Math.Max(0, candles.Count - 1 - ObLookback);
            for (var i = candles.Count - 2; i >= start; i--)
            {
                var source = candles[i];
                var opposite = structure.Bullish ? source.Close < source.Open : source.Close > source.Open;
                if (!opposite)
                    continue;

                RegisterHtfZone(new Zone
                {
                    Type = structure.Bullish ? ZoneType.BullOrderBlock : ZoneType.BearOrderBlock,
                    IsHtf = true,
                    HtfLabel = agg.Label,
                    HtfMinutes = agg.Minutes,
                    StartBar = source.FirstChartBar,
                    ConfirmedBar = confirmedChartBar,
                    EligibleFromBar = confirmedChartBar,
                    ConfirmingStructureBar = structure.Id,
                    Top = ObStyle == ObZoneStyle.Body ? Math.Max(source.Open, source.Close) : source.High,
                    Bottom = ObStyle == ObZoneStyle.Body ? Math.Min(source.Open, source.Close) : source.Low
                }, confirmedChartBar);
                return;
            }
        }

        private void ApplyHtfBodyCloseMitigation(HtfAggregator agg, int confirmedChartBar)
        {
            if (agg.Candles.Count == 0)
                return;

            var close = agg.Candles[^1].Close;
            var inversions = new List<Zone>();
            foreach (var zone in _zones)
            {
                if (!zone.IsHtf || zone.HtfMinutes != agg.Minutes || zone.ConfirmedBar >= confirmedChartBar)
                    continue;

                var rule = zone.IsOrderBlock ? ObMitigation : FvgMitigation;
                if (zone.State != ZoneState.Mitigated && rule == MitigationRule.BodyClose &&
                    StrictRules.IsBodyCloseInvalidated(zone.IsBullish, close, zone.Top, zone.Bottom))
                {
                    Mitigate(zone, confirmedChartBar, "HTF BodyClose");
                }

                if (!IfvgEnabled || zone.Inverted ||
                    zone.Type is not (ZoneType.BullFvg or ZoneType.BearFvg) ||
                    (zone.State == ZoneState.Mitigated &&
                     (!zone.EndBar.HasValue || confirmedChartBar - zone.EndBar.Value > 3)) ||
                    !StrictRules.IsBodyCloseInvalidated(zone.IsBullish, close, zone.Top, zone.Bottom))
                    continue;

                zone.Inverted = true;
                if (zone.State != ZoneState.Mitigated)
                    Mitigate(zone, confirmedChartBar, "HTF IFVG body-close conversion");

                inversions.Add(new Zone
                {
                    Type = zone.Type == ZoneType.BullFvg ? ZoneType.BearIfvg : ZoneType.BullIfvg,
                    IsHtf = true,
                    HtfLabel = agg.Label,
                    HtfMinutes = agg.Minutes,
                    StartBar = confirmedChartBar,
                    ConfirmedBar = confirmedChartBar,
                    EligibleFromBar = confirmedChartBar,
                    Top = zone.Top,
                    Bottom = zone.Bottom
                });
            }

            foreach (var inversion in inversions)
            {
                RegisterHtfZone(inversion, confirmedChartBar);
                JournalEvent(confirmedChartBar, "ZoneInverted", inversion.IsBullish ? "Bull" : "Bear", inversion, 0m,
                    "HTF FVG flipped polarity (source-timeframe body close through)");
            }
        }

        private void RegisterHtfZone(Zone proposed, int confirmedChartBar)
        {
            AddZone(proposed);
        }

        private void ReconcileHistoricalHtfZonesThrough(int throughBar)
        {
            foreach (var zone in _zones.Where(z => z.IsHtf).ToList())
                ReconcileHistoricalHtfZone(zone, throughBar);
        }

        private void ReconcileHistoricalHtfZone(Zone zone, int throughBar)
        {
            if (!zone.IsHtf || throughBar < zone.EligibleFromBar)
                return;

            if (zone.State == ZoneState.Mitigated)
            {
                zone.HistoricalReconciledThroughBar = Math.Max(zone.HistoricalReconciledThroughBar, throughBar);
                return;
            }

            var from = Math.Max(zone.EligibleFromBar, zone.HistoricalReconciledThroughBar + 1);
            for (var bar = from; bar <= throughBar; bar++)
            {
                if (zone.State == ZoneState.Mitigated)
                    break;

                var candle = GetCandle(bar);
                if (!StrictRules.HasOhlcIntersection(candle.High, candle.Low, zone.Top, zone.Bottom))
                    continue;

                if (zone.FirstPresentationBar == null)
                {
                    zone.FirstPresentationBar = bar;
                    zone.FirstPresentationTime = BarTime(bar);
                    zone.State = ZoneState.Touched;
                    zone.TouchEpisodes = 1;
                }

                zone.LastTouchedBar = bar;
                zone.CoreEntryConsumed = true;
                var rule = zone.IsOrderBlock ? ObMitigation : FvgMitigation;
                var reachedMid = zone.IsBullish ? candle.Low <= zone.Mid : candle.High >= zone.Mid;
                var reachedFar = zone.IsBullish ? candle.Low <= zone.Bottom : candle.High >= zone.Top;
                if (rule == MitigationRule.AnyTouch)
                    Mitigate(zone, bar, "Historical HTF AnyTouch");
                else if (rule == MitigationRule.Midline && reachedMid)
                    Mitigate(zone, bar, "Historical HTF Midline");
                else if (rule == MitigationRule.FullFill && reachedFar)
                    Mitigate(zone, bar, "Historical HTF FullFill");
            }

            zone.HistoricalReconciledThroughBar = Math.Max(zone.HistoricalReconciledThroughBar, throughBar);
        }

        #endregion
    }
}
