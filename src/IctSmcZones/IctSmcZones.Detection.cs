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
            ConfirmSwings(bar);
            DetectStructureBreak(bar);
            DetectFvg(bar);
            ApplyBodyCloseMitigation(bar);
            UpdateHtf(bar);
            Prune(bar);
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
                OnStructureEvent(evt);

                if (ShowOb)
                    CreateOrderBlock(bar, bullish: false);
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
                zone.Top >= z.Bottom && zone.Bottom <= z.Top);

            if (overlaps)
                return;

            _zones.Add(zone);
            OnZoneCreated(zone);

            var sameType = _zones.Where(z => z.Type == zone.Type && z.IsHtf == zone.IsHtf && z.State != ZoneState.Mitigated)
                                 .OrderByDescending(z => z.StartBar)
                                 .ToList();

            var cap = zone.IsHtf ? MaxHtfZones : MaxZonesPerType;
            foreach (var stale in sameType.Skip(cap))
                _zones.Remove(stale);
        }

        /// <summary>BodyClose mitigation can only be judged on a finalized candle.</summary>
        private void ApplyBodyCloseMitigation(int bar)
        {
            var candle = GetCandle(bar);
            var bodyLow = Math.Min(candle.Open, candle.Close);
            var bodyHigh = Math.Max(candle.Open, candle.Close);

            foreach (var zone in _zones)
            {
                if (zone.State == ZoneState.Mitigated || zone.StartBar >= bar)
                    continue;

                var rule = zone.IsOrderBlock ? ObMitigation : FvgMitigation;
                if (rule != MitigationRule.BodyClose)
                    continue;

                if (zone.IsBullish && bodyLow < zone.Bottom)
                    Mitigate(zone, bar);
                else if (!zone.IsBullish && bodyHigh > zone.Top)
                    Mitigate(zone, bar);
            }

            // Classify finished sweeps: close back inside = trap, close through = run.
            foreach (var level in _liquidity.Where(l => l.Swept && l.SweptBar == bar && l.WasTrap == null))
                level.WasTrap = level.BuySide ? candle.Close < level.Price : candle.Close > level.Price;
        }

        private void Mitigate(Zone zone, int bar)
        {
            zone.State = ZoneState.Mitigated;
            zone.EndBar = bar;
        }

        private void Prune(int bar)
        {
            _zones.RemoveAll(z =>
                z.State == ZoneState.Mitigated &&
                (!ShowMitigated || bar - (z.EndBar ?? bar) > KeepMitigatedBars));

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

        /// <summary>
        /// Aggregates chart candles into HTF buckets by open time and runs the same
        /// institutional detection (FVG + displacement order blocks) on that series.
        /// HTF zones carry more weight — the book: "OBs are more powerful when
        /// aligned with higher timeframes".
        /// </summary>
        private void UpdateHtf(int bar)
        {
            if (!HtfEnabled)
                return;

            var candle = GetCandle(bar);
            var bucketTicks = TimeSpan.FromMinutes(HtfMinutes).Ticks;
            var bucketStart = new DateTime(candle.Time.Ticks - candle.Time.Ticks % bucketTicks);

            if (_htfCurrent == null || bucketStart > _htfCurrent.BucketStart)
            {
                if (_htfCurrent != null)
                {
                    _htfCandles.Add(_htfCurrent);
                    OnHtfCandleClosed();
                }

                _htfCurrent = new HtfCandle
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
                _htfCurrent.High = Math.Max(_htfCurrent.High, candle.High);
                _htfCurrent.Low = Math.Min(_htfCurrent.Low, candle.Low);
                _htfCurrent.Close = candle.Close;
                _htfCurrent.LastChartBar = bar;
            }

            if (_htfCandles.Count > 400)
                _htfCandles.RemoveRange(0, _htfCandles.Count - 400);
        }

        private void OnHtfCandleClosed()
        {
            var n = _htfCandles.Count;

            if (HtfFvgEnabled && n >= 3)
            {
                var a = _htfCandles[n - 3];
                var c = _htfCandles[n - 2];
                var b = _htfCandles[n - 1];

                var minSize = Math.Max(MinFvgTicks * TickSize, _atr * MinFvgAtrFraction);

                if (b.Low > a.High && b.Low - a.High >= minSize)
                {
                    AddZone(new Zone
                    {
                        Type = ZoneType.BullFvg,
                        IsHtf = true,
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
                        StartBar = c.FirstChartBar,
                        Top = a.Low,
                        Bottom = b.High
                    });
                }
            }

            if (HtfObEnabled && n >= 6)
            {
                var last = _htfCandles[n - 1];
                var avgRange = _htfCandles.Skip(Math.Max(0, n - 11)).Take(10).Average(x => x.High - x.Low);

                if (avgRange > 0 && last.High - last.Low >= avgRange * HtfDisplacementFactor)
                {
                    var bullish = last.Close > last.Open;

                    for (var i = n - 2; i >= Math.Max(0, n - 6); i--)
                    {
                        var c = _htfCandles[i];
                        var isOpposite = bullish ? c.Close < c.Open : c.Close > c.Open;
                        if (!isOpposite)
                            continue;

                        AddZone(new Zone
                        {
                            Type = bullish ? ZoneType.BullOrderBlock : ZoneType.BearOrderBlock,
                            IsHtf = true,
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
