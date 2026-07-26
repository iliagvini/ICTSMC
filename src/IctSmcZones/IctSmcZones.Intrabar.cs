using System;
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

            // Arm the entry model when an MSS follows a liquidity sweep on the correct side.
            if (evt.Bullish)
            {
                var sweepOk = !RequireSweepForEntry ||
                              (_pendingBullSweepBar > 0 && evt.Bar - _pendingBullSweepBar <= SweepToMssWindow);
                if (sweepOk)
                    _armedBullUntil = evt.Bar + ArmWindowBars;
            }
            else
            {
                var sweepOk = !RequireSweepForEntry ||
                              (_pendingBearSweepBar > 0 && evt.Bar - _pendingBearSweepBar <= SweepToMssWindow);
                if (sweepOk)
                    _armedBearUntil = evt.Bar + ArmWindowBars;
            }
        }

        /// <summary>
        /// Fires the "sniper entry" alert the moment price returns into an aligned,
        /// unmitigated zone while the model is armed (sweep + MSS already seen) and
        /// the zone sits in the correct half of the dealing range.
        /// </summary>
        private void TickEntryModel(int bar)
        {
            if (!EntryModelEnabled)
                return;

            var candle = GetCandle(bar);
            var eq = GetEquilibrium();

            if (_armedBullUntil >= bar)
            {
                var zone = _zones.FirstOrDefault(z =>
                    z.State != ZoneState.Mitigated &&
                    z.IsBullish &&
                    z.StartBar < bar &&
                    candle.Low <= z.Top &&
                    (!EntryNeedsPdAlignment || eq == null || z.Mid <= eq.Value));

                if (zone != null)
                {
                    _armedBullUntil = -1;
                    _pendingBullSweepBar = -1;
                    EmitEntrySignal(zone, longSide: true);
                }
            }

            if (_armedBearUntil >= bar)
            {
                var zone = _zones.FirstOrDefault(z =>
                    z.State != ZoneState.Mitigated &&
                    !z.IsBullish &&
                    z.StartBar < bar &&
                    candle.High >= z.Bottom &&
                    (!EntryNeedsPdAlignment || eq == null || z.Mid >= eq.Value));

                if (zone != null)
                {
                    _armedBearUntil = -1;
                    _pendingBearSweepBar = -1;
                    EmitEntrySignal(zone, longSide: false);
                }
            }
        }

        private void EmitEntrySignal(Zone zone, bool longSide)
        {
            if (!AlertOnEntry)
                return;

            var buffer = SlBufferTicks * TickSize;
            decimal entry, sl;

            if (longSide)
            {
                entry = zone.Top;
                sl = zone.Bottom - buffer;
            }
            else
            {
                entry = zone.Bottom;
                sl = zone.Top + buffer;
            }

            var risk = Math.Abs(entry - sl);
            var tp2 = longSide ? entry + risk * 2 : entry - risk * 2;
            var tp3 = longSide ? entry + risk * 3 : entry - risk * 3;

            var dir = longSide ? "LONG" : "SHORT";
            var emoji = longSide ? "🟢" : "🔴";

            Fire($"{emoji} ENTRY MODEL {dir} — sweep + MSS + return to {zone.Tag} " +
                 $"{FormatPrice(zone.Bottom)}–{FormatPrice(zone.Top)}.\n" +
                 $"Entry ~{FormatPrice(entry)} | SL {FormatPrice(sl)} | TP(2R) {FormatPrice(tp2)} | TP(3R) {FormatPrice(tp3)}.\n" +
                 "Confirm with a rejection wick / lower-TF MSS before executing.");
        }

        /// <summary>Equilibrium (50%) of the current dealing range: last swing low ↔ last swing high.</summary>
        private decimal? GetEquilibrium()
        {
            if (_lastSwingHigh == null || _lastSwingLow == null)
                return null;

            return (_lastSwingHigh.Price + _lastSwingLow.Price) / 2m;
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
