using System;

namespace ICTSMC
{
    /// <summary>Kind of institutional zone drawn on the chart.</summary>
    public enum ZoneType
    {
        BullOrderBlock,
        BearOrderBlock,
        BullFvg,
        BearFvg,
        /// <summary>Inversion FVG: a broken bearish FVG flipped into support.</summary>
        BullIfvg,
        /// <summary>Inversion FVG: a broken bullish FVG flipped into resistance.</summary>
        BearIfvg
    }

    /// <summary>Lifecycle of a zone.</summary>
    public enum ZoneState
    {
        Active,
        Touched,
        Mitigated
    }

    /// <summary>How a zone gets consumed/invalidated.</summary>
    public enum MitigationRule
    {
        /// <summary>Any touch of the zone edge consumes it.</summary>
        AnyTouch,
        /// <summary>Price reaching the 50% level (consequent encroachment) consumes it.</summary>
        Midline,
        /// <summary>Price fully trading through the far edge consumes it.</summary>
        FullFill,
        /// <summary>A candle BODY closing beyond the far edge consumes it.</summary>
        BodyClose
    }

    /// <summary>Chart display philosophy.</summary>
    public enum DisplayMode
    {
        /// <summary>Live-trading view: only currently tradeable objects near price.</summary>
        Clean,
        /// <summary>Review view: no proximity culling, longer retention of past objects.</summary>
        Detailed
    }

    /// <summary>How the higher timeframe is chosen.</summary>
    public enum HtfSelectionMode
    {
        /// <summary>Detect the chart timeframe from the data and pick the institutional HTF ladder automatically.</summary>
        Auto,
        /// <summary>Use the fixed minutes configured in settings.</summary>
        Manual
    }

    /// <summary>Which part of the order-block candle builds the zone.</summary>
    public enum ObZoneStyle
    {
        /// <summary>Open-to-close body of the OB candle (as taught in the book).</summary>
        Body,
        /// <summary>Full high-to-low range of the OB candle.</summary>
        FullRange
    }

    /// <summary>Final disposition of a crossed liquidity pool.</summary>
    public enum LiquidityDisposition
    {
        TakenPendingClose,
        ConfirmedTrap,
        Run,
        Indeterminate,
        Expired
    }

    /// <summary>Strict sweep-to-MSS setup lifecycle.</summary>
    public enum SetupStatus
    {
        AwaitingMss,
        Armed,
        Consumed,
        Invalidated,
        Expired
    }

    /// <summary>Whether a structure event is execution/internal or directional/external.</summary>
    public enum StructureScope
    {
        Internal,
        External
    }

    /// <summary>How a signal could be filled by the available market data.</summary>
    public enum SignalFillStatus
    {
        SignalOnly,
        Filled,
        UnfilledGap,
        AmbiguousOhlc,
        Cancelled
    }

    /// <summary>Reliability of the market-event sequence used for a signal/outcome.</summary>
    public enum MarketDataQuality
    {
        LiveOrderedObservations,
        TickReplay,
        OhlcApproximation
    }

    /// <summary>One explicit base exit model; analytics never infer it from a TP2 latch.</summary>
    public enum ExitPlan
    {
        FullAtTp2,
        FullAtTp3,
        PartialAtTp2RunnerToTp3
    }

    internal enum ZoneContactKind
    {
        None,
        EnteredFromExpectedSide,
        AlreadyInside,
        GapThrough,
        OhlcPossible
    }

    internal sealed class Zone
    {
        /// <summary>Stable id for journaling and audit.</summary>
        public int Id;
        public ZoneType Type;
        public bool IsHtf;
        /// <summary>Human label of the HTF layer this zone belongs to ("4H", "D", …). Empty for chart-TF zones.</summary>
        public string HtfLabel = "";
        /// <summary>Minutes of the HTF layer (0 for chart-TF zones). Drives confluence scoring.</summary>
        public int HtfMinutes;
        /// <summary>
        /// Visual origin. Rendering deliberately continues to use this field so V2
        /// keeps the established chart presentation of V1.
        /// </summary>
        public int StartBar;
        /// <summary>Bar at which the zone became objectively valid.</summary>
        public int ConfirmedBar;
        /// <summary>First bar allowed to use the zone as a strict execution POI.</summary>
        public int EligibleFromBar;
        /// <summary>Structure break which validated this zone, where applicable.</summary>
        public int? ConfirmingStructureBar;
        public decimal Top;
        public decimal Bottom;
        public ZoneState State = ZoneState.Active;
        /// <summary>Bar at which the zone was mitigated (drawing stops there).</summary>
        public int? EndBar;
        public bool CreatedAlerted;
        public bool TouchAlerted;
        /// <summary>Set once this FVG has spawned its inversion zone (each gap inverts at most once).</summary>
        public bool Inverted;
        /// <summary>First-touch journaling latch (independent of the alert toggle).</summary>
        public bool TouchLogged;
        /// <summary>Latch: PD-filter rejection already journaled for this zone.</summary>
        public bool PdRejectLogged;
        /// <summary>Latch: a C-tier continuation signal already fired from this zone.</summary>
        public bool ContinuationFired;
        /// <summary>Last bar on which price was in contact with the zone (-1 = never).
        /// A gap of a full untouched bar separates distinct touch episodes.</summary>
        public int LastTouchedBar = -1;
        /// <summary>Distinct touch episodes so far (1 = first presentation).</summary>
        public int TouchEpisodes;
        /// <summary>First actual/possible presentation after confirmation.</summary>
        public int? FirstPresentationBar;
        public DateTime? FirstPresentationTime;
        /// <summary>Last historical chart bar reconciled after late HTF construction.</summary>
        public int HistoricalReconciledThroughBar = -1;
        /// <summary>
        /// Set only after an ordered, qualified strict fill from this POI. A casual
        /// presentation before a setup arms is deliberately not consumption: the
        /// zone can still be linked to a later trap + external-MSS setup.
        /// </summary>
        public bool CoreEntryConsumed;
        /// <summary>Strict setup that produced the consuming fill, if any.</summary>
        public int? ConsumedByStrictSetupId;
        /// <summary>
        /// Number of distinct post-confirmation presentations made while no linked
        /// strict setup was armed. This is audit metadata, not a validity veto.
        /// </summary>
        public int UnarmedPresentationEpisodes;
        /// <summary>Last bar on which an OHLC-only strict candidate was recorded.</summary>
        public int? LastAmbiguousStrictAttemptBar;
        /// <summary>Price contacted the source before it was valid as a tradeable POI.</summary>
        public bool PreConfirmationTouched;
        public string MitigationReason = "";

        public bool IsBullish => Type is ZoneType.BullOrderBlock or ZoneType.BullFvg or ZoneType.BullIfvg;
        public bool IsOrderBlock => Type is ZoneType.BullOrderBlock or ZoneType.BearOrderBlock;
        public decimal Mid => (Top + Bottom) / 2m;

        public string Tag
        {
            get
            {
                // No direction glyphs: side is already conveyed by color and by the
                // zone sitting above (resistance) or below (support) price.
                var core = Type switch
                {
                    ZoneType.BullOrderBlock or ZoneType.BearOrderBlock => "OB",
                    ZoneType.BullFvg or ZoneType.BearFvg => "FVG",
                    _ => "iFVG"
                };
                return IsHtf ? $"{(string.IsNullOrEmpty(HtfLabel) ? "HTF" : HtfLabel)} {core}" : core;
            }
        }

        public bool Contains(decimal price) => price <= Top && price >= Bottom;
    }

    internal sealed class SwingPoint
    {
        public int Bar;
        public decimal Price;
        /// <summary>Set once structure broke through this swing (used for BoS/MSS bookkeeping).</summary>
        public bool Broken;
    }

    internal sealed class LiquidityLevel
    {
        public int Id;
        public decimal Price;
        public int StartBar;
        /// <summary>true = buy-side liquidity resting above highs; false = sell-side below lows.</summary>
        public bool BuySide;
        /// <summary>true when built from (near-)equal highs/lows — a stronger pool.</summary>
        public bool IsEqual;
        public bool Swept;
        public int? SweptBar;
        public bool SweptAlerted;
        /// <summary>true = price closed back inside (classic sweep/trap), false = closed through (run).</summary>
        public bool? WasTrap;
        public int? LiquidityEventId;
    }

    internal sealed class LiquidityEvent
    {
        public int Id;
        public int LiquidityLevelId;
        /// <summary>true means the trap would prime a long setup (SSL raid).</summary>
        public bool LongSetup;
        public bool BuySide;
        public decimal Level;
        public int TakenBar;
        public DateTime TakenTime;
        public decimal MaximumPenetration;
        public LiquidityDisposition Disposition = LiquidityDisposition.TakenPendingClose;
        public int? ClassifiedBar;
    }

    internal sealed class StrictSetup
    {
        public int Id;
        public bool Long;
        public int LiquidityEventId;
        public int CreatedBar;
        public int ArmedBar;
        public int ExpiresBar;
        public SetupStatus Status = SetupStatus.AwaitingMss;
        public decimal RangeHigh;
        public decimal RangeLow;
        public int RangeHighBar;
        public int RangeLowBar;
        public readonly System.Collections.Generic.HashSet<int> EligiblePoiIds = new();
        public int? MssStructureEventId;
        public string InvalidationReason = "";
    }

    internal sealed class StructureEvent
    {
        public int Id;
        public int Bar;
        public int FromBar;
        public decimal Level;
        public bool Bullish;
        /// <summary>true = Market Structure Shift (reversal), false = Break of Structure (continuation).</summary>
        public bool IsMss;
        public StructureScope Scope = StructureScope.Internal;
    }

    /// <summary>
    /// One fired entry signal, tracked bar-by-bar for MAE/MFE and outcome —
    /// the raw material of the performance analytics.
    /// </summary>
    internal sealed class SignalRecord
    {
        public int Id;
        public System.DateTime Time;
        public bool Live;
        public bool Long;
        public string Tier = "";
        public string ArmSource = "";
        public string TriggerTag = "";
        public ZoneType TriggerType;
        public string Layer = "";
        public decimal ZoneTop;
        public decimal ZoneBottom;
        public decimal Entry;
        public decimal PlannedEntry;
        public decimal Sl;
        public decimal Tp2;
        public decimal Tp3;
        public string PdStatus = "";
        public string Confluence = "";
        /// <summary>Strict setup responsible for this signal (0 for non-strict records).</summary>
        public int StrictSetupId;
        /// <summary>
        /// Distinct unarmed presentations that preceded this signal. Allows the
        /// journal to measure whether retained, previously-touched POIs add value.
        /// </summary>
        public int PriorUnarmedPresentations;
        public int SignalBar;
        public int FillBar;
        public long FillSequence;
        public SignalFillStatus FillStatus;
        public MarketDataQuality DataQuality;
        public ExitPlan ExitPlan;
        /// <summary>Id of the zone that triggered this signal (for invalidation alerts).</summary>
        public int TriggerZoneId;

        // Excursion tracking (absolute price units; reported in R).
        public decimal Mae;
        public decimal Mfe;
        public bool Tp2Hit;
        public bool Resolved;
        public string Outcome = "";
        public decimal Exit;
        public int ResolvedBar;

        // Shadow trade-management simulation (virtual — never traded, only logged).
        // BE-at-+1R shadow: once price moves +1R in favor, the virtual stop jumps
        // to entry; the same bar-by-bar rules then resolve the shadow position.
        public bool BeArmed;
        public bool BeTp2;
        public bool BeDone;
        public decimal BeR;
        // Partial-at-+2R shadow: at +2R half the position is banked (+1R locked)
        // and the remaining half runs to TP3 with its stop at entry.
        public bool PartialTaken;
        public bool PartialDone;
        public decimal PartialR;
        /// <summary>Stop for the remaining position after an explicit partial exit.</summary>
        public decimal RunnerStop;
        /// <summary>Actual realized R for the selected base exit plan.</summary>
        public decimal RealizedR;

        // Exit-warning latches: each threat class warns at most once per signal.
        public bool WarnedStructure;
        public bool WarnedZone;

        public decimal Risk => System.Math.Abs(Entry - Sl);
        public bool IsAnalyticsEligible => FillStatus == SignalFillStatus.Filled && Resolved &&
                                           DataQuality != MarketDataQuality.OhlcApproximation;

        public string ZoneFamily => TriggerType switch
        {
            ZoneType.BullOrderBlock or ZoneType.BearOrderBlock => "OB",
            ZoneType.BullFvg or ZoneType.BearFvg => "FVG",
            _ => "iFVG"
        };
    }

    /// <summary>A higher-timeframe candle aggregated from chart bars.</summary>
    internal sealed class HtfCandle
    {
        public DateTime BucketStart;
        public int FirstChartBar;
        public int LastChartBar;
        public decimal Open;
        public decimal High;
        public decimal Low;
        public decimal Close;
    }

    /// <summary>
    /// One higher-timeframe layer: aggregates chart candles into fixed time buckets
    /// and keeps the resulting synthetic series.
    /// </summary>
    internal sealed class HtfAggregator
    {
        public int Minutes;
        public string Label;
        public readonly System.Collections.Generic.List<HtfCandle> Candles = new();
        public HtfCandle Current;
        public decimal Atr;
        public bool AtrSeeded;
        public readonly System.Collections.Generic.List<SwingPoint> SwingHighs = new();
        public readonly System.Collections.Generic.List<SwingPoint> SwingLows = new();
        public SwingPoint LastSwingHigh;
        public SwingPoint LastSwingLow;
        public int Trend;
    }
}
