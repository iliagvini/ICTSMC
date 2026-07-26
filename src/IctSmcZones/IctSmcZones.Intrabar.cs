using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace IctSmc
{
    public partial class IctSmcZones
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        #region Intrabar reaction engine

        /// <summary>
        /// Called on EVERY tick of the developing candle (and once per historical bar).
        /// Detects zone touches, liquidity sweeps, touch-based mitigation and
        /// entry-model triggers the instant they happen — no waiting for the close.
        /// </summary>
        private void ProcessIntrabar(int bar)
        {
            CheckLiquiditySweeps(bar);
            CheckZoneTouches(bar);
            TickEntryModel(bar);
        }

        private void CheckLiquiditySweeps(int bar)
        {
            var candle = GetCandle(bar);

            foreach (var level in _liquidity)
            {
                if (level.Swept || level.StartBar >= bar)
                    continue;

                var crossed = level.BuySide ? candle.High > level.Price : candle.Low < level.Price;
                if (!crossed)
                    continue;

                level.Swept = true;
                level.SweptBar = bar;

                // Entry-model precursor: taking sell-side liquidity primes LONGS,
                // taking buy-side liquidity primes SHORTS.
                if (level.BuySide)
                    _pendingBearSweepBar = bar;
                else
                    _pendingBullSweepBar = bar;

                if (!level.SweptAlerted && AlertOnSweep)
                {
                    level.SweptAlerted = true;
                    var side = level.BuySide ? "Buy-side" : "Sell-side";
                    var pool = level.IsEqual ? (level.BuySide ? " (equal highs)" : " (equal lows)") : "";
                    Fire($"💧 {side} liquidity taken{pool} @ {FormatPrice(level.Price)}. " +
                         (level.BuySide ? "Watch for bearish MSS → short setup." : "Watch for bullish MSS → long setup."));
                }
            }
        }

        private void CheckZoneTouches(int bar)
        {
            var candle = GetCandle(bar);

            foreach (var zone in _zones)
            {
                if (zone.State == ZoneState.Mitigated || zone.StartBar >= bar)
                    continue;

                var rule = zone.IsOrderBlock ? ObMitigation : FvgMitigation;

                // A bullish zone sits below price and is tapped from above;
                // a bearish zone sits above price and is tapped from below.
                var touched = zone.IsBullish ? candle.Low <= zone.Top : candle.High >= zone.Bottom;
                if (!touched)
                    continue;

                if (zone.State == ZoneState.Active)
                    zone.State = ZoneState.Touched;

                if (!zone.TouchAlerted && AlertOnZoneTouch)
                {
                    zone.TouchAlerted = true;
                    var dir = zone.IsBullish ? "support — watch for the bounce" : "resistance — watch for the rejection";
                    Fire($"🎯 Price tapped {zone.Tag} {FormatPrice(zone.Bottom)}–{FormatPrice(zone.Top)} ({dir}).");
                }

                // Touch-based mitigation rules react intrabar as well.
                switch (rule)
                {
                    case MitigationRule.AnyTouch:
                        Mitigate(zone, bar);
                        break;

                    case MitigationRule.Midline:
                        if (zone.IsBullish ? candle.Low <= zone.Mid : candle.High >= zone.Mid)
                            Mitigate(zone, bar);
                        break;

                    case MitigationRule.FullFill:
                        if (zone.IsBullish ? candle.Low <= zone.Bottom : candle.High >= zone.Top)
                            Mitigate(zone, bar);
                        break;

                    // BodyClose is handled on finalized candles in ApplyBodyCloseMitigation.
                }
            }
        }

        #endregion

        #region Entry model (sweep → MSS → return to zone)

        private void OnStructureEvent(StructureEvent evt)
        {
            if (AlertOnStructure)
            {
                var kind = evt.IsMss ? "MSS" : "BoS";
                var dir = evt.Bullish ? "bullish" : "bearish";
                var hint = evt.IsMss
                    ? (evt.Bullish ? "Trend flipping UP — look for longs on the retrace." : "Trend flipping DOWN — look for shorts on the retrace.")
                    : "Trend continuation.";
                Fire($"📐 {kind} {dir} @ {FormatPrice(evt.Level)}. {hint}");
            }

            if (!EntryModelEnabled || !evt.IsMss)
                return;

            // An MSS is fresh structural information for BOTH sides:
            // it arms its own direction and it proves any opposite armed setup was
            // built on a failed shift — cancel it instead of letting it expire by clock.
            if (evt.Bullish)
            {
                if (CancelOnOppositeMss)
                {
                    var wasArmed = _armedBearUntil >= evt.Bar;
                    _armedBearUntil = -1;
                    _pendingBearSweepBar = -1;

                    if (wasArmed && AlertOnFailedMss)
                        Fire("⚠️ Failed bearish MSS — armed SHORT setup cancelled by a bullish MSS. " +
                             "Failed shifts often fuel the opposite move: watch the new long side.");
                }

                var sweepOk = !RequireSweepForEntry ||
                              (_pendingBullSweepBar > 0 && evt.Bar - _pendingBullSweepBar <= SweepToMssWindow);
                if (sweepOk)
                    _armedBullUntil = evt.Bar + ArmWindowBars;
            }
            else
            {
                if (CancelOnOppositeMss)
                {
                    var wasArmed = _armedBullUntil >= evt.Bar;
                    _armedBullUntil = -1;
                    _pendingBullSweepBar = -1;

                    if (wasArmed && AlertOnFailedMss)
                        Fire("⚠️ Failed bullish MSS — armed LONG setup cancelled by a bearish MSS. " +
                             "Failed shifts often fuel the opposite move: watch the new short side.");
                }

                var sweepOk = !RequireSweepForEntry ||
                              (_pendingBearSweepBar > 0 && evt.Bar - _pendingBearSweepBar <= SweepToMssWindow);
                if (sweepOk)
                    _armedBearUntil = evt.Bar + ArmWindowBars;
            }
        }

        /// <summary>
        /// Fires the "sniper entry" alert the moment price returns into aligned,
        /// unmitigated zone(s) while the model is armed (sweep + MSS already seen).
        /// All touched zones are collected so stacked LTF/HTF confluence is scored,
        /// and the premium/discount check uses a tolerance band around equilibrium.
        /// </summary>
        private void TickEntryModel(int bar)
        {
            if (!EntryModelEnabled)
                return;

            // A sweep that never produced an MSS inside the window is stale — drop it
            // so a much later MSS can't chain to ancient liquidity.
            if (_pendingBullSweepBar > 0 && bar - _pendingBullSweepBar > SweepToMssWindow)
                _pendingBullSweepBar = -1;
            if (_pendingBearSweepBar > 0 && bar - _pendingBearSweepBar > SweepToMssWindow)
                _pendingBearSweepBar = -1;

            var candle = GetCandle(bar);

            var range = GetDealingRange();
            decimal? eq = null;
            var tolerance = 0m;

            if (range.HasValue)
            {
                eq = (range.Value.High.Price + range.Value.Low.Price) / 2m;
                tolerance = (range.Value.High.Price - range.Value.Low.Price) * PdTolerancePercent / 100m;
            }

            if (_armedBullUntil >= bar)
            {
                var matches = _zones.Where(z =>
                    z.State != ZoneState.Mitigated &&
                    z.IsBullish &&
                    z.StartBar < bar &&
                    candle.Low <= z.Top &&
                    (!EntryNeedsPdAlignment || eq == null || z.Mid <= eq.Value + tolerance)).ToList();

                if (matches.Count > 0)
                {
                    _armedBullUntil = -1;
                    _pendingBullSweepBar = -1;
                    EmitEntrySignal(matches, longSide: true, eq, tolerance);
                }
            }

            if (_armedBearUntil >= bar)
            {
                var matches = _zones.Where(z =>
                    z.State != ZoneState.Mitigated &&
                    !z.IsBullish &&
                    z.StartBar < bar &&
                    candle.High >= z.Bottom &&
                    (!EntryNeedsPdAlignment || eq == null || z.Mid >= eq.Value - tolerance)).ToList();

                if (matches.Count > 0)
                {
                    _armedBearUntil = -1;
                    _pendingBearSweepBar = -1;
                    EmitEntrySignal(matches, longSide: false, eq, tolerance);
                }
            }
        }

        private void EmitEntrySignal(List<Zone> matches, bool longSide, decimal? eq, decimal tolerance)
        {
            if (!AlertOnEntry)
                return;

            // The trigger zone is the one price physically touched first
            // (highest top for a falling tap, lowest bottom for a rising one);
            // the trade plan is built from it so the stop stays structural and tight.
            var trigger = longSide
                ? matches.OrderByDescending(z => z.Top).First()
                : matches.OrderBy(z => z.Bottom).First();

            var buffer = SlBufferTicks * TickSize;
            decimal entry, sl;

            if (longSide)
            {
                entry = trigger.Top;
                sl = trigger.Bottom - buffer;
            }
            else
            {
                entry = trigger.Bottom;
                sl = trigger.Top + buffer;
            }

            var risk = Math.Abs(entry - sl);
            var tp2 = longSide ? entry + risk * 2 : entry - risk * 2;
            var tp3 = longSide ? entry + risk * 3 : entry - risk * 3;

            // Confluence tier: Daily/Weekly involvement = A++, any HTF = A+, LTF-only = B.
            var tierCount = matches.Any(z => z.HtfMinutes >= 1440) ? 3
                : matches.Any(z => z.IsHtf) ? 2
                : 1;
            var tierName = tierCount switch { 3 => "A++", 2 => "A+", _ => "B" };
            var mark = string.Concat(Enumerable.Repeat(longSide ? "🟢" : "🔴", tierCount));

            var confluence = string.Join(" + ", matches
                .OrderByDescending(z => z.HtfMinutes)
                .ThenBy(z => z.Tag)
                .Select(z => z.Tag)
                .Distinct()
                .Take(4));

            var pdStatus = "n/a";
            if (eq.HasValue)
            {
                pdStatus = trigger.Mid < eq.Value - tolerance ? "Discount"
                    : trigger.Mid <= eq.Value + tolerance ? "Near EQ"
                    : "Premium";
            }

            var dir = longSide ? "LONG" : "SHORT";

            Fire($"{mark} ENTRY MODEL {dir} [{tierName}] — sweep + MSS + return to {trigger.Tag} " +
                 $"{FormatPrice(trigger.Bottom)}–{FormatPrice(trigger.Top)}.\n" +
                 $"Confluence: {confluence} | PD: {pdStatus}\n" +
                 $"Entry ~{FormatPrice(entry)} | SL {FormatPrice(sl)} | TP(2R) {FormatPrice(tp2)} | TP(3R) {FormatPrice(tp3)}.\n" +
                 "Confirm with a rejection wick / lower-TF MSS before executing.");
        }

        /// <summary>
        /// Current dealing range as a properly ORDERED swing pair (high above low).
        /// After a strong one-way run the most recent swing high can sit below the
        /// most recent swing low; in that case the older side is walked back until a
        /// consistent pair is found, so equilibrium is never computed from an
        /// inverted range.
        /// </summary>
        private (SwingPoint High, SwingPoint Low)? GetDealingRange()
        {
            if (_swingHighs.Count == 0 || _swingLows.Count == 0)
                return null;

            var high = _swingHighs[^1];
            var low = _swingLows[^1];

            if (high.Price <= low.Price)
            {
                if (high.Bar >= low.Bar)
                {
                    low = _swingLows.FindLast(l => l.Price < high.Price);
                }
                else
                {
                    high = _swingHighs.FindLast(h => h.Price > low.Price);
                }

                if (high == null || low == null || high.Price <= low.Price)
                    return null;
            }

            return (high, low);
        }

        #endregion

        #region Alert plumbing

        private void OnZoneCreated(Zone zone)
        {
            if (zone.CreatedAlerted || !AlertOnZoneCreated)
                return;

            zone.CreatedAlerted = true;
            Fire($"📦 New {zone.Tag} formed: {FormatPrice(zone.Bottom)}–{FormatPrice(zone.Top)}.");
        }

        /// <summary>
        /// Central alert dispatcher. Alerts fire only in realtime (never while the
        /// indicator replays history) and go to ATAS popups and/or Telegram.
        /// </summary>
        private void Fire(string message)
        {
            if (!_realtime)
                return;

            var instrument = InstrumentInfo?.Instrument ?? "";
            var full = string.IsNullOrEmpty(instrument) ? message : $"[{instrument}] {message}";

            if (UsePopupAlerts)
            {
                try
                {
                    AddAlert(AlertFile, full);
                }
                catch
                {
                    // Alert subsystem unavailable (e.g. during optimization) — ignore.
                }
            }

            if (TelegramEnabled)
                SendTelegram(full);
        }

        private void SendTelegram(string text)
        {
            var token = TelegramBotToken?.Trim();
            var chatId = TelegramChatId?.Trim();

            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chatId))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var url = $"https://api.telegram.org/bot{token}/sendMessage";
                    var payload = new FormUrlEncodedContent(new[]
                    {
                        new System.Collections.Generic.KeyValuePair<string, string>("chat_id", chatId),
                        new System.Collections.Generic.KeyValuePair<string, string>("text", text)
                    });

                    using var response = await Http.PostAsync(url, payload).ConfigureAwait(false);
                    _ = response.IsSuccessStatusCode;
                }
                catch
                {
                    // Network hiccups must never crash the chart thread.
                }
            });
        }

        #endregion
    }
}
