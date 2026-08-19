using System;
using System.Collections.Generic;
using System.Globalization;
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

        // Killzone rejections are journaled at most once per bar per side.
        private int _kzRejectBullBar = -1;
        private int _kzRejectBearBar = -1;

        #region Zone contact geometry

        /// <summary>
        /// True when the candle's range actually OVERLAPS the zone.
        ///
        /// The previous one-sided test (`low &lt;= top` for a bullish zone) stayed true
        /// for as long as price was anywhere BELOW the zone — including far below it.
        /// For FVGs that self-healed, because FullFill mitigation kills the zone in the
        /// same pass; for ORDER BLOCKS it did not, because BodyClose mitigation can only
        /// be judged on a finalized candle. So while price sliced down through a bullish
        /// OB, the block still counted as "touched" and the entry model could fire a
        /// LONG whose quoted entry sat above the market and whose stop price had already
        /// traded. A range overlap is the correct contact test in both directions.
        /// </summary>
        private static bool ZoneInContact(Zone zone, decimal high, decimal low) =>
            low <= zone.Top && high >= zone.Bottom;

        /// <summary>
        /// True when price actually traded THROUGH the zone's proximal edge on this
        /// bar — the edge the trade plan quotes as its entry. This is stricter than
        /// mere contact and is what an entry signal requires: it proves price returned
        /// into the zone from the correct side rather than being parked beyond it.
        /// </summary>
        private static bool EntryEdgeTraded(Zone zone, decimal high, decimal low) => zone.IsBullish
            ? high >= zone.Top && low <= zone.Top
            : low <= zone.Bottom && high >= zone.Bottom;

        #endregion

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
                MarkRenderDirty();

                // Entry-model precursor: taking sell-side liquidity primes LONGS,
                // taking buy-side liquidity primes SHORTS.
                if (level.BuySide)
                    _pendingBearSweepBar = bar;
                else
                    _pendingBullSweepBar = bar;

                JournalEvent(bar, "LiquiditySweep", level.BuySide ? "BuySide" : "SellSide", null, level.Price,
                    level.Label);

                if (!level.SweptAlerted && AlertOnSweep)
                {
                    level.SweptAlerted = true;
                    var side = level.BuySide ? "Buy-side" : "Sell-side";
                    Fire($"💧 Liquidity taken — {side} · {level.Label}\n" +
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

                var inContact = ZoneInContact(zone, candle.High, candle.Low);

                if (inContact)
                {
                    // Distinct touch episodes: contact on consecutive bars is ONE episode;
                    // a full untouched bar in between separates two.
                    var isRetouch = zone.LastTouchedBar >= 0 && bar > zone.LastTouchedBar + 1;
                    zone.LastTouchedBar = bar;
                    MarkRenderDirty();

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
                        JournalEvent(bar, "ZoneTouch", zone.IsBullish ? "Bull" : "Bear", zone, candle.Close, "");
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
                }

                // Mitigation is evaluated whether or not the candle is in CONTACT with
                // the zone. Midline and FullFill are level-crossing tests, and price can
                // cross clean past a zone in a single candle without its range ever
                // overlapping the zone (a gap or one violent bar). Gating them on contact
                // would leave such a zone alive and tradeable forever.
                switch (rule)
                {
                    case MitigationRule.AnyTouch:
                        if (inContact)
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
            //
            // That hand-off is BUDGETED. Left unbounded it let the model bootstrap
            // itself: sweep → arm long → bearish MSS traps it → arm short (no sweep) →
            // bullish MSS traps that → arm long (no sweep) → … Because the tracked trend
            // flips on every alternating break, every alternating break is an MSS, so in
            // a range the machine ping-ponged forever off ONE historical sweep while
            // RequireSweepForEntry was on. MaxTrapChainHops caps how far an arming may
            // sit from a real liquidity sweep.
            if (evt.Bullish)
            {
                var trappedShorts = false;
                var bearDepth = 0;

                if (CancelOnOppositeMss)
                {
                    trappedShorts = _armedBearUntil >= evt.Bar;
                    bearDepth = _armedBearTrapDepth;

                    if (trappedShorts)
                        JournalEvent(evt.Bar, "FailedMSS", "Bear", null, evt.Level,
                            $"Short setup cancelled by bullish MSS @ {FormatPrice(evt.Level)}; " +
                            $"was armed at bar {_armedBearAtBar} (source={_armedBearSource}, depth={bearDepth}, " +
                            $"{evt.Bar - _armedBearAtBar} bars in, {_armedBearUntil - evt.Bar} bars of window left)");

                    _armedBearUntil = -1;
                    _armedBearAtBar = -1;
                    _armedBearSource = "";
                    _armedBearTrapDepth = 0;
                    _pendingBearSweepBar = -1;

                    if (trappedShorts && AlertOnFailedMss)
                        Fire("⚠️ Failed bearish MSS\n" +
                             "❌ Armed SHORT setup cancelled by a bullish MSS\n" +
                             "👀 Failed shifts often fuel the opposite move — watch the new long side");
                }

                var hadSweep = _pendingBullSweepBar > 0 && evt.Bar - _pendingBullSweepBar <= SweepToMssWindow;
                var sweepOk = !RequireSweepForEntry || hadSweep;

                var trapDepth = bearDepth + 1;
                var trapArmAllowed = ArmOnFailedMss && trappedShorts && trapDepth <= MaxTrapChainHops;

                if (sweepOk || trapArmAllowed)
                {
                    _armedBullUntil = evt.Bar + ArmWindowBars;
                    _armedBullAtBar = evt.Bar;
                    _armedBullTrapDepth = hadSweep || !RequireSweepForEntry ? 0 : trapDepth;
                    _armedBullSource = hadSweep && trappedShorts ? "Sweep+Trap"
                        : hadSweep ? "Sweep"
                        : trapArmAllowed ? "TrapArm"
                        : "MSS-only";

                    JournalEvent(evt.Bar, "Armed", "Bull", null, evt.Level,
                        $"Source={_armedBullSource}; TrapDepth={_armedBullTrapDepth}/{MaxTrapChainHops}; MssBar={evt.Bar}; " +
                        $"SweepBar={(hadSweep ? _pendingBullSweepBar.ToString(CultureInfo.InvariantCulture) : "none")}; " +
                        $"SweepAge={(hadSweep ? $"{evt.Bar - _pendingBullSweepBar}/{SweepToMssWindow}" : "n/a")}; " +
                        $"ArmedUntil=bar {_armedBullUntil} (+{ArmWindowBars})");
                }
                else
                {
                    var reason = _pendingBullSweepBar > 0
                        ? $"sell-side sweep too old: SweepBar={_pendingBullSweepBar}, age={evt.Bar - _pendingBullSweepBar} > window {SweepToMssWindow}"
                        : $"no sell-side sweep on record within {SweepToMssWindow} bars";
                    var trapNote = !ArmOnFailedMss ? "trap-arm off"
                        : !trappedShorts ? "no armed short to trap"
                        : $"trap budget spent: depth {trapDepth} > MaxTrapChainHops {MaxTrapChainHops}";

                    JournalEvent(evt.Bar, "ArmRejected", "Bull", null, evt.Level,
                        $"Bullish MSS @ {FormatPrice(evt.Level)} not armed; RequireSweep=on; {reason}; TrapArm: {trapNote}");
                }
            }
            else
            {
                var trappedLongs = false;
                var bullDepth = 0;

                if (CancelOnOppositeMss)
                {
                    trappedLongs = _armedBullUntil >= evt.Bar;
                    bullDepth = _armedBullTrapDepth;

                    if (trappedLongs)
                        JournalEvent(evt.Bar, "FailedMSS", "Bull", null, evt.Level,
                            $"Long setup cancelled by bearish MSS @ {FormatPrice(evt.Level)}; " +
                            $"was armed at bar {_armedBullAtBar} (source={_armedBullSource}, depth={bullDepth}, " +
                            $"{evt.Bar - _armedBullAtBar} bars in, {_armedBullUntil - evt.Bar} bars of window left)");

                    _armedBullUntil = -1;
                    _armedBullAtBar = -1;
                    _armedBullSource = "";
                    _armedBullTrapDepth = 0;
                    _pendingBullSweepBar = -1;

                    if (trappedLongs && AlertOnFailedMss)
                        Fire("⚠️ Failed bullish MSS\n" +
                             "❌ Armed LONG setup cancelled by a bearish MSS\n" +
                             "👀 Failed shifts often fuel the opposite move — watch the new short side");
                }

                var hadSweep = _pendingBearSweepBar > 0 && evt.Bar - _pendingBearSweepBar <= SweepToMssWindow;
                var sweepOk = !RequireSweepForEntry || hadSweep;

                var trapDepth = bullDepth + 1;
                var trapArmAllowed = ArmOnFailedMss && trappedLongs && trapDepth <= MaxTrapChainHops;

                if (sweepOk || trapArmAllowed)
                {
                    _armedBearUntil = evt.Bar + ArmWindowBars;
                    _armedBearAtBar = evt.Bar;
                    _armedBearTrapDepth = hadSweep || !RequireSweepForEntry ? 0 : trapDepth;
                    _armedBearSource = hadSweep && trappedLongs ? "Sweep+Trap"
                        : hadSweep ? "Sweep"
                        : trapArmAllowed ? "TrapArm"
                        : "MSS-only";

                    JournalEvent(evt.Bar, "Armed", "Bear", null, evt.Level,
                        $"Source={_armedBearSource}; TrapDepth={_armedBearTrapDepth}/{MaxTrapChainHops}; MssBar={evt.Bar}; " +
                        $"SweepBar={(hadSweep ? _pendingBearSweepBar.ToString(CultureInfo.InvariantCulture) : "none")}; " +
                        $"SweepAge={(hadSweep ? $"{evt.Bar - _pendingBearSweepBar}/{SweepToMssWindow}" : "n/a")}; " +
                        $"ArmedUntil=bar {_armedBearUntil} (+{ArmWindowBars})");
                }
                else
                {
                    var reason = _pendingBearSweepBar > 0
                        ? $"buy-side sweep too old: SweepBar={_pendingBearSweepBar}, age={evt.Bar - _pendingBearSweepBar} > window {SweepToMssWindow}"
                        : $"no buy-side sweep on record within {SweepToMssWindow} bars";
                    var trapNote = !ArmOnFailedMss ? "trap-arm off"
                        : !trappedLongs ? "no armed long to trap"
                        : $"trap budget spent: depth {trapDepth} > MaxTrapChainHops {MaxTrapChainHops}";

                    JournalEvent(evt.Bar, "ArmRejected", "Bear", null, evt.Level,
                        $"Bearish MSS @ {FormatPrice(evt.Level)} not armed; RequireSweep=on; {reason}; TrapArm: {trapNote}");
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
                _armedBullTrapDepth = 0;
            }

            if (_armedBearUntil > 0 && bar > _armedBearUntil)
            {
                JournalEvent(bar, "ArmExpired", "Bear", null, 0m,
                    $"Source={_armedBearSource}; ArmedAt=bar {_armedBearAtBar}; waited {bar - _armedBearAtBar} bars (window {ArmWindowBars}); no aligned zone was touched");
                _armedBearUntil = -1;
                _armedBearAtBar = -1;
                _armedBearSource = "";
                _armedBearTrapDepth = 0;
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

            var inKillzone = InKillzone(bar);

            if (_armedBullUntil >= bar)
            {
                if (!inKillzone)
                {
                    // The setup is NOT consumed — it stays armed and can still fire once
                    // the session opens, which is exactly how a killzone is traded.
                    if (_kzRejectBullBar != bar)
                    {
                        _kzRejectBullBar = bar;
                        JournalEvent(bar, "EntryRejected", "Bull", null, candle.Close,
                            $"Killzone filter: {BarTime(bar):HH:mm} outside [{KillzoneWindows}]; setup stays armed until bar {_armedBullUntil}");
                    }
                }
                else
                {
                    var candidates = _zones.Where(z =>
                        z.State != ZoneState.Mitigated &&
                        z.IsBullish &&
                        z.StartBar < bar &&
                        ZoneInContact(z, candle.High, candle.Low)).ToList();

                    var touched = candidates
                        .Where(z => EntryEdgeTraded(z, candle.High, candle.Low))
                        .ToList();

                    // In contact but the proximal edge never traded this bar: price was
                    // already inside or beyond the zone when the model armed, so quoting
                    // that edge as the entry would put the plan on the wrong side of the
                    // market. The setup is not consumed — it can still fire on a clean
                    // re-entry — but the suppression is journaled, never silent.
                    foreach (var z in candidates.Where(z => !z.EdgeRejectLogged && !touched.Contains(z)))
                    {
                        z.EdgeRejectLogged = true;
                        JournalEvent(bar, "EntryRejected", "Bull", z, candle.Close,
                            $"contact without an edge cross: entry {FormatPrice(z.Top)} was not traded on this bar " +
                            "(price already inside/beyond the zone when armed); setup stays armed");
                    }

                    var matches = touched.Where(z => PdAligned(z, true, eq, tolerance)).ToList();

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

                    matches = FilterByOte(matches, true, bar, candle.Close);

                    if (matches.Count > 0)
                    {
                        var source = _armedBullSource;
                        _armedBullUntil = -1;
                        _armedBullAtBar = -1;
                        _pendingBullSweepBar = -1;
                        _armedBullSource = "";
                        _armedBullTrapDepth = 0;
                        EmitEntrySignal(matches, longSide: true, eq, tolerance, source, bar);
                    }
                }
            }

            if (_armedBearUntil >= bar)
            {
                if (!inKillzone)
                {
                    if (_kzRejectBearBar != bar)
                    {
                        _kzRejectBearBar = bar;
                        JournalEvent(bar, "EntryRejected", "Bear", null, candle.Close,
                            $"Killzone filter: {BarTime(bar):HH:mm} outside [{KillzoneWindows}]; setup stays armed until bar {_armedBearUntil}");
                    }
                }
                else
                {
                    var candidates = _zones.Where(z =>
                        z.State != ZoneState.Mitigated &&
                        !z.IsBullish &&
                        z.StartBar < bar &&
                        ZoneInContact(z, candle.High, candle.Low)).ToList();

                    var touched = candidates
                        .Where(z => EntryEdgeTraded(z, candle.High, candle.Low))
                        .ToList();

                    // In contact but the proximal edge never traded this bar: price was
                    // already inside or beyond the zone when the model armed, so quoting
                    // that edge as the entry would put the plan on the wrong side of the
                    // market. The setup is not consumed — it can still fire on a clean
                    // re-entry — but the suppression is journaled, never silent.
                    foreach (var z in candidates.Where(z => !z.EdgeRejectLogged && !touched.Contains(z)))
                    {
                        z.EdgeRejectLogged = true;
                        JournalEvent(bar, "EntryRejected", "Bear", z, candle.Close,
                            $"contact without an edge cross: entry {FormatPrice(z.Bottom)} was not traded on this bar " +
                            "(price already inside/beyond the zone when armed); setup stays armed");
                    }

                    var matches = touched.Where(z => PdAligned(z, false, eq, tolerance)).ToList();

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

                    matches = FilterByOte(matches, false, bar, candle.Close);

                    if (matches.Count > 0)
                    {
                        var source = _armedBearSource;
                        _armedBearUntil = -1;
                        _armedBearAtBar = -1;
                        _pendingBearSweepBar = -1;
                        _armedBearSource = "";
                        _armedBearTrapDepth = 0;
                        EmitEntrySignal(matches, longSide: false, eq, tolerance, source, bar);
                    }
                }
            }
        }

        private bool PdAligned(Zone zone, bool longSide, decimal? eq, decimal tolerance)
        {
            if (!EntryNeedsPdAlignment || eq == null)
                return true;

            return longSide ? zone.Mid <= eq.Value + tolerance : zone.Mid >= eq.Value - tolerance;
        }

        /// <summary>
        /// Optional OTE refinement (ICT's 0.618–0.79 "optimal trade entry" pocket of the
        /// current impulse leg). Applies only when the leg direction matches the trade
        /// side — a retracement pocket is undefined against the leg — so it narrows
        /// entries without ever silently vetoing a whole side.
        /// </summary>
        private List<Zone> FilterByOte(List<Zone> matches, bool longSide, int bar, decimal close)
        {
            if (!OteFilterEnabled || matches.Count == 0)
                return matches;

            if (_legDirection != (longSide ? 1 : -1))
                return matches;

            var band = GetOteBand();
            if (!band.HasValue)
                return matches;

            var kept = new List<Zone>();

            foreach (var z in matches)
            {
                if (z.Mid <= band.Value.Top && z.Mid >= band.Value.Bottom)
                {
                    kept.Add(z);
                    continue;
                }

                if (!z.OteRejectLogged)
                {
                    z.OteRejectLogged = true;
                    JournalEvent(bar, "EntryRejected", longSide ? "Bull" : "Bear", z, close,
                        $"OTE filter: zone mid {FormatPrice(z.Mid)} outside " +
                        $"{FormatPrice(band.Value.Bottom)}–{FormatPrice(band.Value.Top)} " +
                        $"({OteMinPercent:0.#}%–{OteMaxPercent:0.#}% retracement of the current leg)");
                }
            }

            return kept;
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
            var candle = GetCandle(bar);

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
                TriggerZoneId = trigger.Id,
                HighAtSignal = candle.High,
                LowAtSignal = candle.Low
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

            var candle = GetCandle(bar);

            // Same contact discipline as the core model: the quoted entry edge must
            // actually have traded on this bar, so a C-tier plan can never be printed
            // with its entry sitting on the wrong side of the market.
            if (!EntryEdgeTraded(zone, candle.High, candle.Low))
                return;

            if (!InKillzone(bar))
                return;

            var range = GetDealingRange();
            decimal? eq = null;
            var tolerance = 0m;
            if (range.HasValue)
            {
                eq = (range.Value.High.Price + range.Value.Low.Price) / 2m;
                tolerance = (range.Value.High.Price - range.Value.Low.Price) * PdTolerancePercent / 100m;
            }

            var pdOk = PdAligned(zone, longSide, eq, tolerance);
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
                TriggerZoneId = zone.Id,
                HighAtSignal = candle.High,
                LowAtSignal = candle.Low
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

        /// <summary>
        /// The OTE pocket of the CURRENT impulse leg: the configured retracement band
        /// (0.618–0.79 by default) measured back from the leg's extreme toward its
        /// origin. Null before the first structure break or on a degenerate leg.
        /// </summary>
        private (decimal Top, decimal Bottom)? GetOteBand()
        {
            if (_legDirection == 0 || _legAnchor == null || _legExtreme == null)
                return null;

            var high = _legDirection == 1 ? _legExtreme.Price : _legAnchor.Price;
            var low = _legDirection == 1 ? _legAnchor.Price : _legExtreme.Price;
            var span = high - low;

            if (span <= 0m)
                return null;

            var near = Math.Min(OteMinPercent, OteMaxPercent) / 100m;
            var far = Math.Max(OteMinPercent, OteMaxPercent) / 100m;

            if (_legDirection == 1)
                return (high - span * near, high - span * far);

            return (low + span * far, low + span * near);
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
                catch (Exception ex)
                {
                    // Alert subsystem unavailable (e.g. during optimization), or the
                    // configured sound file does not exist. Never rethrow onto the chart
                    // thread — but leave a trace, because a silently swallowed popup
                    // failure is indistinguishable from "no alerts fired".
                    JournalEvent(_lastSeenBar, "AlertFailed", "", null, 0m,
                        $"popup alert failed (sound file '{AlertFile}'): {ex.GetType().Name}: {ex.Message}");
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
