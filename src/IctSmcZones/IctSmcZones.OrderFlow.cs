using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using ATAS.Indicators;

namespace IctSmc
{
    /// <summary>
    /// Order-flow lens — OBSERVATIONAL ONLY.
    ///
    /// Reads ATAS's native bid/ask/delta data (per bar and per price level inside
    /// each bar) to detect absorption at zones and liquidity, snapshot the exact
    /// order-flow state at the tick an entry signal fires, and track how the flow
    /// evolves while the signal is open. Everything lands in the journal so the
    /// analytics can answer whether order flow adds edge to the ICT model.
    /// It never gates, delays, or modifies signal execution.
    ///
    /// Absorption definition (all conditions required, on a completed bar):
    ///  1. effort      — volume ≥ AbsorptionVolumeFactor × average volume
    ///  2. aggression  — |delta| ≥ AbsorptionMinDeltaShare × volume, pointed
    ///                   AGAINST where the bar closed (heavy selling but close in
    ///                   the upper half = bullish absorption; mirror for bearish)
    ///  3. result fail — range ≤ AbsorptionMaxRangeAtr × ATR, OR the close pinned
    ///                   in the outer 40% of the bar against the aggression
    ///  4. location    — the bar overlaps an active zone or an unswept liquidity
    ///                   level (absorption in the middle of nowhere is noise)
    /// Footprint data (POC position, stacked diagonal imbalances) is recorded as
    /// supporting evidence when the feed provides it, never required.
    /// </summary>
    public partial class IctSmcZones
    {
        private const string GrpOrderFlow = "12. Order Flow";

        #region Settings

        [Display(GroupName = GrpOrderFlow, Name = "Order-flow tracking (observational)", Order = 1200)]
        public bool OrderFlowEnabled { get; set; } = true;

        [Display(GroupName = GrpOrderFlow, Name = "Volume baseline lookback (bars)", Order = 1210)]
        [Range(10, 500)]
        public int OfVolumeLookback { get; set; } = 50;

        [Display(GroupName = GrpOrderFlow, Name = "Absorption: min volume vs average (x)", Order = 1220)]
        [Range(1.0, 10.0)]
        public decimal AbsorptionVolumeFactor { get; set; } = 1.3m;

        [Display(GroupName = GrpOrderFlow, Name = "Absorption: max bar range vs ATR (x)", Order = 1230)]
        [Range(0.1, 3.0)]
        public decimal AbsorptionMaxRangeAtr { get; set; } = 0.6m;

        [Display(GroupName = GrpOrderFlow, Name = "Absorption: min |delta| share of volume", Order = 1240)]
        [Range(0.02, 0.9)]
        public decimal AbsorptionMinDeltaShare { get; set; } = 0.10m;

        [Display(GroupName = GrpOrderFlow, Name = "Footprint: imbalance ratio (x)", Order = 1250)]
        [Range(1.5, 10.0)]
        public decimal OfImbalanceRatio { get; set; } = 3.0m;

        [Display(GroupName = GrpOrderFlow, Name = "Footprint: stacked imbalance levels", Order = 1260)]
        [Range(2, 10)]
        public int OfStackedImbalances { get; set; } = 3;

        [Display(GroupName = GrpOrderFlow, Name = "Alert on absorption", Order = 1270)]
        public bool AlertOnAbsorption { get; set; } = false;

        #endregion

        #region State

        private readonly Queue<decimal> _ofVolWindow = new();
        private decimal _ofVolSum;
        private decimal _cvd;
        private readonly Queue<decimal> _cvdRing = new();
        private int _ofVolBars;
        private int _ofZeroDeltaBars;
        private bool _ofDeltaAvailable = true;
        private bool _ofFootprintAvailable = true;
        private bool _ofInfoLogged;

        #endregion

        private void ResetOrderFlow()
        {
            _ofVolWindow.Clear();
            _ofVolSum = 0m;
            _cvd = 0m;
            _cvdRing.Clear();
            _ofVolBars = 0;
            _ofZeroDeltaBars = 0;
            _ofDeltaAvailable = true;
            _ofFootprintAvailable = true;
            _ofInfoLogged = false;
        }

        /// <summary>Per completed bar: baselines, CVD, data-quality latch, absorption.</summary>
        private void UpdateOrderFlow(int bar)
        {
            if (!OrderFlowEnabled)
                return;

            var candle = GetCandle(bar);
            var vol = candle.Volume;
            var delta = candle.Delta;

            // Relative volume is measured against PRIOR bars only.
            var avgVol = _ofVolWindow.Count > 0 ? _ofVolSum / _ofVolWindow.Count : 0m;

            // Data-quality: some feeds carry volume but no bid/ask split — delta is
            // permanently zero. Latch once so delta metrics report blank, not fake 0s.
            if (vol > 0)
            {
                _ofVolBars++;
                if (delta == 0 && candle.Bid == 0 && candle.Ask == 0)
                    _ofZeroDeltaBars++;
            }

            if (!_ofInfoLogged && _ofVolBars >= 30)
            {
                _ofInfoLogged = true;
                _ofDeltaAvailable = _ofZeroDeltaBars < _ofVolBars;
                JournalEvent(bar, "OrderFlowInfo", "", null, 0m, _ofDeltaAvailable
                    ? "Bid/ask delta data available"
                    : "Feed provides no bid/ask split; delta metrics disabled, volume metrics active");
            }

            _cvd += delta;
            _cvdRing.Enqueue(_cvd);
            while (_cvdRing.Count > 6)
                _cvdRing.Dequeue();

            DetectAbsorption(bar, candle, vol, delta, avgVol);

            if (vol > 0)
            {
                _ofVolWindow.Enqueue(vol);
                _ofVolSum += vol;
                while (_ofVolWindow.Count > OfVolumeLookback)
                    _ofVolSum -= _ofVolWindow.Dequeue();
            }
        }

        private void DetectAbsorption(int bar, IndicatorCandle candle, decimal vol, decimal delta, decimal avgVol)
        {
            if (!_ofDeltaAvailable || avgVol <= 0 || vol <= 0 || _atr <= 0)
                return;

            var range = candle.High - candle.Low;
            if (range <= 0)
                return;

            var relVol = vol / avgVol;
            if (relVol < AbsorptionVolumeFactor)
                return;

            var deltaShare = delta / vol;
            var closePos = (candle.Close - candle.Low) / range;
            var rangeAtr = range / _atr;
            var compressed = rangeAtr <= AbsorptionMaxRangeAtr;

            // Aggression against the close, with a failed result: sellers hammered
            // the bid yet price held its upper half (bullish), or mirror (bearish).
            var bullish = deltaShare <= -AbsorptionMinDeltaShare && closePos >= 0.5m
                          && (compressed || closePos >= 0.6m);
            var bearish = deltaShare >= AbsorptionMinDeltaShare && closePos <= 0.5m
                          && (compressed || closePos <= 0.4m);

            if (!bullish && !bearish)
                return;

            // Location filter: only meaningful at an active zone or unswept liquidity.
            Zone locZone = null;
            string location = null;

            foreach (var z in _zones)
            {
                if (z.State == ZoneState.Mitigated || candle.Low > z.Top || candle.High < z.Bottom)
                    continue;
                // Prefer the zone whose side matches the absorption direction.
                if (locZone == null || (z.IsBullish == bullish && locZone.IsBullish != bullish))
                    locZone = z;
            }

            if (locZone != null)
            {
                location = $"{locZone.Tag} {Num(locZone.Bottom)}-{Num(locZone.Top)}";
            }
            else
            {
                var tol = EqualLevelTicks * TickSize;
                var level = _liquidity.FirstOrDefault(l =>
                    !l.Swept && l.Price >= candle.Low - tol && l.Price <= candle.High + tol);
                if (level != null)
                    location = $"{(level.BuySide ? "BSL" : "SSL")} {Num(level.Price)}";
            }

            if (location == null)
                return;

            if (locZone != null)
            {
                locZone.LastAbsorptionBar = bar;
                locZone.LastAbsorptionBull = bullish;
            }

            TryReadFootprint(candle, out var pocPct, out var buyStacks, out var sellStacks);

            var extra = string.Join(", ",
                $"Vol={Num(vol)} ({relVol.ToString("0.0", CultureInfo.InvariantCulture)}x avg)",
                $"Delta={Num(delta)} ({(deltaShare * 100m).ToString("0", CultureInfo.InvariantCulture)}%)",
                $"MinD={Num(candle.MinDelta)}",
                $"MaxD={Num(candle.MaxDelta)}",
                $"Range/ATR={rangeAtr.ToString("0.00", CultureInfo.InvariantCulture)}",
                $"ClosePos={(closePos * 100m).ToString("0", CultureInfo.InvariantCulture)}%",
                pocPct >= 0 ? $"POC={pocPct.ToString("0", CultureInfo.InvariantCulture)}%" : "POC=n/a",
                $"StackedImb={buyStacks}B/{sellStacks}S",
                $"At={location}");

            JournalEvent(bar, "Absorption", bullish ? "Bull" : "Bear", locZone, candle.Close, extra);

            if (AlertOnAbsorption)
            {
                Fire($"🧲 Absorption — {(bullish ? "selling absorbed (bullish)" : "buying absorbed (bearish)")}\n" +
                     $"📍 At: {location}\n" +
                     $"📊 Vol {relVol.ToString("0.0", CultureInfo.InvariantCulture)}× avg · Δ {(deltaShare * 100m).ToString("0", CultureInfo.InvariantCulture)}%\n" +
                     $"👀 Confluence for {(bullish ? "longs" : "shorts")} at this level");
            }
        }

        /// <summary>
        /// The ONLY place footprint (per-price bid/ask) API is touched. Guarded so a
        /// feed or SDK version without cluster data degrades to bar-level metrics.
        /// Stacked imbalances are diagonal: ask at a level vs bid one tick below
        /// (buy side), counted once per run of ≥ OfStackedImbalances levels.
        /// </summary>
        private void TryReadFootprint(IndicatorCandle candle, out decimal pocPct, out int buyStacks, out int sellStacks)
        {
            pocPct = -1m;
            buyStacks = 0;
            sellStacks = 0;

            if (!_ofFootprintAvailable)
                return;

            try
            {
                var levels = candle.GetAllPriceLevels()?
                    .OrderBy(l => l.Price)
                    .ToList();

                if (levels == null || levels.Count == 0)
                    return;

                var range = candle.High - candle.Low;
                if (range > 0)
                {
                    var pocPrice = levels.OrderByDescending(l => l.Volume).First().Price;
                    pocPct = (pocPrice - candle.Low) / range * 100m;
                }

                if (levels.Count < 2)
                    return;

                var runBuy = 0;
                var runSell = 0;

                for (var i = 1; i < levels.Count; i++)
                {
                    var askUp = levels[i].Ask;
                    var bidDown = levels[i - 1].Bid;

                    var buyImb = askUp > 0 && askUp >= OfImbalanceRatio * Math.Max(1m, bidDown);
                    var sellImb = bidDown > 0 && bidDown >= OfImbalanceRatio * Math.Max(1m, askUp);

                    runBuy = buyImb ? runBuy + 1 : 0;
                    runSell = sellImb ? runSell + 1 : 0;

                    if (runBuy == OfStackedImbalances)
                        buyStacks++;
                    if (runSell == OfStackedImbalances)
                        sellStacks++;
                }
            }
            catch
            {
                // Footprint data not available in this feed/SDK combination.
                _ofFootprintAvailable = false;
                pocPct = -1m;
                buyStacks = 0;
                sellStacks = 0;
            }
        }

        /// <summary>
        /// Snapshot of the order-flow state at the exact tick a signal fires — the
        /// developing candle's live volume/delta plus session CVD and any recent
        /// same-direction absorption stamped on the matched zones.
        /// </summary>
        private void CaptureOrderFlowSnapshot(SignalRecord rec, List<Zone> matches, int bar)
        {
            if (!OrderFlowEnabled)
                return;

            var candle = GetCandle(bar);
            var vol = candle.Volume;
            var avgVol = _ofVolWindow.Count > 0 ? _ofVolSum / _ofVolWindow.Count : 0m;

            rec.OfCaptured = true;
            rec.OfDeltaAvailable = _ofDeltaAvailable;
            rec.OfVolume = vol;
            rec.OfRelVolume = avgVol > 0 ? vol / avgVol : 0m;
            rec.OfDelta = candle.Delta;
            rec.OfDeltaShare = vol > 0 ? candle.Delta / vol : 0m;
            // _cvd covers completed bars; add the developing bar's delta so far.
            rec.OfCvd = _cvd + candle.Delta;
            rec.OfCvdAtEntry = rec.OfCvd;
            rec.OfCvdSlope5 = _cvdRing.Count > 0 ? _cvd - _cvdRing.Peek() : 0m;

            TryReadFootprint(candle, out var pocPct, out var buyStacks, out var sellStacks);
            rec.OfPocPct = pocPct;
            rec.OfImbalances = _ofFootprintAvailable ? $"{buyStacks}B/{sellStacks}S" : "";

            var stamped = matches
                .Where(z => z.LastAbsorptionBar >= 0 &&
                            bar - z.LastAbsorptionBar <= 10 &&
                            z.LastAbsorptionBull == rec.Long)
                .OrderByDescending(z => z.LastAbsorptionBar)
                .FirstOrDefault();

            if (stamped != null)
            {
                rec.OfAbsorptionAtEntry = true;
                rec.OfAbsorption = $"{(rec.Long ? "Bull" : "Bear")} {bar - stamped.LastAbsorptionBar} bars ago @ {stamped.Tag}";
            }
        }
    }
}
