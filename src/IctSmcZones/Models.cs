using System;

namespace IctSmc
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
        public int StartBar;
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
        /// <summary>Bar of the most recent absorption event detected at this zone (-1 = none).</summary>
        public int LastAbsorptionBar = -1;
        /// <summary>Direction of that absorption (true = bullish, selling absorbed).</summary>
        public bool LastAbsorptionBull;

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
    }

    internal sealed class StructureEvent
    {
        public int Bar;
        public int FromBar;
        public decimal Level;
        public bool Bullish;
        /// <summary>true = Market Structure Shift (reversal), false = Break of Structure (continuation).</summary>
        public bool IsMss;
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
        public bool TriggerHtf;
        public string Layer = "";
        public decimal ZoneTop;
        public decimal ZoneBottom;
        public decimal Entry;
        public decimal Sl;
        public decimal Tp2;
        public decimal Tp3;
        public string PdStatus = "";
        public string Confluence = "";
        public int SignalBar;
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

        // Order-flow snapshot at the firing tick (observational; blank columns when
        // not captured or the feed lacks bid/ask delta).
        public bool OfCaptured;
        public bool OfDeltaAvailable = true;
        public decimal OfVolume;
        public decimal OfRelVolume;
        public decimal OfDelta;
        public decimal OfDeltaShare;
        public decimal OfCvd;
        public decimal OfCvdSlope5;
        public decimal OfPocPct = -1m;
        public string OfImbalances = "";
        public string OfAbsorption = "";
        public bool OfAbsorptionAtEntry;

        // Order-flow evolution after entry.
        public decimal OfPostDelta5;
        public int OfAlignedBars;
        public int OfTrackedBars;
        public decimal OfCvdAtEntry;
        public decimal OfCvdDrift;

        public decimal Risk => System.Math.Abs(Entry - Sl);

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
    }
}
