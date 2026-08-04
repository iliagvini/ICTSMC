using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace ICTSMC
{
    public partial class ICTSMCStrategy
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

                JournalEvent(bar, "LiquiditySweep", level.BuySide ? "BuySide" : "SellSide", null, level.Price,
                    level.IsEqual ? "Equal highs/lows pool" : "");

                if (!level.SweptAlerted && AlertOnSweep)
                {
                    level.SweptAlerted = true;
                    var side = level.BuySide ? "Buy-side" : "Sell-side";
                    var pool = level.IsEqual ? (level.BuySide ? " · equal highs" : " · equal lows") : "";
                    Fire($"💧 Liquidity taken — {side}{pool}\n" +
                         $"📍 Level: {FormatPrice(level.Price)}\n" +
                         (level.BuySide
                             ? "👀 Next: watch for bearish MSS → short setup"
                             : "👀 Next: watch for bullish MSS → long setup"));
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

                // Distinct touch episodes: contact on consecutive bars is ONE episode;
                // a full untouched bar in between separates two.
                var isRetouch = zone.LastTouchedBar >= 0 && bar > zone.LastTouchedBar + 1;
                zone.LastTouchedBar = bar;

                if (zone.State == ZoneState.Active)
                {
                    zone.State = ZoneState.Touched;
                    zone.TouchEpisodes = 1;
                    TryEmitContinuationSignal(zone, bar);
                }
                else if (isRetouch && zone.State == ZoneState.Touched)
                {
                    zone.TouchEpisodes++;
                    var age = bar - zone.StartBar;

                    JournalEvent(bar, "ZoneRetouch", zone.IsBullish ? "Bull" : "Bear", zone, candle.Close,
                        $"touch #{zone.TouchEpisodes}; zone age {age} bars");

                    // Info only, deliberately NOT a trade signal: the first presentation
                    // consumed the one-signal-per-zone budget; re-touches are weaker.
                    if (AlertOnZoneRetouch)
                        Fire($"🔁 Zone re-touched — {zone.Tag} (info only)\n" +
                             $"📍 Zone: {FormatPrice(zone.Bottom)}–{FormatPrice(zone.Top)}\n" +
                             $"🔢 Touch #{zone.TouchEpisodes} · zone is {age} bars old\n" +
                             "ℹ️ No signal: first touch already consumed — re-touches are lower probability");
                }

                if (!zone.TouchLogged)
                {
                    zone.TouchLogged = true;
                    JournalEvent(bar, "ZoneTouch", zone.IsBullish ? "Bull" : "Bear", zone, GetCandle(bar).Close, "");
                }

                if (!zone.TouchAlerted && AlertOnZoneTouch)
                {
                    zone.TouchAlerted = true;
                    Fire($"🎯 Zone tapped — {zone.Tag}\n" +
                         $"📍 Range: {FormatPrice(zone.Bottom)}–{FormatPrice(zone.Top)}\n" +
                         (zone.IsBullish
                             ? "🛡 Support — watch for the bounce"
                             : "🧱 Resistance — watch for the rejection"));
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
            JournalEvent(evt.Bar, evt.IsMss ? "MSS" : "BoS", evt.Bullish ? "Bull" : "Bear", null, evt.Level, "");

            if (AlertOnStructure)
            {
                var kind = evt.IsMss ? "MSS" : "BoS";
                var arrow = evt.Bullish ? "📈 bullish" : "📉 bearish";
                var hint = evt.IsMss
                    ? (evt.Bullish ? "🔄 Trend flipping UP — look for longs on the retrace" : "🔄 Trend flipping DOWN — look for shorts on the retrace")
                    : "➡️ Trend continuation";
                Fire($"📐 {kind} {arrow}\n" +
                     $"📍 Broken level: {FormatPrice(evt.Level)}\n" +
                     hint);
            }

            if (!EntryModelEnabled || !evt.IsMss)
                return;

            // An MSS is fresh structural information for BOTH sides: it proves any
            // opposite armed setup was built on a failed shift (cancel it), and — with
            // ArmOnFailedMss — the traders trapped in that failure ARE the liquidity,
            // so the failed shift itself counts as the sweep precursor for the new side
            // (the trap / IFVG continuation entry).
            if (evt.Bullish)
            {
                var trappedShorts = false;

                if (CancelOnOppositeMss)
                {
                    trappedShorts = _armedBearUntil >= evt.Bar;

                    if (trappedShorts)
                        JournalEvent(evt.Bar, "FailedMSS", "Bear", null, evt.Level,
                            $"Short setup cancelled by bullish MSS @ {FormatPrice(evt.Level)}; " +
                            $"was armed at bar {_armedBearAtBar} (source={_armedBearSource}, {evt.Bar - _armedBearAtBar} bars in, " +
                            $"{_armedBearUntil - evt.Bar} bars of window left)" +
                            (ArmOnFailedMss ? "; long trap-armed" : ""));

                    _armedBearUntil = -1;
                    _armedBearAtBar = -1;
                    _armedBearSource = "";
                    _pendingBearSweepBar = -1;

                    if (trappedShorts && AlertOnFailedMss)
                        Fire("⚠️ Failed bearish MSS\n" +
                             "❌ Armed SHORT setup cancelled by a bullish MSS\n" +
                             (ArmOnFailedMss
                                 ? "🪤 Long side auto-armed off the trapped shorts (trap/IFVG entry)"
                                 : "👀 Failed shifts often fuel the opposite move — watch the new long side"));
                }

                var hadSweep = _pendingBullSweepBar > 0 && evt.Bar - _pendingBullSweepBar <= SweepToMssWindow;
                var sweepOk = !RequireSweepForEntry || hadSweep;
                if (sweepOk || (ArmOnFailedMss && trappedShorts))
                {
                    _armedBullUntil = evt.Bar + ArmWindowBars;
                    _armedBullAtBar = evt.Bar;
                    _armedBullSource = hadSweep && trappedShorts ? "Sweep+Trap"
                        : hadSweep ? "Sweep"
                        : trappedShorts ? "TrapArm"
                        : "MSS-only";

                    JournalEvent(evt.Bar, "Armed", "Bull", null, evt.Level,
                        $"Source={_armedBullSource}; MssBar={evt.Bar}; " +
                        $"SweepBar={(hadSweep ? _pendingBullSweepBar.ToString() : "none")}; " +
                        $"SweepAge={(hadSweep ? $"{evt.Bar - _pendingBullSweepBar}/{SweepToMssWindow}" : "n/a")}; " +
                        $"ArmedUntil=bar {_armedBullUntil} (+{ArmWindowBars})");
                }
                else
                {
                    var reason = _pendingBullSweepBar > 0
                        ? $"sell-side sweep too old: SweepBar={_pendingBullSweepBar}, age={evt.Bar - _pendingBullSweepBar} > window {SweepToMssWindow}"
                        : $"no sell-side sweep on record within {SweepToMssWindow} bars";
                    JournalEvent(evt.Bar, "ArmRejected", "Bull", null, evt.Level,
                        $"Bullish MSS @ {FormatPrice(evt.Level)} not armed; RequireSweep=on; {reason}; TrapArm={(ArmOnFailedMss ? "on, no armed short to trap" : "off")}");
                }
            }
            else
            {
                var trappedLongs = false;

                if (CancelOnOppositeMss)
                {
                    trappedLongs = _armedBullUntil >= evt.Bar;

                    if (trappedLongs)
                        JournalEvent(evt.Bar, "FailedMSS", "Bull", null, evt.Level,
                            $"Long setup cancelled by bearish MSS @ {FormatPrice(evt.Level)}; " +
                            $"was armed at bar {_armedBullAtBar} (source={_armedBullSource}, {evt.Bar - _armedBullAtBar} bars in, " +
                            $"{_armedBullUntil - evt.Bar} bars of window left)" +
                            (ArmOnFailedMss ? "; short trap-armed" : ""));

                    _armedBullUntil = -1;
                    _armedBullAtBar = -1;
                    _armedBullSource = "";
                    _pendingBullSweepBar = -1;

                    if (trappedLongs && AlertOnFailedMss)
                        Fire("⚠️ Failed bullish MSS\n" +
                             "❌ Armed LONG setup cancelled by a bearish MSS\n" +
                             (ArmOnFailedMss
                                 ? "🪤 Short side auto-armed off the trapped longs (trap/IFVG entry)"
                                 : "👀 Failed shifts often fuel the opposite move — watch the new short side"));
                }

                var hadSweep = _pendingBearSweepBar > 0 && evt.Bar - _pendingBearSweepBar <= SweepToMssWindow;
                var sweepOk = !RequireSweepForEntry || hadSweep;
                if (sweepOk || (ArmOnFailedMss && trappedLongs))
                {
                    _armedBearUntil = evt.Bar + ArmWindowBars;
                    _armedBearAtBar = evt.Bar;
                    _armedBearSource = hadSweep && trappedLongs ? "Sweep+Trap"
                        : hadSweep ? "Sweep"
                        : trappedLongs ? "TrapArm"
                        : "MSS-only";

                    JournalEvent(evt.Bar, "Armed", "Bear", null, evt.Level,
                        $"Source={_armedBearSource}; MssBar={evt.Bar}; " +
                        $"SweepBar={(hadSweep ? _pendingBearSweepBar.ToString() : "none")}; " +
                        $"SweepAge={(hadSweep ? $"{evt.Bar - _pendingBearSweepBar}/{SweepToMssWindow}" : "n/a")}; " +
                        $"ArmedUntil=bar {_armedBearUntil} (+{ArmWindowBars})");
                }
                else
                {
                    var reason = _pendingBearSweepBar > 0
                        ? $"buy-side sweep too old: SweepBar={_pendingBearSweepBar}, age={evt.Bar - _pendingBearSweepBar} > window {SweepToMssWindow}"
                        : $"no buy-side sweep on record within {SweepToMssWindow} bars";
                    JournalEvent(evt.Bar, "ArmRejected", "Bear", null, evt.Level,
                        $"Bearish MSS @ {FormatPrice(evt.Level)} not armed; RequireSweep=on; {reason}; TrapArm={(ArmOnFailedMss ? "on, no armed long to trap" : "off")}");
                }
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

            // Every state expiry leaves an explicit audit record — silence is not
            // an audit trail.
            if (_pendingBullSweepBar > 0 && bar - _pendingBullSweepBar > SweepToMssWindow)
            {
                JournalEvent(bar, "SweepExpired", "SellSide", null, 0m,
                    $"SweepBar={_pendingBullSweepBar}; age={bar - _pendingBullSweepBar} > window {SweepToMssWindow}; no bullish MSS followed");
                _pendingBullSweepBar = -1;
            }

            if (_pendingBearSweepBar > 0 && bar - _pendingBearSweepBar > SweepToMssWindow)
            {
                JournalEvent(bar, "SweepExpired", "BuySide", null, 0m,
                    $"SweepBar={_pendingBearSweepBar}; age={bar - _pendingBearSweepBar} > window {SweepToMssWindow}; no bearish MSS followed");
                _pendingBearSweepBar = -1;
            }

            if (_armedBullUntil > 0 && bar > _armedBullUntil)
            {
                JournalEvent(bar, "ArmExpired", "Bull", null, 0m,
                    $"Source={_armedBullSource}; ArmedAt=bar {_armedBullAtBar}; waited {bar - _armedBullAtBar} bars (window {ArmWindowBars}); no aligned zone was touched");
                _armedBullUntil = -1;
                _armedBullAtBar = -1;
                _armedBullSource = "";
            }

            if (_armedBearUntil > 0 && bar > _armedBearUntil)
            {
                JournalEvent(bar, "ArmExpired", "Bear", null, 0m,
                    $"Source={_armedBearSource}; ArmedAt=bar {_armedBearAtBar}; waited {bar - _armedBearAtBar} bars (window {ArmWindowBars}); no aligned zone was touched");
                _armedBearUntil = -1;
                _armedBearAtBar = -1;
                _armedBearSource = "";
            }

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
                var touched = _zones.Where(z =>
                    z.State != ZoneState.Mitigated &&
                    z.IsBullish &&
                    z.StartBar < bar &&
                    candle.Low <= z.Top).ToList();

                var matches = touched.Where(z =>
                    !EntryNeedsPdAlignment || eq == null || z.Mid <= eq.Value + tolerance).ToList();

                // Zones the PD filter vetoed — logged once per zone with the exact
                // numbers, so every filtered entry can be audited and back-scored.
                if (EntryNeedsPdAlignment && eq != null)
                {
                    foreach (var z in touched.Where(z => z.Mid > eq.Value + tolerance && !z.PdRejectLogged))
                    {
                        z.PdRejectLogged = true;
                        JournalEvent(bar, "EntryRejected", "Bull", z, candle.Close,
                            $"PD filter: zone mid {FormatPrice(z.Mid)} > limit {FormatPrice(eq.Value + tolerance)} " +
                            $"(EQ {FormatPrice(eq.Value)} + tol {FormatPrice(tolerance)}); excess {FormatPrice(z.Mid - eq.Value - tolerance)}; " +
                            $"ArmedSource={_armedBullSource}; ArmedAt=bar {_armedBullAtBar}");
                    }
                }

                if (matches.Count > 0)
                {
                    var source = _armedBullSource;
                    _armedBullUntil = -1;
                    _armedBullAtBar = -1;
                    _pendingBullSweepBar = -1;
                    _armedBullSource = "";
                    EmitEntrySignal(matches, longSide: true, eq, tolerance, source, bar);
                }
            }

            if (_armedBearUntil >= bar)
            {
                var touched = _zones.Where(z =>
                    z.State != ZoneState.Mitigated &&
                    !z.IsBullish &&
                    z.StartBar < bar &&
                    candle.High >= z.Bottom).ToList();

                var matches = touched.Where(z =>
                    !EntryNeedsPdAlignment || eq == null || z.Mid >= eq.Value - tolerance).ToList();

                if (EntryNeedsPdAlignment && eq != null)
                {
                    foreach (var z in touched.Where(z => z.Mid < eq.Value - tolerance && !z.PdRejectLogged))
                    {
                        z.PdRejectLogged = true;
                        JournalEvent(bar, "EntryRejected", "Bear", z, candle.Close,
                            $"PD filter: zone mid {FormatPrice(z.Mid)} < limit {FormatPrice(eq.Value - tolerance)} " +
                            $"(EQ {FormatPrice(eq.Value)} - tol {FormatPrice(tolerance)}); shortfall {FormatPrice(eq.Value - tolerance - z.Mid)}; " +
                            $"ArmedSource={_armedBearSource}; ArmedAt=bar {_armedBearAtBar}");
                    }
                }

                if (matches.Count > 0)
                {
                    var source = _armedBearSource;
                    _armedBearUntil = -1;
                    _armedBearAtBar = -1;
                    _pendingBearSweepBar = -1;
                    _armedBearSource = "";
                    EmitEntrySignal(matches, longSide: false, eq, tolerance, source, bar);
                }
            }
        }

        private void EmitEntrySignal(List<Zone> matches, bool longSide, decimal? eq, decimal tolerance, string armSource, int bar)
        {
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

            var record = new SignalRecord
            {
                Id = ++_nextSignalId,
                Time = BarTime(bar),
                Live = _realtime,
                Long = longSide,
                Tier = tierName,
                ArmSource = string.IsNullOrEmpty(armSource) ? "Unknown" : armSource,
                TriggerTag = trigger.Tag,
                TriggerType = trigger.Type,
                Layer = trigger.IsHtf ? trigger.HtfLabel : "LTF",
                ZoneTop = trigger.Top,
                ZoneBottom = trigger.Bottom,
                Entry = entry,
                Sl = sl,
                Tp2 = tp2,
                Tp3 = tp3,
                PdStatus = pdStatus,
                Confluence = confluence,
                SignalBar = bar,
                TriggerZoneId = trigger.Id
            };

            JournalSignal(record);

            if (!AlertOnEntry)
                return;

            Fire($"{mark} {dir} ENTRY — {tierName} setup\n" +
                 $"📍 Zone: {trigger.Tag} {FormatPrice(trigger.Bottom)}–{FormatPrice(trigger.Top)}\n" +
                 $"🧩 Confluence: {confluence}\n" +
                 $"⚖️ Range position: {pdStatus}\n" +
                 $"▶️ Entry: ~{FormatPrice(entry)}\n" +
                 $"🛑 Stop: {FormatPrice(sl)}\n" +
                 $"🎯 Targets: 2R {FormatPrice(tp2)} · 3R {FormatPrice(tp3)}\n" +
                 "✅ Confirm first: rejection wick / lower-TF MSS");
        }

        /// <summary>
        /// C-tier "Non-ICT concept" continuation signal: fires on the FIRST touch
        /// of a fresh, trend-aligned zone that the core sweep→MSS→discount model
        /// would NOT trade — either because no setup is armed, or because the zone
        /// sits beyond the PD limit (premium/discount continuation). Deliberately
        /// the lowest tier: it exists so the journal measures the momentum-
        /// continuation play with real outcomes instead of excluding it untracked.
        /// A++/A+/B logic is untouched; when the armed model can fire from this
        /// zone, C stays silent (no double signal).
        /// </summary>
        private void TryEmitContinuationSignal(Zone zone, int bar)
        {
            if (!EntryModelEnabled || !ContinuationSignalsEnabled || zone.ContinuationFired)
                return;

            var longSide = zone.IsBullish;

            // Continuation only: trade strictly with the tracked structure trend.
            if (_trend != (longSide ? 1 : -1))
                return;

            // Fresh zones only — the concept is a newly minted imbalance in a move.
            if (bar - zone.StartBar > ContinuationMaxAgeBars)
                return;

            var range = GetDealingRange();
            decimal? eq = null;
            var tolerance = 0m;
            if (range.HasValue)
            {
                eq = (range.Value.High.Price + range.Value.Low.Price) / 2m;
                tolerance = (range.Value.High.Price - range.Value.Low.Price) * PdTolerancePercent / 100m;
            }

            var pdOk = !EntryNeedsPdAlignment || eq == null ||
                       (longSide ? zone.Mid <= eq.Value + tolerance : zone.Mid >= eq.Value - tolerance);
            var armed = longSide ? _armedBullUntil >= bar : _armedBearUntil >= bar;

            // The core model owns this touch — it can (or just did) fire an A/B signal.
            if (armed && pdOk)
                return;

            zone.ContinuationFired = true;

            var reason = armed
                ? $"PD override: zone mid {FormatPrice(zone.Mid)} beyond EQ limit"
                : "no sweep→MSS chain";

            var buffer = SlBufferTicks * TickSize;
            var entry = longSide ? zone.Top : zone.Bottom;
            var sl = longSide ? zone.Bottom - buffer : zone.Top + buffer;
            var risk = Math.Abs(entry - sl);
            var tp2 = longSide ? entry + risk * 2 : entry - risk * 2;
            var tp3 = longSide ? entry + risk * 3 : entry - risk * 3;

            var pdStatus = "n/a";
            if (eq.HasValue)
            {
                pdStatus = zone.Mid < eq.Value - tolerance ? "Discount"
                    : zone.Mid <= eq.Value + tolerance ? "Near EQ"
                    : "Premium";
            }

            var record = new SignalRecord
            {
                Id = ++_nextSignalId,
                Time = BarTime(bar),
                Live = _realtime,
                Long = longSide,
                Tier = "C",
                ArmSource = "Continuation",
                TriggerTag = zone.Tag,
                TriggerType = zone.Type,
                Layer = zone.IsHtf ? zone.HtfLabel : "LTF",
                ZoneTop = zone.Top,
                ZoneBottom = zone.Bottom,
                Entry = entry,
                Sl = sl,
                Tp2 = tp2,
                Tp3 = tp3,
                PdStatus = pdStatus,
                Confluence = $"Non-ICT concept · momentum continuation · {reason}",
                SignalBar = bar,
                TriggerZoneId = zone.Id
            };

            JournalSignal(record);

            if (!AlertOnEntry)
                return;

            var counterPd = pdStatus == (longSide ? "Premium" : "Discount");
            Fire($"🟡 C-TIER {(longSide ? "LONG" : "SHORT")} — Continuation (Non-ICT)\n" +
                 $"📍 Zone: {zone.Tag} {FormatPrice(zone.Bottom)}–{FormatPrice(zone.Top)} (fresh, {bar - zone.StartBar} bars old)\n" +
                 $"⚖️ Range position: {pdStatus}{(counterPd ? " — counter-PD" : "")}\n" +
                 $"ℹ️ Outside core model: {reason}\n" +
                 $"▶️ Entry: ~{FormatPrice(entry)}\n" +
                 $"🛑 Stop: {FormatPrice(sl)}\n" +
                 $"🎯 Targets: 2R {FormatPrice(tp2)} · 3R {FormatPrice(tp3)}\n" +
                 "✅ Lowest tier — demand strong LTF confirmation");
        }

        /// <summary>
        /// Current dealing range. With DealingRangeFromLeg (default) the range is
        /// the CURRENT impulse leg — origin extreme to running extreme, re-anchored
        /// on every BoS/MSS — so equilibrium is structural and current, and valid
        /// post-MSS retrace zones are measured against the leg they belong to
        /// rather than a stale pre-break top/bottom. Falls back to the confirmed
        /// swing pair (order-corrected) before the first structure break or when
        /// the leg toggle is off.
        /// </summary>
        private (SwingPoint High, SwingPoint Low)? GetDealingRange()
        {
            if (DealingRangeFromLeg && _legDirection != 0 && _legAnchor != null && _legExtreme != null)
            {
                var legHigh = _legDirection == 1 ? _legExtreme : _legAnchor;
                var legLow = _legDirection == 1 ? _legAnchor : _legExtreme;

                if (legHigh.Price > legLow.Price)
                    return (legHigh, legLow);
            }

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
            Fire($"📦 New zone — {zone.Tag}\n📍 Range: {FormatPrice(zone.Bottom)}–{FormatPrice(zone.Top)}");
        }

        /// <summary>
        /// Central alert dispatcher. Alerts fire only in realtime (never while the
        /// indicator replays history) and go to ATAS popups and/or Telegram.
        /// Popups get a flattened one-liner; Telegram gets the structured
        /// multi-line card with a bold header (HTML mode).
        /// </summary>
        private void Fire(string message)
        {
            if (!_realtime)
                return;

            var instrument = InstrumentInfo?.Instrument ?? "";

            if (UsePopupAlerts)
            {
                try
                {
                    var flat = message.Replace("\n", " | ");
                    AddAlert(AlertFile, string.IsNullOrEmpty(instrument) ? flat : $"[{instrument}] {flat}");
                }
                catch
                {
                    // Alert subsystem unavailable (e.g. during optimization) — ignore.
                }
            }

            if (TelegramEnabled)
            {
                // Telegram identity includes the chart timeframe: "GC 1H", "NQ 15m".
                var identity = string.IsNullOrEmpty(_chartTfLabel) || string.IsNullOrEmpty(instrument)
                    ? instrument
                    : $"{instrument} {_chartTfLabel}";
                SendTelegram(identity, message);
            }
        }

        private static string EscapeHtml(string text) =>
            text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private void SendTelegram(string instrument, string text)
        {
            var token = TelegramBotToken?.Trim();
            var chatId = TelegramChatId?.Trim();

            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chatId))
                return;

            // Structured card: bold headline, body lines as-is, instrument footer.
            var lines = text.Split('\n');
            var sb = new System.Text.StringBuilder();
            sb.Append("<b>").Append(EscapeHtml(lines[0])).Append("</b>");

            for (var i = 1; i < lines.Length; i++)
                sb.Append('\n').Append(EscapeHtml(lines[i]));

            if (!string.IsNullOrEmpty(instrument))
                sb.Append("\n\n").Append("💹 <b>").Append(EscapeHtml(instrument)).Append("</b>");

            var html = sb.ToString();

            _ = Task.Run(async () =>
            {
                try
                {
                    var url = $"https://api.telegram.org/bot{token}/sendMessage";
                    var payload = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("chat_id", chatId),
                        new KeyValuePair<string, string>("text", html),
                        new KeyValuePair<string, string>("parse_mode", "HTML"),
                        new KeyValuePair<string, string>("disable_web_page_preview", "true")
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
