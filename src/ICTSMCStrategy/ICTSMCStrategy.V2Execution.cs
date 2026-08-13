using System;
using System.Collections.Generic;
using System.Linq;

namespace ICTSMC
{
    /// <summary>
    /// V2 execution path. Rendering-facing zone geometry remains in the existing
    /// models; this file owns only causal eligibility, fills, and outcome order.
    /// </summary>
    public partial class ICTSMCStrategy
    {
        private readonly struct ZoneContact
        {
            public readonly Zone Zone;
            public readonly ZoneContactKind Kind;

            public ZoneContact(Zone zone, ZoneContactKind kind)
            {
                Zone = zone;
                Kind = kind;
            }
        }

        // Reused on the hot tick path: the indicator must not allocate a fresh
        // contact collection for every market update.
        private readonly List<ZoneContact> _observationContacts = new(32);

        /// <summary>
        /// Internal structure remains a chart/context event. V2 journals and
        /// alerts it, but never lets it arm the strict reversal model; only the
        /// independently-confirmed external structure layer may do that.
        /// </summary>
        private void OnStructureEvent(StructureEvent evt)
        {
            JournalEvent(evt.Bar, evt.IsMss ? "InternalMSS" : "InternalBoS",
                evt.Bullish ? "Bull" : "Bear", null, evt.Level, "context only; cannot arm strict entry");

            if (!AlertOnStructure)
                return;

            var kind = evt.IsMss ? "internal MSS" : "internal BoS";
            Fire($"📐 {kind} {(evt.Bullish ? "bullish" : "bearish")}\n" +
                 $"📍 Broken level: {FormatPrice(evt.Level)}\n" +
                 "ℹ️ Context only — strict entries require an external MSS after a confirmed trap");
        }

        private void ProcessV2Observation(int bar, decimal value)
        {
            ExpireStrictSetups(bar);

            var observed = value != 0m ? value : GetCandle(bar).Close;
            var previous = _lastObservedPrice ?? GetCandle(Math.Max(0, bar - 1)).Close;
            _observationSequence++;

            // Existing filled positions own the tick before any new setup can use it.
            UpdateOpenSignalsOnObservation(bar, previous, observed, _observationSequence);
            ProcessLiveLiquidityCrosses(bar, previous, observed);

            var contacts = CollectLiveZoneContacts(bar, previous, observed);
            EvaluateStrictSetupsForLiveContacts(bar, observed, contacts);
            ApplyZoneContacts(bar, observed, contacts, isOhlc: false);

            _lastObservedPrice = observed;
            _lastObservedPriceBar = bar;
        }

        private void ProcessOhlcObservation(int bar)
        {
            var candle = GetCandle(bar);
            ProcessOhlcLiquidityCrosses(bar, candle.High, candle.Low);
            var contacts = CollectOhlcZoneContacts(bar, candle.High, candle.Low);

            // OHLC tells us that a presentation may have happened, not its sequence.
            // A strict historical candidate is recorded as ambiguous rather than
            // becoming a fabricated filled trade or contaminating expectancy.
            EvaluateStrictSetupsForOhlcContacts(bar, candle.High, candle.Low, contacts);
            ApplyZoneContacts(bar, candle.Close, contacts, isOhlc: true, ohlcHigh: candle.High, ohlcLow: candle.Low);
        }

        private void ProcessLiveLiquidityCrosses(int bar, decimal previous, decimal current)
        {
            foreach (var level in _liquidity)
            {
                if (level.Swept || level.StartBar >= bar)
                    continue;

                var crossed = level.BuySide
                    ? previous <= level.Price && current > level.Price
                    : previous >= level.Price && current < level.Price;
                if (!crossed)
                    continue;

                RegisterTakenLiquidity(level, bar, current);
            }
        }

        private void ProcessOhlcLiquidityCrosses(int bar, decimal high, decimal low)
        {
            foreach (var level in _liquidity)
            {
                if (level.Swept || level.StartBar >= bar)
                    continue;

                var crossed = level.BuySide ? high > level.Price : low < level.Price;
                if (crossed)
                    RegisterTakenLiquidity(level, bar, level.Price);
            }
        }

        private void RegisterTakenLiquidity(LiquidityLevel level, int bar, decimal observedPrice)
        {
            level.Swept = true;
            level.SweptBar = bar;
            level.WasTrap = null;

            var evt = new LiquidityEvent
            {
                Id = ++_nextLiquidityEventId,
                LiquidityLevelId = level.Id,
                LongSetup = !level.BuySide,
                BuySide = level.BuySide,
                Level = level.Price,
                TakenBar = bar,
                TakenTime = BarTime(bar),
                MaximumPenetration = Math.Abs(observedPrice - level.Price),
                Disposition = LiquidityDisposition.TakenPendingClose
            };
            level.LiquidityEventId = evt.Id;
            _liquidityEvents.Add(evt);

            JournalEvent(bar, "LiquidityTakenPendingClose", level.BuySide ? "BuySide" : "SellSide", null,
                level.Price, level.IsEqual ? "Equal highs/lows pool" : "");

            if (!level.SweptAlerted && AlertOnSweep)
            {
                level.SweptAlerted = true;
                var side = level.BuySide ? "Buy-side" : "Sell-side";
                Fire($"💧 Liquidity taken — {side}\n" +
                     $"📍 Level: {FormatPrice(level.Price)}\n" +
                     "⏳ Awaiting candle-close trap/run classification before a strict setup can arm");
            }
        }

        private List<ZoneContact> CollectLiveZoneContacts(int bar, decimal previous, decimal current)
        {
            var contacts = _observationContacts;
            contacts.Clear();
            foreach (var zone in _zones)
            {
                if (zone.State == ZoneState.Mitigated || zone.StartBar >= bar)
                    continue;

                var kind = StrictRules.ClassifyObservedContact(zone.IsBullish, previous, current, zone.Top, zone.Bottom);
                if (kind == ZoneContactKind.None)
                    continue;

                if (bar < zone.EligibleFromBar)
                {
                    zone.PreConfirmationTouched = true;
                    JournalEvent(bar, "PreConfirmationTouch", zone.IsBullish ? "Bull" : "Bear", zone, current,
                        $"zone eligible from bar {zone.EligibleFromBar}; contact ignored for execution");
                    continue;
                }

                contacts.Add(new ZoneContact(zone, kind));
            }

            return contacts;
        }

        private List<ZoneContact> CollectOhlcZoneContacts(int bar, decimal high, decimal low)
        {
            var contacts = _observationContacts;
            contacts.Clear();
            foreach (var zone in _zones)
            {
                if (zone.State == ZoneState.Mitigated || zone.StartBar >= bar)
                    continue;
                if (!StrictRules.HasOhlcIntersection(high, low, zone.Top, zone.Bottom))
                    continue;

                if (bar < zone.EligibleFromBar)
                {
                    zone.PreConfirmationTouched = true;
                    continue;
                }

                contacts.Add(new ZoneContact(zone, ZoneContactKind.OhlcPossible));
            }

            return contacts;
        }

        private void EvaluateStrictSetupsForLiveContacts(int bar, decimal observed, List<ZoneContact> contacts)
        {
            RegisterUnfilledGapContacts(_bullStrictSetup, bar, observed, contacts, longSide: true);
            RegisterUnfilledGapContacts(_bearStrictSetup, bar, observed, contacts, longSide: false);
            EvaluateStrictSetupForLiveContacts(_bullStrictSetup, bar, observed, contacts, longSide: true);
            EvaluateStrictSetupForLiveContacts(_bearStrictSetup, bar, observed, contacts, longSide: false);
        }

        private void EvaluateStrictSetupForLiveContacts(StrictSetup setup, int bar, decimal observed,
            List<ZoneContact> contacts, bool longSide)
        {
            if (setup is not { Status: SetupStatus.Armed })
                return;

            Zone match = null;
            for (var i = 0; i < contacts.Count; i++)
            {
                var contact = contacts[i];
                if (contact.Kind != ZoneContactKind.EnteredFromExpectedSide || contact.Zone.IsBullish != longSide ||
                    !IsStrictPoiForSetup(contact.Zone, setup))
                    continue;

                if (match == null || (setup.Long ? contact.Zone.Top > match.Top : contact.Zone.Bottom < match.Bottom))
                    match = contact.Zone;
            }
            if (match == null)
                return;

            if (EntryNeedsPdAlignment && !StrictRules.PassesPremiumDiscount(setup.Long, observed,
                    setup.RangeHigh, setup.RangeLow,
                    (setup.RangeHigh - setup.RangeLow) * PdTolerancePercent / 100m))
            {
                if (!match.PdRejectLogged)
                {
                    match.PdRejectLogged = true;
                    JournalEvent(bar, "EntryRejected", setup.Long ? "Bull" : "Bear", match, observed,
                        $"strict PD: actual entry {FormatPrice(observed)} outside EQ tolerance; " +
                        $"range={FormatPrice(setup.RangeLow)}-{FormatPrice(setup.RangeHigh)}; " +
                        "POI retained for a later qualifying presentation");
                }
                return;
            }

            EmitStrictFilledSignal(setup, match, bar, observed);
        }

        private void RegisterUnfilledGapContacts(StrictSetup setup, int bar, decimal observed,
            List<ZoneContact> contacts, bool longSide)
        {
            if (setup is not { Status: SetupStatus.Armed })
                return;

            for (var i = 0; i < contacts.Count; i++)
            {
                var contact = contacts[i];
                var zone = contact.Zone;
                if (contact.Kind != ZoneContactKind.GapThrough || zone.IsBullish != longSide ||
                    !IsStrictPoiForSetup(zone, setup))
                    continue;

                var record = new SignalRecord
                {
                    Id = ++_nextSignalId,
                    Time = BarTime(bar),
                    Live = true,
                    Long = setup.Long,
                    Tier = "UNFILLED",
                    ArmSource = "ConfirmedTrap+ExternalMSS",
                    TriggerTag = zone.Tag,
                    TriggerType = zone.Type,
                    Layer = zone.IsHtf ? zone.HtfLabel : "LTF",
                    ZoneTop = zone.Top,
                    ZoneBottom = zone.Bottom,
                    PlannedEntry = setup.Long ? zone.Top : zone.Bottom,
                    Entry = observed,
                    Sl = setup.Long ? zone.Bottom - SlBufferTicks * TickSize : zone.Top + SlBufferTicks * TickSize,
                    PdStatus = "n/a — no verified in-zone print",
                    Confluence = "Observed price jumped completely through POI; no fill assumed",
                    SignalBar = bar,
                    FillStatus = SignalFillStatus.UnfilledGap,
                    DataQuality = MarketDataQuality.LiveOrderedObservations,
                    ExitPlan = BaseExitPlan,
                    TriggerZoneId = zone.Id,
                    StrictSetupId = setup.Id,
                    PriorUnarmedPresentations = zone.UnarmedPresentationEpisodes
                };

                JournalSignal(record);
                JournalEvent(bar, "UnfilledGap", setup.Long ? "Bull" : "Bear", zone, observed,
                    "Strict POI crossed without an observed in-zone price; no fill assumed and POI retained until body-close invalidation or a qualified fill");
            }
        }

        private void EvaluateStrictSetupsForOhlcContacts(int bar, decimal high, decimal low, List<ZoneContact> contacts)
        {
            EvaluateStrictSetupForOhlcContacts(_bullStrictSetup, bar, high, low, contacts, longSide: true);
            EvaluateStrictSetupForOhlcContacts(_bearStrictSetup, bar, high, low, contacts, longSide: false);
        }

        private void EvaluateStrictSetupForOhlcContacts(StrictSetup setup, int bar, decimal high, decimal low,
            List<ZoneContact> contacts, bool longSide)
        {
            if (setup is not { Status: SetupStatus.Armed })
                return;

            Zone match = null;
            for (var i = 0; i < contacts.Count; i++)
            {
                var contact = contacts[i];
                if (contact.Zone.IsBullish == longSide && IsStrictPoiForSetup(contact.Zone, setup))
                {
                    match = contact.Zone;
                    break;
                }
            }
            if (match == null)
                return;

            var plannedEntry = setup.Long ? match.Top : match.Bottom;
            var sl = setup.Long ? match.Bottom - SlBufferTicks * TickSize : match.Top + SlBufferTicks * TickSize;
            var risk = Math.Abs(plannedEntry - sl);
            var tp2 = setup.Long ? plannedEntry + risk * 2m : plannedEntry - risk * 2m;
            var tp3 = setup.Long ? plannedEntry + risk * 3m : plannedEntry - risk * 3m;

            // The entry itself is possible, but its order relative to stop/targets
            // is unknowable from a completed OHLC bar. Preserve the audit evidence
            // without pretending that it is a filled, scored trade.
            EmitAmbiguousOhlcSignal(setup, match, bar, plannedEntry, sl, tp2, tp3,
                StrictRules.IsPotentialOhlcAmbiguity(setup.Long, high, low, plannedEntry, sl, tp2, tp3));
        }

        private bool IsStrictPoiForSetup(Zone zone, StrictSetup setup)
        {
            if (!IsStrictPoiAvailableForCurrentPolicy(zone))
                return false;
            if (zone.IsBullish != setup.Long || zone.EligibleFromBar > setup.ExpiresBar)
                return false;
            if (!setup.EligiblePoiIds.Contains(zone.Id))
                return false;
            if (!IsZoneVisibleAndEligibleForStrictEntry(zone))
                return false;
            return true;
        }

        private bool IsStrictPoiAvailableForCurrentPolicy(Zone zone) =>
            StrictRules.IsStrictPoiAvailable(zone.State, zone.CoreEntryConsumed, zone.PreConfirmationTouched) &&
            (StrictPoiSurvivesUnarmedTouch || zone.State == ZoneState.Active);

        private StrictSetup FindArmedStrictSetupForZone(Zone zone)
        {
            var setup = zone.IsBullish ? _bullStrictSetup : _bearStrictSetup;
            return setup is { Status: SetupStatus.Armed } && setup.EligiblePoiIds.Contains(zone.Id)
                ? setup
                : null;
        }

        private bool IsChartZoneFamilyAllowedForStrictEntry(Zone zone) =>
            zone.IsOrderBlock
                ? DetectLtfOb && UseObForStrictEntry && ShowOb
                : DetectLtfFvg && UseFvgForStrictEntry && ShowFvg;

        private bool IsZoneVisibleAndEligibleForStrictEntry(Zone zone)
        {
            if (zone.IsHtf)
            {
                if (!HtfZonesAsExecutionPoi)
                    return false;
                return zone.IsOrderBlock ? HtfObEnabled && ShowOb : HtfFvgEnabled && ShowFvg;
            }

            return IsChartZoneFamilyAllowedForStrictEntry(zone);
        }

        /// <summary>
        /// A strict POI is retained through casual wick/touch presentations. This
        /// deliberately overrides legacy touch/mid/full-fill mitigation for the
        /// execution lifecycle: a body close through the far boundary remains the
        /// terminal invalidation, while a verified strict fill is the only explicit
        /// execution consumption.
        /// </summary>
        private bool RetainStrictPoiThroughTouchMitigation(Zone zone)
        {
            if (!StrictPoiSurvivesUnarmedTouch)
                return false;

            // HTF zones already use source-timeframe body-close invalidation. Keep
            // them out of an accidental LTF wick-based terminal state as well.
            if (zone.IsHtf)
                return true;

            return zone.IsOrderBlock ? UseObForStrictEntry : UseFvgForStrictEntry;
        }

        private void EmitStrictFilledSignal(StrictSetup setup, Zone trigger, int bar, decimal entry)
        {
            var buffer = SlBufferTicks * TickSize;
            var sl = setup.Long ? trigger.Bottom - buffer : trigger.Top + buffer;
            var risk = Math.Abs(entry - sl);
            if (risk <= 0m)
            {
                JournalEvent(bar, "EntryRejected", setup.Long ? "Bull" : "Bear", trigger, entry,
                    "non-positive executable risk");
                return;
            }

            var tp2 = setup.Long ? entry + risk * 2m : entry - risk * 2m;
            var tp3 = setup.Long ? entry + risk * 3m : entry - risk * 3m;
            var confluenceZones = GetGeometricHtfConfluence(trigger, entry, setup.Long);
            var tierCount = confluenceZones.Any(z => z.HtfMinutes >= 1440) ? 3
                : confluenceZones.Count > 0 ? 2 : 1;
            var tierName = tierCount switch { 3 => "A++", 2 => "A+", _ => "B" };
            var confluence = confluenceZones.Count == 0
                ? trigger.Tag
                : string.Join(" + ", new[] { trigger }.Concat(confluenceZones)
                    .OrderByDescending(z => z.HtfMinutes).Select(z => z.Tag).Distinct().Take(4));
            var tolerance = (setup.RangeHigh - setup.RangeLow) * PdTolerancePercent / 100m;
            var eq = (setup.RangeHigh + setup.RangeLow) / 2m;
            var pd = entry < eq - tolerance ? "Discount" : entry > eq + tolerance ? "Premium" : "Near EQ";

            var record = new SignalRecord
            {
                Id = ++_nextSignalId,
                Time = BarTime(bar),
                Live = true,
                Long = setup.Long,
                Tier = tierName,
                ArmSource = "ConfirmedTrap+ExternalMSS",
                TriggerTag = trigger.Tag,
                TriggerType = trigger.Type,
                Layer = trigger.IsHtf ? trigger.HtfLabel : "LTF",
                ZoneTop = trigger.Top,
                ZoneBottom = trigger.Bottom,
                PlannedEntry = setup.Long ? trigger.Top : trigger.Bottom,
                Entry = entry,
                Sl = sl,
                Tp2 = tp2,
                Tp3 = tp3,
                PdStatus = pd,
                Confluence = confluence,
                SignalBar = bar,
                FillBar = bar,
                FillSequence = _observationSequence,
                FillStatus = SignalFillStatus.Filled,
                DataQuality = MarketDataQuality.LiveOrderedObservations,
                ExitPlan = BaseExitPlan,
                RunnerStop = sl,
                TriggerZoneId = trigger.Id,
                StrictSetupId = setup.Id,
                PriorUnarmedPresentations = trigger.UnarmedPresentationEpisodes
            };

            trigger.CoreEntryConsumed = true;
            trigger.ConsumedByStrictSetupId = setup.Id;
            setup.Status = SetupStatus.Consumed;
            JournalSignal(record);

            if (!AlertOnEntry)
                return;

            var mark = string.Concat(Enumerable.Repeat(setup.Long ? "🟢" : "🔴", tierCount));
            Fire($"{mark} {(setup.Long ? "LONG" : "SHORT")} ENTRY — {tierName} strict setup\n" +
                 $"📍 Zone: {trigger.Tag} {FormatPrice(trigger.Bottom)}–{FormatPrice(trigger.Top)}\n" +
                 $"🧩 Confluence: {confluence}\n" +
                 $"⚖️ Range position: {pd}\n" +
                 $"▶️ Fill: {FormatPrice(entry)}\n" +
                 $"🛑 Stop: {FormatPrice(sl)}\n" +
                 $"🎯 Targets: 2R {FormatPrice(tp2)} · 3R {FormatPrice(tp3)}");
        }

        private void EmitAmbiguousOhlcSignal(StrictSetup setup, Zone trigger, int bar, decimal entry,
            decimal sl, decimal tp2, decimal tp3, bool sameBarConflict)
        {
            var record = new SignalRecord
            {
                Id = ++_nextSignalId,
                Time = BarTime(bar),
                Live = false,
                Long = setup.Long,
                Tier = "UNSCORED",
                ArmSource = "ConfirmedTrap+ExternalMSS",
                TriggerTag = trigger.Tag,
                TriggerType = trigger.Type,
                Layer = trigger.IsHtf ? trigger.HtfLabel : "LTF",
                ZoneTop = trigger.Top,
                ZoneBottom = trigger.Bottom,
                PlannedEntry = entry,
                Entry = entry,
                Sl = sl,
                Tp2 = tp2,
                Tp3 = tp3,
                PdStatus = "OHLC only",
                Confluence = sameBarConflict
                    ? "OHLC-only: entry/outcome order ambiguous"
                    : "OHLC-only: fill order unverified",
                SignalBar = bar,
                FillStatus = SignalFillStatus.AmbiguousOhlc,
                DataQuality = MarketDataQuality.OhlcApproximation,
                ExitPlan = BaseExitPlan,
                TriggerZoneId = trigger.Id,
                StrictSetupId = setup.Id,
                PriorUnarmedPresentations = trigger.UnarmedPresentationEpisodes
            };

            trigger.LastAmbiguousStrictAttemptBar = bar;
            setup.Status = SetupStatus.Consumed;
            JournalSignal(record);
            JournalEvent(bar, "AmbiguousOhlcSignal", setup.Long ? "Bull" : "Bear", trigger, entry,
                record.Confluence);
        }

        private List<Zone> GetGeometricHtfConfluence(Zone trigger, decimal entry, bool longSide) =>
            !HtfZonesAsConfluence
                ? new List<Zone>()
                : _zones.Where(z => z.IsHtf && z.State != ZoneState.Mitigated && z.IsBullish == longSide &&
                                    (z.IsOrderBlock ? HtfObEnabled && ShowOb : HtfFvgEnabled && ShowFvg) &&
                                    (z.Contains(entry) || StrictRules.IntervalsOverlap(trigger.Top, trigger.Bottom, z.Top, z.Bottom)))
                        .OrderByDescending(z => z.HtfMinutes)
                        .ToList();

        private void ApplyZoneContacts(int bar, decimal observedPrice, List<ZoneContact> contacts, bool isOhlc,
            decimal? ohlcHigh = null, decimal? ohlcLow = null)
        {
            foreach (var contact in contacts)
            {
                var zone = contact.Zone;
                var newEpisode = zone.LastTouchedBar < 0 || bar > zone.LastTouchedBar + 1;
                zone.LastTouchedBar = bar;

                if (zone.State == ZoneState.Active)
                {
                    zone.State = ZoneState.Touched;
                    zone.TouchEpisodes = 1;
                    zone.FirstPresentationBar = bar;
                    zone.FirstPresentationTime = BarTime(bar);
                }
                else if (newEpisode)
                {
                    zone.TouchEpisodes++;
                    var consumption = zone.CoreEntryConsumed
                        ? $"strict POI consumed by qualified setup #{zone.ConsumedByStrictSetupId?.ToString() ?? "?"}"
                        : "no prior qualified strict fill; POI remains eligible";
                    JournalEvent(bar, "ZoneRetouch", zone.IsBullish ? "Bull" : "Bear", zone, observedPrice,
                        $"touch #{zone.TouchEpisodes}; {consumption}");
                }

                if (!zone.TouchLogged)
                {
                    zone.TouchLogged = true;
                    JournalEvent(bar, contact.Kind == ZoneContactKind.GapThrough ? "ZoneGapThrough" : "ZoneTouch",
                        zone.IsBullish ? "Bull" : "Bear", zone, observedPrice,
                        isOhlc ? "OHLC presentation; execution not assumed" : contact.Kind.ToString());
                }

                // A previously touched POI remains a candidate until a strict fill
                // actually consumes it or a candle body invalidates it. Record each
                // distinct unarmed presentation so later signal/outcome analysis can
                // separate fresh-zone results from retained-POI results.
                var armedForZone = FindArmedStrictSetupForZone(zone);
                var ambiguousAttemptThisBar = isOhlc && zone.LastAmbiguousStrictAttemptBar == bar;
                if (newEpisode && !zone.CoreEntryConsumed && armedForZone == null && !ambiguousAttemptThisBar)
                {
                    zone.UnarmedPresentationEpisodes++;
                    JournalEvent(bar, "PoiUnarmedPresentation", zone.IsBullish ? "Bull" : "Bear", zone, observedPrice,
                        $"presentation #{zone.UnarmedPresentationEpisodes}; no linked armed strict setup; " +
                        "POI retained until body-close invalidation or a qualified strict fill");
                }

                if (!zone.TouchAlerted && AlertOnZoneTouch)
                {
                    zone.TouchAlerted = true;
                    Fire($"🎯 Zone tapped — {zone.Tag}\n" +
                         $"📍 Range: {FormatPrice(zone.Bottom)}–{FormatPrice(zone.Top)}\n" +
                         (zone.IsBullish ? "🛡 Support — watch for the bounce" : "🧱 Resistance — watch for the rejection"));
                }

                // Optional C-tier logic is deliberately isolated from the strict
                // engine. It can use this verified contact only before the zone is
                // consumed for future presentations.
                if (!isOhlc && !zone.CoreEntryConsumed &&
                    contact.Kind == ZoneContactKind.EnteredFromExpectedSide)
                {
                    TryEmitExperimentalContinuation(zone, bar, observedPrice);
                }

                var rule = zone.IsOrderBlock ? ObMitigation : FvgMitigation;
                if (rule == MitigationRule.BodyClose || RetainStrictPoiThroughTouchMitigation(zone))
                    continue;

                var high = ohlcHigh ?? observedPrice;
                var low = ohlcLow ?? observedPrice;
                var reachedMid = zone.IsBullish ? low <= zone.Mid : high >= zone.Mid;
                var reachedFar = zone.IsBullish ? low <= zone.Bottom : high >= zone.Top;
                if (rule == MitigationRule.AnyTouch)
                    Mitigate(zone, bar, "AnyTouch");
                else if (rule == MitigationRule.Midline && reachedMid)
                    Mitigate(zone, bar, "Midline");
                else if (rule == MitigationRule.FullFill && reachedFar)
                    Mitigate(zone, bar, "FullFill");

            }
        }

        /// <summary>
        /// Explicitly opt-in, non-ICT continuation experiment. It never upgrades a
        /// strict setup and is always written to a separate analytics family. The
        /// contact/fill still follows the same ordered-price and one-zone-one-use
        /// rules as the strict engine.
        /// </summary>
        private void TryEmitExperimentalContinuation(Zone zone, int bar, decimal entry)
        {
            if (!EntryModelEnabled || !ContinuationSignalsEnabled || zone.ContinuationFired || zone.PreConfirmationTouched)
                return;

            var longSide = zone.IsBullish;
            var trendAligned = _externalTrend == (longSide ? 1 : -1);
            var confirmedTrap = FindLatestConfirmedTrap(longSide, bar);
            var sweepReversal = ContinuationAfterSweep && !trendAligned && confirmedTrap != null;
            if (!trendAligned && !sweepReversal)
            {
                zone.ContinuationFired = true;
                JournalEvent(bar, "ContinuationRejected", longSide ? "Bull" : "Bear", zone, entry,
                    "experimental C-tier requires external trend alignment or an opt-in confirmed-trap reversal");
                return;
            }

            var age = bar - zone.ConfirmedBar;
            if (age > ContinuationMaxAgeBars)
            {
                zone.ContinuationFired = true;
                JournalEvent(bar, "ContinuationRejected", longSide ? "Bull" : "Bear", zone, entry,
                    $"zone confirmed {age} bars ago; limit {ContinuationMaxAgeBars}");
                return;
            }

            var buffer = SlBufferTicks * TickSize;
            var sl = longSide ? zone.Bottom - buffer : zone.Top + buffer;
            var risk = Math.Abs(entry - sl);
            if (risk <= 0m)
                return;

            var tp2 = longSide ? entry + risk * 2m : entry - risk * 2m;
            var tp3 = longSide ? entry + risk * 3m : entry - risk * 3m;
            var source = sweepReversal ? "ExperimentalConfirmedTrapReversal" : "ExperimentalContinuation";
            var record = new SignalRecord
            {
                Id = ++_nextSignalId,
                Time = BarTime(bar),
                Live = true,
                Long = longSide,
                Tier = "C",
                ArmSource = source,
                TriggerTag = zone.Tag,
                TriggerType = zone.Type,
                Layer = zone.IsHtf ? zone.HtfLabel : "LTF",
                ZoneTop = zone.Top,
                ZoneBottom = zone.Bottom,
                PlannedEntry = longSide ? zone.Top : zone.Bottom,
                Entry = entry,
                Sl = sl,
                Tp2 = tp2,
                Tp3 = tp3,
                PdStatus = "Not applied — experimental",
                Confluence = "Non-ICT experimental continuation; excluded from strict analytics",
                SignalBar = bar,
                FillBar = bar,
                FillSequence = _observationSequence,
                FillStatus = SignalFillStatus.Filled,
                DataQuality = MarketDataQuality.LiveOrderedObservations,
                ExitPlan = BaseExitPlan,
                RunnerStop = sl,
                TriggerZoneId = zone.Id
            };

            zone.ContinuationFired = true;
            // C-tier is explicitly non-ICT experimentation. It must not spend a
            // POI that a later qualified strict setup may legitimately use.
            JournalSignal(record);
            JournalEvent(bar, "ExperimentalContinuationFilled", longSide ? "Bull" : "Bear", zone, entry,
                $"{source}; explicitly non-ICT C tier");

            if (AlertOnEntry)
            {
                Fire($"🟡 C-TIER {(longSide ? "LONG" : "SHORT")} — experimental only\n" +
                     $"📍 Zone: {zone.Tag} {FormatPrice(zone.Bottom)}–{FormatPrice(zone.Top)}\n" +
                     $"▶️ Fill: {FormatPrice(entry)} · Stop: {FormatPrice(sl)}\n" +
                     "ℹ️ Not a strict ICT setup; reported separately from core analytics");
            }
        }

        private void ExpireStrictSetups(int bar)
        {
            ExpireStrictSetup(_bullStrictSetup, bar);
            ExpireStrictSetup(_bearStrictSetup, bar);
        }

        private void ExpireStrictSetup(StrictSetup setup, int bar)
        {
            if (setup is not { Status: SetupStatus.AwaitingMss or SetupStatus.Armed } || bar <= setup.ExpiresBar)
                return;

            setup.Status = SetupStatus.Expired;
            JournalEvent(bar, "SetupExpired", setup.Long ? "Bull" : "Bear", null, 0m,
                $"strict setup #{setup.Id}; {(setup.ArmedBar >= 0 ? "entry" : "MSS")} not completed before bar {setup.ExpiresBar}");
        }

        private void UpdateOpenSignalsOnObservation(int bar, decimal previous, decimal current, long sequence)
        {
            for (var i = _openSignals.Count - 1; i >= 0; i--)
            {
                var signal = _openSignals[i];
                if (signal.Resolved || signal.FillStatus != SignalFillStatus.Filled || sequence <= signal.FillSequence)
                    continue;

                if (signal.Long)
                {
                    signal.Mfe = Math.Max(signal.Mfe, current - signal.Entry);
                    signal.Mae = Math.Max(signal.Mae, signal.Entry - current);
                }
                else
                {
                    signal.Mfe = Math.Max(signal.Mfe, signal.Entry - current);
                    signal.Mae = Math.Max(signal.Mae, current - signal.Entry);
                }

                if (signal.ExitPlan == ExitPlan.PartialAtTp2RunnerToTp3)
                    UpdatePartialTrade(signal, bar, current);
                else
                    UpdateFullTrade(signal, bar, current);
            }
        }

        private void UpdateFullTrade(SignalRecord signal, int bar, decimal current)
        {
            var stopHit = signal.Long ? current <= signal.Sl : current >= signal.Sl;
            if (stopHit)
            {
                ResolveLive(signal, bar, "SL", current, signal.Risk > 0m
                    ? (signal.Long ? current - signal.Entry : signal.Entry - current) / signal.Risk : -1m);
                return;
            }

            var target = signal.ExitPlan == ExitPlan.FullAtTp2 ? signal.Tp2 : signal.Tp3;
            var targetHit = signal.Long ? current >= target : current <= target;
            if (targetHit)
            {
                var r = signal.ExitPlan == ExitPlan.FullAtTp2 ? 2m : 3m;
                ResolveLive(signal, bar, signal.ExitPlan == ExitPlan.FullAtTp2 ? "TP2" : "TP3", target, r);
            }
        }

        private void UpdatePartialTrade(SignalRecord signal, int bar, decimal current)
        {
            if (!signal.PartialTaken)
            {
                var stopHit = signal.Long ? current <= signal.Sl : current >= signal.Sl;
                if (stopHit)
                {
                    ResolveLive(signal, bar, "SL", current, signal.Risk > 0m
                        ? (signal.Long ? current - signal.Entry : signal.Entry - current) / signal.Risk : -1m);
                    return;
                }

                var tp2Hit = signal.Long ? current >= signal.Tp2 : current <= signal.Tp2;
                if (!tp2Hit)
                    return;

                signal.PartialTaken = true;
                signal.Tp2Hit = true;
                signal.RunnerStop = signal.Entry;
                signal.RealizedR = 1m;
            }

            var runnerStopHit = signal.Long ? current <= signal.RunnerStop : current >= signal.RunnerStop;
            if (runnerStopHit)
            {
                ResolveLive(signal, bar, "BE-after-TP2", current, 1m);
                return;
            }

            var tp3Hit = signal.Long ? current >= signal.Tp3 : current <= signal.Tp3;
            if (tp3Hit)
                ResolveLive(signal, bar, "TP3-after-TP2", signal.Tp3, 2.5m);
        }

        private void ResolveLive(SignalRecord signal, int bar, string outcome, decimal exit, decimal r)
        {
            signal.RealizedR = r;
            signal.PartialR = signal.ExitPlan == ExitPlan.PartialAtTp2RunnerToTp3 ? r : r;
            signal.BeR = r;
            signal.PartialDone = true;
            signal.BeDone = true;
            ResolveFilledSignal(signal, bar, outcome, exit, r);
        }
    }
}
