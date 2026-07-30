using System;
using System.Collections.Generic;
using System.Linq;
using ATAS.Indicators;

namespace IctSmc
{
    public partial class IctSmcZones
    {
        /// <summary>
        /// Runs once per finalized candle. All pattern DETECTION happens here;
        /// touch/sweep/mitigation REACTION happens intrabar in ProcessIntrabar.
        /// </summary>
        private void OnBarComplete(int bar)
        {
            if (bar < 1)
                return;

            UpdateAtr(bar);
            UpdateOrderFlow(bar);
            ConfirmSwings(bar);
            DetectStructureBreak(bar);
            UpdateLegExtreme(bar);
            DetectFvg(bar);
            ApplyBodyCloseMitigation(bar);
            UpdateHtf(bar);
            UpdateOpenSignals(bar);
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
                    Bar = bar,
                    FromBar = _lastSwingHigh.Bar,
                    Level = _lastSwingHigh.Price,
                    Bullish = true,
                    IsMss = isMss
                };
                _structure.Add(evt);
                AnchorLeg(evt);
                OnStructureEvent(evt);

                if (ShowOb)
                    CreateOrderBlock(bar, bullish: true);
            }

            if (_lastSwingLow is { Broken: false } && close < _lastSwingLow.Price)
            {
                _lastSwingLow.Broken = true;
                var isMss = _trend == 1;
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
                AnchorLeg(evt);
                OnStructureEvent(evt);

                if (ShowOb)
                    CreateOrderBlock(bar, bullish: false);
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
            if (!ShowFvg || bar < 2)
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
                    Top = c0.Low,
                    Bottom = c2.High
                });
            }
        }

        #endregion

        #region Zone bookkeeping

        private void AddZone(Zone zone)
        {
            // Skip duplicates: an active zone of the same type covering the same territory.
            var overlaps = _zones.Any(z =>
                z.State != ZoneState.Mitigated &&
                z.Type == zone.Type &&
                z.IsHtf == zone.IsHtf &&
                z.HtfLabel == zone.HtfLabel &&
                zone.Top >= z.Bottom && zone.Bottom <= z.Top);

            if (overlaps)
                return;

            zone.Id = ++_nextZoneId;
            _zones.Add(zone);
            OnZoneCreated(zone);
            JournalEvent(_lastSeenBar, "ZoneCreated", zone.IsBullish ? "Bull" : "Bear", zone, 0m, "");

            var sameType = _zones.Where(z => z.Type == zone.Type && z.IsHtf == zone.IsHtf &&
                                             z.HtfLabel == zone.HtfLabel && z.State != ZoneState.Mitigated)
                                 .OrderByDescending(z => z.StartBar)
                                 .ToList();

            var cap = zone.IsHtf ? MaxHtfZones : MaxZonesPerType;
            foreach (var stale in sameType.Skip(cap))
                _zones.Remove(stale);
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
            var bodyLow = Math.Min(candle.Open, candle.Close);
            var bodyHigh = Math.Max(candle.Open, candle.Close);

            var inversions = new List<Zone>();

            foreach (var zone in _zones)
            {
                if (zone.StartBar >= bar)
                    continue;

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
                    (zone.State != ZoneState.Mitigated || (zone.EndBar.HasValue && bar - zone.EndBar.Value <= 3)))
                {
                    if (zone.Type == ZoneType.BullFvg && bodyLow < zone.Bottom)
                    {
                        zone.Inverted = true;
                        if (zone.State != ZoneState.Mitigated)
                            Mitigate(zone, bar);

                        inversions.Add(new Zone
                        {
                            Type = ZoneType.BearIfvg,
                            IsHtf = zone.IsHtf,
                            HtfLabel = zone.HtfLabel,
                            HtfMinutes = zone.HtfMinutes,
                            StartBar = bar,
                            Top = zone.Top,
                            Bottom = zone.Bottom
                        });
                    }
                    else if (zone.Type == ZoneType.BearFvg && bodyHigh > zone.Top)
                    {
                        zone.Inverted = true;
                        if (zone.State != ZoneState.Mitigated)
                            Mitigate(zone, bar);

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

            // Classify finished sweeps: close back inside = trap, close through = run.
            foreach (var level in _liquidity.Where(l => l.Swept && l.SweptBar == bar && l.WasTrap == null))
                level.WasTrap = level.BuySide ? candle.Close < level.Price : candle.Close > level.Price;
        }

        private void Mitigate(Zone zone, int bar)
        {
            if (zone.State == ZoneState.Mitigated)
                return;

            zone.State = ZoneState.Mitigated;
            zone.EndBar = bar;
            JournalEvent(bar, "ZoneMitigated", zone.IsBullish ? "Bull" : "Bear", zone, 0m, "");
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

            _liquidity.RemoveAll(l => l.Swept && l.SweptBar.HasValue && bar - l.SweptBar.Value > KeepMitigatedBars);
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
        private void UpdateHtf(int bar)
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

                    for (var i = 1; i <= bar; i++)
                        foreach (var agg in _htfAggregators)
                            FeedAggregator(agg, i);
                }

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

            foreach (var minutes in layers)
                _htfAggregators.Add(new HtfAggregator { Minutes = minutes, Label = MinutesToLabel(minutes) });

            var layerText = _htfAggregators.Count == 0
                ? "none (chart TF too high)"
                : string.Join(" + ", _htfAggregators.Select(a => a.Label));

            var chartText = regular
                ? MinutesToLabel(chartMinutes)
                : $"~{approx:0.#}m/bar (irregular → {MinutesToLabel(chartMinutes)})";

            _htfInfo = HtfMode == HtfSelectionMode.Manual
                ? $"HTF manual: {layerText} · chart {chartText}"
                : $"HTF auto: {layerText} · chart {chartText}";
        }

        /// <summary>
        /// Bucket start for a candle open time. Buckets are anchored to midnight
        /// (plus the configurable session anchor for daily-and-above layers, e.g.
        /// 18:00 ET futures session opens). Weekly buckets align to Monday 00:00
        /// because .NET tick zero (0001-01-01) is a Monday.
        /// </summary>
        private DateTime GetBucketStart(DateTime time, int minutes)
        {
            var anchorTicks = minutes >= 1440 ? TimeSpan.FromMinutes(DailyAnchorMinutes).Ticks : 0L;
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
                    OnHtfCandleClosed(agg);
                }

                agg.Current = new HtfCandle
                {
                    BucketStart = bucketStart,
                    FirstChartBar = bar,
                    LastChartBar = bar,
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
                agg.Current.LastChartBar = bar;
            }

            if (agg.Candles.Count > 400)
                agg.Candles.RemoveRange(0, agg.Candles.Count - 400);
        }

        private void OnHtfCandleClosed(HtfAggregator agg)
        {
            var candles = agg.Candles;
            var n = candles.Count;

            if (HtfFvgEnabled && n >= 3)
            {
                var a = candles[n - 3];
                var c = candles[n - 2];
                var b = candles[n - 1];

                var minSize = Math.Max(MinFvgTicks * TickSize, _atr * MinFvgAtrFraction);

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
                    });
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
                    });
                }
            }

            if (HtfObEnabled && n >= 6)
            {
                var last = candles[n - 1];
                var avgRange = candles.Skip(Math.Max(0, n - 11)).Take(10).Average(x => x.High - x.Low);

                if (avgRange > 0 && last.High - last.Low >= avgRange * HtfDisplacementFactor)
                {
                    var bullish = last.Close > last.Open;

                    for (var i = n - 2; i >= Math.Max(0, n - 6); i--)
                    {
                        var c = candles[i];
                        var isOpposite = bullish ? c.Close < c.Open : c.Close > c.Open;
                        if (!isOpposite)
                            continue;

                        AddZone(new Zone
                        {
                            Type = bullish ? ZoneType.BullOrderBlock : ZoneType.BearOrderBlock,
                            IsHtf = true,
                            HtfLabel = agg.Label,
                        HtfMinutes = agg.Minutes,
                            StartBar = c.FirstChartBar,
                            Top = ObStyle == ObZoneStyle.Body ? Math.Max(c.Open, c.Close) : c.High,
                            Bottom = ObStyle == ObZoneStyle.Body ? Math.Min(c.Open, c.Close) : c.Low
                        });
                        break;
                    }
                }
            }
        }

        #endregion
    }
}
