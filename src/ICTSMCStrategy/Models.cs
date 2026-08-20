using System;
using System.Collections.Generic;

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
        BearIfvg,
        /// <summary>Breaker: a violated bearish order block flipped into support.</summary>
        BullBreaker,
        /// <summary>Breaker: a violated bullish order block flipped into resistance.</summary>
        BearBreaker
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

    /// <summary>Where a liquidity pool came from.</summary>
    public enum LiquidityOrigin
    {
        /// <summary>Confirmed fractal swing pivot.</summary>
        Swing,
        /// <summary>Previous day high/low.</summary>
        PreviousDay,
        /// <summary>Previous week high/low.</summary>
        PreviousWeek
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
        /// <summary>Set once this order block has spawned its breaker (each OB breaks at most once).</summary>
        public bool BreakerSpawned;
        /// <summary>First-touch journaling latch (independent of the alert toggle).</summary>
        public bool TouchLogged;
        /// <summary>Latch: PD-filter rejection already journaled for this zone.</summary>
        public bool PdRejectLogged;
        /// <summary>Latch: OTE-filter rejection already journaled for this zone.</summary>
        public bool OteRejectLogged;
        /// <summary>Latch: "armed while price was already inside" rejection already journaled.</summary>
        public bool EdgeRejectLogged;
        /// <summary>Latch: a C-tier continuation signal already fired from this zone.</summary>
        public bool ContinuationFired;
        /// <summary>Last bar on which price was in contact with the zone (-1 = never).
        /// A gap of a full untouched bar separates distinct touch episodes.</summary>
        public int LastTouchedBar = -1;
        /// <summary>Distinct touch episodes so far (1 = first presentation).</summary>
        public int TouchEpisodes;

        public bool IsBullish => Type is ZoneType.BullOrderBlock or ZoneType.BullFvg
            or ZoneType.BullIfvg or ZoneType.BullBreaker;

        /// <summary>Order-block family (raw blocks and the breakers they turn into) — drives the OB mitigation rule.</summary>
        public bool IsOrderBlock => Type is ZoneType.BullOrderBlock or ZoneType.BearOrderBlock
            or ZoneType.BullBreaker or ZoneType.BearBreaker;

        public decimal Mid => (Top + Bottom) / 2m;

        /// <summary>Height in price units; always non-negative.</summary>
        public decimal Height => Top - Bottom;

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
                    ZoneType.BullBreaker or ZoneType.BearBreaker => "BRK",
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
        /// <summary>Where the pool came from: a swing pivot, or a previous session extreme.</summary>
        public LiquidityOrigin Origin = LiquidityOrigin.Swing;
        public bool Swept;
        public int? SweptBar;
        public bool SweptAlerted;
        /// <summary>true = price closed back inside (classic sweep/trap), false = closed through (run).</summary>
        public bool? WasTrap;

        /// <summary>Chart label: PDH/PDL/PWH/PWL for session extremes, EQH/EQL or BSL/SSL for swing pools.</summary>
        public string Label => Origin switch
        {
            LiquidityOrigin.PreviousDay => BuySide ? "PDH" : "PDL",
            LiquidityOrigin.PreviousWeek => BuySide ? "PWH" : "PWL",
            _ => BuySide ? (IsEqual ? "EQH · BSL" : "BSL") : (IsEqual ? "EQL · SSL" : "SSL")
        };

        /// <summary>Session extremes are the strongest draws and are never culled by the per-side cap.</summary>
        public bool IsSessionLevel => Origin != LiquidityOrigin.Swing;
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

        // Signal-bar excursion split. The signal fires INTRABAR, so the developing
        // candle's extremes at that instant separate pre-entry price action from
        // post-entry exposure: only excursion beyond these belongs to the trade.
        public decimal HighAtSignal;
        public decimal LowAtSignal;

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

        // Exit-warning latches: each threat class warns at most once per signal.
        public bool WarnedStructure;
        public bool WarnedZone;

        public decimal Risk => System.Math.Abs(Entry - Sl);

        public string ZoneFamily => TriggerType switch
        {
            ZoneType.BullOrderBlock or ZoneType.BearOrderBlock => "OB",
            ZoneType.BullFvg or ZoneType.BearFvg => "FVG",
            ZoneType.BullBreaker or ZoneType.BearBreaker => "BRK",
            _ => "iFVG"
        };
    }

    /// <summary>A higher-timeframe candle aggregated from chart bars.</summary>
    internal sealed class HtfCandle
    {
        public DateTime BucketStart;
        public int FirstChartBar;
        public decimal Open;
        public decimal High;
        public decimal Low;
        public decimal Close;
    }

    /// <summary>
    /// One higher-timeframe layer: aggregates chart candles into fixed time buckets
    /// and keeps the resulting synthetic series.
    /// </summary>
    /// <summary>
    /// Per-layer market-structure state: the SAME bookkeeping the chart timeframe keeps,
    /// held separately for each synthetic HTF series so the identical swing/BoS/order-block
    /// rules can run on it.
    /// </summary>
    internal sealed class HtfStructure
    {
        public readonly System.Collections.Generic.List<SwingPoint> Highs = new();
        public readonly System.Collections.Generic.List<SwingPoint> Lows = new();
        public SwingPoint LastHigh;
        public SwingPoint LastLow;

        // Wilder ATR of this series, seeded from a full-period simple average.
        public decimal Atr;
        public int AtrSamples;
        public decimal AtrSeedSum;
    }

    internal sealed class HtfAggregator
    {
        public int Minutes;
        public string Label;
        public readonly System.Collections.Generic.List<HtfCandle> Candles = new();
        public HtfCandle Current;
        public readonly HtfStructure Structure = new();

        /// <summary>Mean high-low range of the recent closed HTF candles — the scale
        /// reference for this layer's own noise filters (never the chart-TF ATR).</summary>
        public decimal AverageRange(int lookback)
        {
            var n = Candles.Count;
            if (n == 0)
                return 0m;

            var take = Math.Min(lookback, n);
            var sum = 0m;
            for (var i = n - take; i < n; i++)
                sum += Candles[i].High - Candles[i].Low;

            return take > 0 ? sum / take : 0m;
        }
    }

    /// <summary>An intraday time window (minutes after midnight, platform time).</summary>
    internal readonly struct TimeWindow
    {
        public readonly int StartMinute;
        public readonly int EndMinute;

        public TimeWindow(int startMinute, int endMinute)
        {
            StartMinute = startMinute;
            EndMinute = endMinute;
        }

        /// <summary>Windows that wrap past midnight (e.g. 22:00-02:00) are handled.</summary>
        public bool Contains(int minuteOfDay) => StartMinute <= EndMinute
            ? minuteOfDay >= StartMinute && minuteOfDay < EndMinute
            : minuteOfDay >= StartMinute || minuteOfDay < EndMinute;
    }

    #region Immutable render model

    // OnRender and OnCalculate run on DIFFERENT ATAS threads. Rather than lock the
    // hot trading path for the duration of a draw, the calculation thread publishes
    // an immutable snapshot of everything the renderer needs; the renderer performs
    // a single volatile reference read and then works entirely from value types.
    // No shared mutable collection is ever enumerated across threads.

    internal readonly struct ZoneView
    {
        public readonly ZoneType Type;
        public readonly bool IsHtf;
        public readonly bool IsBullish;
        public readonly bool IsOrderBlock;
        public readonly string Tag;
        public readonly int StartBar;
        public readonly bool HasEndBar;
        public readonly int EndBar;
        public readonly decimal Top;
        public readonly decimal Bottom;
        public readonly decimal Mid;
        public readonly ZoneState State;

        public ZoneView(Zone z)
        {
            Type = z.Type;
            IsHtf = z.IsHtf;
            IsBullish = z.IsBullish;
            IsOrderBlock = z.IsOrderBlock;
            Tag = z.Tag;
            StartBar = z.StartBar;
            HasEndBar = z.EndBar.HasValue;
            EndBar = z.EndBar ?? 0;
            Top = z.Top;
            Bottom = z.Bottom;
            Mid = z.Mid;
            State = z.State;
        }

        public bool Contains(decimal price) => price <= Top && price >= Bottom;
    }

    internal readonly struct LiquidityView
    {
        public readonly decimal Price;
        public readonly int StartBar;
        public readonly bool BuySide;
        public readonly bool Swept;
        public readonly bool HasSweptBar;
        public readonly int SweptBar;
        public readonly bool WasTrap;
        public readonly string Label;
        /// <summary>Equal-high/low pools and session extremes draw with a heavier stroke.</summary>
        public readonly bool Emphasis;

        public LiquidityView(LiquidityLevel l)
        {
            Price = l.Price;
            StartBar = l.StartBar;
            BuySide = l.BuySide;
            Swept = l.Swept;
            HasSweptBar = l.SweptBar.HasValue;
            SweptBar = l.SweptBar ?? 0;
            WasTrap = l.WasTrap == true;
            Label = l.Label;
            Emphasis = l.IsEqual || l.IsSessionLevel;
        }
    }

    internal readonly struct StructureView
    {
        public readonly int Bar;
        public readonly int FromBar;
        public readonly decimal Level;
        public readonly bool Bullish;
        public readonly bool IsMss;

        public StructureView(StructureEvent e)
        {
            Bar = e.Bar;
            FromBar = e.FromBar;
            Level = e.Level;
            Bullish = e.Bullish;
            IsMss = e.IsMss;
        }
    }

    /// <summary>Immutable snapshot handed from the calculation thread to the renderer.</summary>
    internal sealed class RenderModel
    {
        public static readonly RenderModel Empty = new(
            new List<ZoneView>(), new List<LiquidityView>(), new List<StructureView>(),
            0m, 0m, 0, false, 0m, 0m, 0, false, 0m, 0m, "");

        public readonly List<ZoneView> Zones;
        public readonly List<LiquidityView> Liquidity;
        public readonly List<StructureView> Structure;
        public readonly decimal Atr;
        public readonly decimal LastClose;
        public readonly int LastBar;

        public readonly bool HasRange;
        public readonly decimal RangeHigh;
        public readonly decimal RangeLow;
        public readonly int RangeAnchorBar;

        public readonly bool HasOte;
        public readonly decimal OteTop;
        public readonly decimal OteBottom;

        public readonly string HtfInfo;

        public decimal RangeEq => (RangeHigh + RangeLow) / 2m;

        public RenderModel(List<ZoneView> zones, List<LiquidityView> liquidity, List<StructureView> structure,
            decimal atr, decimal lastClose, int lastBar,
            bool hasRange, decimal rangeHigh, decimal rangeLow, int rangeAnchorBar,
            bool hasOte, decimal oteTop, decimal oteBottom, string htfInfo)
        {
            Zones = zones;
            Liquidity = liquidity;
            Structure = structure;
            Atr = atr;
            LastClose = lastClose;
            LastBar = lastBar;
            HasRange = hasRange;
            RangeHigh = rangeHigh;
            RangeLow = rangeLow;
            RangeAnchorBar = rangeAnchorBar;
            HasOte = hasOte;
            OteTop = oteTop;
            OteBottom = oteBottom;
            HtfInfo = htfInfo ?? "";
        }
    }

    #endregion
}
