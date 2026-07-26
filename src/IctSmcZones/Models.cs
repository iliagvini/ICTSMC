using System;

namespace IctSmc
{
    /// <summary>Kind of institutional zone drawn on the chart.</summary>
    public enum ZoneType
    {
        BullOrderBlock,
        BearOrderBlock,
        BullFvg,
        BearFvg
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
        public ZoneType Type;
        public bool IsHtf;
        /// <summary>Human label of the HTF layer this zone belongs to ("4H", "D", …). Empty for chart-TF zones.</summary>
        public string HtfLabel = "";
        public int StartBar;
        public decimal Top;
        public decimal Bottom;
        public ZoneState State = ZoneState.Active;
        /// <summary>Bar at which the zone was mitigated (drawing stops there).</summary>
        public int? EndBar;
        public bool CreatedAlerted;
        public bool TouchAlerted;

        public bool IsBullish => Type is ZoneType.BullOrderBlock or ZoneType.BullFvg;
        public bool IsOrderBlock => Type is ZoneType.BullOrderBlock or ZoneType.BearOrderBlock;
        public decimal Mid => (Top + Bottom) / 2m;

        public string Tag
        {
            get
            {
                var core = Type switch
                {
                    ZoneType.BullOrderBlock => "OB▲",
                    ZoneType.BearOrderBlock => "OB▼",
                    ZoneType.BullFvg => "FVG▲",
                    _ => "FVG▼"
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
