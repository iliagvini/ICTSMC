using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.Threading;
using ATAS.Indicators;

namespace ICTSMC
{
    /// <summary>
    /// ICT / Smart-Money-Concepts zone engine for ATAS.
    ///
    /// Implements the full playbook from "Mastering ICT &amp; SMC Trading":
    ///  • Liquidity pools (BSL/SSL, equal highs/lows, PDH/PDL/PWH/PWL) + sweep vs. run classification
    ///  • Fair Value Gaps (3-candle imbalance) with configurable mitigation
    ///  • Order Blocks (last opposite candle before a displacement that breaks structure)
    ///  • Breaker blocks (violated order blocks flipped into the opposite polarity)
    ///  • Break of Structure (BoS) and Market Structure Shift (MSS/CHoCH) on protected swings
    ///  • Premium / Discount (equilibrium of the current dealing range) + optional OTE band
    ///  • Higher-timeframe (HTF) framework: HTF FVGs + HTF order blocks mapped onto the chart
    ///  • Entry model: sweep → MSS → return into aligned zone in the correct half of the range
    ///  • Optional killzone (session) gating of entries
    ///  • Popup + Telegram alerts, fired intrabar the moment price TOUCHES a zone
    ///    (no waiting for candle close — price often reacts instantly on the tap).
    /// </summary>
    [DisplayName("ICT/SMC Strategy")]
    [Category("Order Flow")]
    public partial class ICTSMCStrategy : Indicator
    {
        #region Group names

        private const string GrpGeneral = "01. General";
        private const string GrpStructure = "02. Market Structure";
        private const string GrpFvg = "03. Fair Value Gaps";
        private const string GrpOb = "04. Order Blocks";
        private const string GrpLiq = "05. Liquidity";
        private const string GrpPd = "06. Premium/Discount";
        private const string GrpHtf = "07. Higher Timeframe";
        private const string GrpSignal = "08. Entry Model";
        private const string GrpAlerts = "09. Alerts";
        private const string GrpTelegram = "10. Telegram";

        #endregion

        #region State

        private readonly List<Zone> _zones = new();
        private readonly List<SwingPoint> _swingHighs = new();
        private readonly List<SwingPoint> _swingLows = new();
        private readonly List<LiquidityLevel> _liquidity = new();
        private readonly List<StructureEvent> _structure = new();

        // HTF engine
        private readonly List<HtfAggregator> _htfAggregators = new();
        private readonly Dictionary<long, int> _barDeltaCounts = new(); // delta seconds -> occurrences
        private readonly List<long> _barDeltaSamples = new();
        private bool _htfConfigured;
        private string _htfInfo = "";
        private string _chartTfLabel = "";
        /// <summary>Measured chart timeframe in minutes; scales HTF-relative windows.</summary>
        private int _chartMinutes;

        // Previous-session liquidity (PDH/PDL/PWH/PWL) bookkeeping.
        private DateTime _currentDayBucket = DateTime.MinValue;
        private DateTime _currentWeekBucket = DateTime.MinValue;
        private decimal _dayHigh, _dayLow, _weekHigh, _weekLow;
        private bool _dayOpen, _weekOpen;

        private SwingPoint _lastSwingHigh;
        private SwingPoint _lastSwingLow;

        /// <summary>+1 bullish, -1 bearish, 0 undefined.</summary>
        private int _trend;

        private decimal _atr;
        private int _atrSamples;
        private decimal _atrSeedSum;

        private int _lastSeenBar = -1;
        private bool _realtime;

        /// <summary>True once the first real calculation has run — gates setting-driven
        /// recalculation so restoring saved settings does not rebuild repeatedly.</summary>
        private bool _settingsLive;

        // Entry-model state machine
        private int _pendingBullSweepBar = -1;  // sell-side liquidity was swept (long setup precursor)
        private int _pendingBearSweepBar = -1;  // buy-side liquidity was swept (short setup precursor)
        // The level that primed each side. Kept so arming can ask whether that liquidity
        // event was a TRAP (closed back inside) or a RUN (closed through) — an ICT
        // reversal is seeded by the former, never the latter.
        private LiquidityLevel _pendingBullSweepLevel;
        private LiquidityLevel _pendingBearSweepLevel;
        private int _armedBullUntil = -1;
        private int _armedBearUntil = -1;
        private int _armedBullAtBar = -1;
        private int _armedBearAtBar = -1;
        private string _armedBullSource = "";   // "Sweep" / "TrapArm" / "Sweep+Trap" / "MSS-only"
        private string _armedBearSource = "";

        // Trap-chain depth: how many consecutive failed-MSS ("TrapArm") hops the
        // current arming is removed from a REAL liquidity sweep. Zero means the arm
        // is backed by an actual sweep. Capped by MaxTrapChainHops so a chopping
        // market cannot ping-pong the model between sides indefinitely and thereby
        // defeat RequireSweepForEntry.
        private int _armedBullTrapDepth;
        private int _armedBearTrapDepth;

        // Impulse-leg dealing range: re-anchored on every structure break so EQ
        // tracks the CURRENT leg instead of a stale pre-break extreme.
        private int _legDirection;              // +1 bull leg, -1 bear leg, 0 none yet
        private SwingPoint _legAnchor;          // origin extreme of the current leg
        private SwingPoint _legExtreme;         // running extreme since the break (completed bars only)

        // Immutable snapshot handed to the render thread (see RenderModel).
        private RenderModel _renderModel = RenderModel.Empty;
        private bool _renderDirty = true;

        // Parsed killzone windows, rebuilt whenever the setting string changes.
        private List<TimeWindow> _killzones = new();
        private string _killzonesParsedFrom;

        #endregion

        /// <summary>
        /// Assigns a setting and forces a clean, full recalculation from bar 0.
        /// Every setting that participates in DETECTION uses this: patching a live
        /// state tree that was built under different rules produces a chart that
        /// corresponds to no coherent configuration.
        /// </summary>
        private void Set<T>(ref T field, T value)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;

            field = value;

            // ATAS assigns every persisted property while restoring a chart, long
            // before the first calculation. Recalculating on each of those would run
            // a full rebuild dozens of times per chart load, so the trigger only arms
            // once the indicator has actually calculated something.
            if (_settingsLive)
                RecalculateValues();
        }

        #region General settings

        [Display(GroupName = GrpGeneral, Name = "Display mode", Order = 90)]
        public DisplayMode DisplayMode { get; set; } = DisplayMode.Clean;

        [Display(GroupName = GrpGeneral, Name = "Visible zones per side (Clean mode)", Order = 92)]
        [Range(1, 20)]
        public int MaxVisibleZonesPerSide { get; set; } = 4;

        [Display(GroupName = GrpGeneral, Name = "Zone visibility range (ATR ×, 0 = all)", Order = 94)]
        [Range(0, 100)]
        public int ZoneVisibilityAtrRange { get; set; } = 8;

        private int _swingPeriod = 3;
        [Display(GroupName = GrpGeneral, Name = "Swing period (fractal strength)", Order = 100)]
        [Range(2, 20)]
        public int SwingPeriod
        {
            get => _swingPeriod;
            set => Set(ref _swingPeriod, Math.Clamp(value, 2, 20));
        }

        private int _atrPeriod = 14;
        [Display(GroupName = GrpGeneral, Name = "ATR period (filters)", Order = 110)]
        [Range(5, 100)]
        public int AtrPeriod
        {
            get => _atrPeriod;
            set => Set(ref _atrPeriod, Math.Clamp(value, 5, 100));
        }

        private int _maxZonesPerType = 25;
        [Display(GroupName = GrpGeneral, Name = "Max active zones per type", Order = 120)]
        [Range(1, 200)]
        public int MaxZonesPerType
        {
            get => _maxZonesPerType;
            set => Set(ref _maxZonesPerType, Math.Clamp(value, 1, 200));
        }

        [Display(GroupName = GrpGeneral, Name = "Show mitigated zones (review only)", Order = 130)]
        public bool ShowMitigated { get; set; } = false;

        private int _keepMitigatedBars = 10;
        [Display(GroupName = GrpGeneral, Name = "Keep mitigated zones in data (bars)", Order = 140)]
        [Range(5, 5000)]
        public int KeepMitigatedBars
        {
            get => _keepMitigatedBars;
            set => Set(ref _keepMitigatedBars, Math.Clamp(value, 5, 5000));
        }

        #endregion

        #region Structure settings

        [Display(GroupName = GrpStructure, Name = "Show BoS / MSS", Order = 200)]
        public bool ShowStructure { get; set; } = true;

        private bool _useProtectedSwings = true;
        [Display(GroupName = GrpStructure, Name = "Protected-swing structure (ignore internal breaks)", Order = 202)]
        public bool UseProtectedSwings
        {
            get => _useProtectedSwings;
            set => Set(ref _useProtectedSwings, value);
        }

        [Display(GroupName = GrpStructure, Name = "Max structure labels on chart", Order = 205)]
        [Range(1, 100)]
        public int MaxStructureLabels { get; set; } = 8;

        [Display(GroupName = GrpStructure, Name = "Bullish structure color", Order = 210)]
        public Color BullStructureColor { get; set; } = Color.FromArgb(255, 38, 166, 91);

        [Display(GroupName = GrpStructure, Name = "Bearish structure color", Order = 220)]
        public Color BearStructureColor { get; set; } = Color.FromArgb(255, 217, 30, 24);

        #endregion

        #region FVG settings

        [Display(GroupName = GrpFvg, Name = "Show FVGs", Order = 300)]
        public bool ShowFvg { get; set; } = true;

        private int _minFvgTicks = 2;
        [Display(GroupName = GrpFvg, Name = "Min gap size (ticks)", Order = 310)]
        [Range(0, 500)]
        public int MinFvgTicks
        {
            get => _minFvgTicks;
            set => Set(ref _minFvgTicks, Math.Clamp(value, 0, 500));
        }

        private decimal _minFvgAtrFraction = 0.15m;
        [Display(GroupName = GrpFvg, Name = "Min gap size (ATR / HTF-range fraction)", Order = 320)]
        [Range(0, 5)]
        public decimal MinFvgAtrFraction
        {
            get => _minFvgAtrFraction;
            set => Set(ref _minFvgAtrFraction, Math.Clamp(value, 0m, 5m));
        }

        private MitigationRule _fvgMitigation = MitigationRule.FullFill;
        [Display(GroupName = GrpFvg, Name = "Mitigation rule", Order = 330)]
        public MitigationRule FvgMitigation
        {
            get => _fvgMitigation;
            set => Set(ref _fvgMitigation, value);
        }

        private bool _ifvgEnabled = true;
        [Display(GroupName = GrpFvg, Name = "Inversion FVGs (IFVG)", Order = 335)]
        public bool IfvgEnabled
        {
            get => _ifvgEnabled;
            set => Set(ref _ifvgEnabled, value);
        }

        [Display(GroupName = GrpFvg, Name = "Bullish FVG color", Order = 340)]
        public Color BullFvgColor { get; set; } = Color.FromArgb(255, 52, 152, 219);

        [Display(GroupName = GrpFvg, Name = "Bearish FVG color", Order = 350)]
        public Color BearFvgColor { get; set; } = Color.FromArgb(255, 230, 126, 34);

        [Display(GroupName = GrpFvg, Name = "Bullish IFVG color", Order = 360)]
        public Color BullIfvgColor { get; set; } = Color.FromArgb(255, 26, 188, 156);

        [Display(GroupName = GrpFvg, Name = "Bearish IFVG color", Order = 370)]
        public Color BearIfvgColor { get; set; } = Color.FromArgb(255, 155, 89, 182);

        #endregion

        #region Order Block settings

        [Display(GroupName = GrpOb, Name = "Show Order Blocks", Order = 400)]
        public bool ShowOb { get; set; } = true;

        private ObZoneStyle _obStyle = ObZoneStyle.Body;
        [Display(GroupName = GrpOb, Name = "Zone style", Order = 410)]
        public ObZoneStyle ObStyle
        {
            get => _obStyle;
            set => Set(ref _obStyle, value);
        }

        private int _obLookback = 15;
        [Display(GroupName = GrpOb, Name = "Lookback for OB candle (bars)", Order = 420)]
        [Range(3, 100)]
        public int ObLookback
        {
            get => _obLookback;
            set => Set(ref _obLookback, Math.Clamp(value, 3, 100));
        }

        private decimal _displacementAtrFactor = 1.5m;
        [Display(GroupName = GrpOb, Name = "Displacement filter (ATR ×)", Order = 430)]
        [Range(0, 10)]
        public decimal DisplacementAtrFactor
        {
            get => _displacementAtrFactor;
            set => Set(ref _displacementAtrFactor, Math.Clamp(value, 0m, 10m));
        }

        private bool _requireImbalanceForOb = true;
        [Display(GroupName = GrpOb, Name = "Displacement must leave an imbalance (FVG)", Order = 435)]
        public bool RequireImbalanceForOb
        {
            get => _requireImbalanceForOb;
            set => Set(ref _requireImbalanceForOb, value);
        }

        private MitigationRule _obMitigation = MitigationRule.BodyClose;
        [Display(GroupName = GrpOb, Name = "Mitigation rule", Order = 440)]
        public MitigationRule ObMitigation
        {
            get => _obMitigation;
            set => Set(ref _obMitigation, value);
        }

        private bool _breakerBlocksEnabled = true;
        [Display(GroupName = GrpOb, Name = "Breaker blocks (violated OB flips polarity)", Order = 445)]
        public bool BreakerBlocksEnabled
        {
            get => _breakerBlocksEnabled;
            set => Set(ref _breakerBlocksEnabled, value);
        }

        [Display(GroupName = GrpOb, Name = "Bullish OB color", Order = 450)]
        public Color BullObColor { get; set; } = Color.FromArgb(255, 46, 204, 113);

        [Display(GroupName = GrpOb, Name = "Bearish OB color", Order = 460)]
        public Color BearObColor { get; set; } = Color.FromArgb(255, 231, 76, 60);

        [Display(GroupName = GrpOb, Name = "Bullish breaker color", Order = 470)]
        public Color BullBreakerColor { get; set; } = Color.FromArgb(255, 39, 174, 96);

        [Display(GroupName = GrpOb, Name = "Bearish breaker color", Order = 480)]
        public Color BearBreakerColor { get; set; } = Color.FromArgb(255, 192, 57, 43);

        #endregion

        #region Liquidity settings

        [Display(GroupName = GrpLiq, Name = "Show liquidity levels", Order = 500)]
        public bool ShowLiquidity { get; set; } = true;

        private int _equalLevelTicks = 3;
        [Display(GroupName = GrpLiq, Name = "Equal high/low tolerance (ticks)", Order = 510)]
        [Range(0, 100)]
        public int EqualLevelTicks
        {
            get => _equalLevelTicks;
            set => Set(ref _equalLevelTicks, Math.Clamp(value, 0, 100));
        }

        private int _maxLiquidityPerSide = 8;
        [Display(GroupName = GrpLiq, Name = "Max swing levels per side", Order = 520)]
        [Range(1, 50)]
        public int MaxLiquidityPerSide
        {
            get => _maxLiquidityPerSide;
            set => Set(ref _maxLiquidityPerSide, Math.Clamp(value, 1, 50));
        }

        private bool _sessionLevelsEnabled = true;
        [Display(GroupName = GrpLiq, Name = "Previous day/week highs & lows (PDH/PDL/PWH/PWL)", Order = 522)]
        public bool SessionLevelsEnabled
        {
            get => _sessionLevelsEnabled;
            set => Set(ref _sessionLevelsEnabled, value);
        }

        [Display(GroupName = GrpLiq, Name = "Keep swept levels visible (bars)", Order = 525)]
        [Range(0, 1000)]
        public int SweptRetentionBars { get; set; } = 40;

        [Display(GroupName = GrpLiq, Name = "Buy-side liquidity color", Order = 530)]
        public Color BslColor { get; set; } = Color.FromArgb(255, 192, 57, 43);

        [Display(GroupName = GrpLiq, Name = "Sell-side liquidity color", Order = 540)]
        public Color SslColor { get; set; } = Color.FromArgb(255, 39, 174, 96);

        #endregion

        #region Premium/Discount settings

        [Display(GroupName = GrpPd, Name = "Show equilibrium line", Order = 600)]
        public bool ShowPremiumDiscount { get; set; } = true;

        [Display(GroupName = GrpPd, Name = "Shade premium/discount halves", Order = 605)]
        public bool PdShadingEnabled { get; set; } = false;

        private bool _dealingRangeFromLeg = true;
        [Display(GroupName = GrpPd, Name = "Anchor dealing range to impulse leg", Order = 608)]
        public bool DealingRangeFromLeg
        {
            get => _dealingRangeFromLeg;
            set => Set(ref _dealingRangeFromLeg, value);
        }

        [Display(GroupName = GrpPd, Name = "Show OTE band (0.618–0.79)", Order = 612)]
        public bool ShowOte { get; set; } = true;

        [Display(GroupName = GrpPd, Name = "Premium shade color", Order = 610)]
        public Color PremiumColor { get; set; } = Color.FromArgb(255, 231, 76, 60);

        [Display(GroupName = GrpPd, Name = "Discount shade color", Order = 620)]
        public Color DiscountColor { get; set; } = Color.FromArgb(255, 46, 204, 113);

        [Display(GroupName = GrpPd, Name = "Equilibrium line color", Order = 630)]
        public Color EquilibriumColor { get; set; } = Color.FromArgb(255, 149, 165, 166);

        [Display(GroupName = GrpPd, Name = "OTE band color", Order = 640)]
        public Color OteColor { get; set; } = Color.FromArgb(255, 241, 196, 15);

        #endregion

        #region HTF settings

        // HTF settings use explicit setters so any change triggers a full, clean
        // recalculation — the aggregation layers are rebuilt from bar 0, never patched.

        private bool _htfEnabled = true;
        private HtfSelectionMode _htfMode = HtfSelectionMode.Auto;
        private int _htfManualMinutes = 240;
        private bool _autoSecondLayer = true;
        private int _dailyAnchorMinutes;

        [Display(GroupName = GrpHtf, Name = "Enable HTF mapping", Order = 700)]
        public bool HtfEnabled
        {
            get => _htfEnabled;
            set => Set(ref _htfEnabled, value);
        }

        [Display(GroupName = GrpHtf, Name = "HTF selection", Order = 705)]
        public HtfSelectionMode HtfMode
        {
            get => _htfMode;
            set => Set(ref _htfMode, value);
        }

        [Display(GroupName = GrpHtf, Name = "Manual HTF (minutes, Manual mode only)", Order = 710)]
        [Range(1, 20160)]
        public int HtfMinutes
        {
            get => _htfManualMinutes;
            set => Set(ref _htfManualMinutes, Math.Clamp(value, 1, 20160));
        }

        [Display(GroupName = GrpHtf, Name = "Auto: add second HTF layer", Order = 712)]
        public bool AutoSecondLayer
        {
            get => _autoSecondLayer;
            set => Set(ref _autoSecondLayer, value);
        }

        [Display(GroupName = GrpHtf, Name = "Daily/Weekly anchor (minutes after midnight, 0 = calendar day)", Order = 714)]
        [Range(0, 1439)]
        public int DailyAnchorMinutes
        {
            get => _dailyAnchorMinutes;
            set => Set(ref _dailyAnchorMinutes, Math.Clamp(value, 0, 1439));
        }

        private int _intradayAnchorMinutes;

        /// <summary>
        /// Phase of the INTRADAY HTF buckets (15m / 1H / 4H). Separate from the daily anchor
        /// on purpose: an instrument can have clock-aligned intraday bars and a session-based
        /// DAILY candle at the same time. ATAS opens 4H candles at 00/04/08/12/16/20 and 1H
        /// candles on the hour, regardless of the futures session — so a single shared anchor
        /// could not serve both. Setting 18:00 to line up a session daily would drag the 4H
        /// buckets to 18/22/02/06/10/14 and break alignment with the platform's own H4 chart.
        ///
        /// 0 (default) = clock-aligned, which is what ATAS does.
        /// </summary>
        [Display(GroupName = GrpHtf, Name = "Intraday HTF anchor (minutes after midnight, 0 = clock-aligned)", Order = 715)]
        [Range(0, 1439)]
        public int IntradayAnchorMinutes
        {
            get => _intradayAnchorMinutes;
            set => Set(ref _intradayAnchorMinutes, Math.Clamp(value, 0, 1439));
        }

        private bool _htfFvgEnabled = true;
        [Display(GroupName = GrpHtf, Name = "HTF Fair Value Gaps", Order = 720)]
        public bool HtfFvgEnabled
        {
            get => _htfFvgEnabled;
            set => Set(ref _htfFvgEnabled, value);
        }

        private bool _htfObEnabled = true;
        [Display(GroupName = GrpHtf, Name = "HTF Order Blocks", Order = 730)]
        public bool HtfObEnabled
        {
            get => _htfObEnabled;
            set => Set(ref _htfObEnabled, value);
        }

        private decimal _htfDisplacementFactor = 1.3m;
        [Display(GroupName = GrpHtf, Name = "HTF displacement (legacy — unused; OBs now use the ATR × filter)", Order = 740)]
        [Range(0, 10)]
        public decimal HtfDisplacementFactor
        {
            get => _htfDisplacementFactor;
            set => Set(ref _htfDisplacementFactor, Math.Clamp(value, 0m, 10m));
        }

        private int _htfStructureLookback = 5;
        [Display(GroupName = GrpHtf, Name = "HTF structure lookback (legacy — unused; swing structure is used)", Order = 742)]
        [Range(2, 30)]
        public int HtfStructureLookback
        {
            get => _htfStructureLookback;
            set => Set(ref _htfStructureLookback, Math.Clamp(value, 2, 30));
        }

        [Display(GroupName = GrpHtf, Name = "HTF zone border color", Order = 745)]
        public Color HtfBorderColor { get; set; } = Color.FromArgb(0xFF, 0xD4, 0xAF, 0x37);

        private int _maxHtfZones = 12;
        [Display(GroupName = GrpHtf, Name = "Max HTF zones per layer", Order = 750)]
        [Range(1, 100)]
        public int MaxHtfZones
        {
            get => _maxHtfZones;
            set => Set(ref _maxHtfZones, Math.Clamp(value, 1, 100));
        }

        [Display(GroupName = GrpHtf, Name = "Show HTF info badge", Order = 760)]
        public bool ShowHtfInfoBadge { get; set; } = true;

        #endregion

        #region Entry model settings

        private bool _entryModelEnabled = true;
        [Display(GroupName = GrpSignal, Name = "Enable entry-model signal", Order = 800)]
        public bool EntryModelEnabled
        {
            get => _entryModelEnabled;
            set => Set(ref _entryModelEnabled, value);
        }

        private bool _requireTrapForEntry = true;
        [Display(GroupName = GrpSignal, Name = "Sweep must be a TRAP, not a run", Order = 812)]
        public bool RequireTrapForEntry
        {
            get => _requireTrapForEntry;
            set => Set(ref _requireTrapForEntry, value);
        }

        private bool _requireSweepForEntry = true;
        [Display(GroupName = GrpSignal, Name = "Require liquidity sweep first", Order = 810)]
        public bool RequireSweepForEntry
        {
            get => _requireSweepForEntry;
            set => Set(ref _requireSweepForEntry, value);
        }

        private int _sweepToMssWindow = 40;
        [Display(GroupName = GrpSignal, Name = "Sweep → MSS window (bars)", Order = 820)]
        [Range(1, 500)]
        public int SweepToMssWindow
        {
            get => _sweepToMssWindow;
            set => Set(ref _sweepToMssWindow, Math.Clamp(value, 1, 500));
        }

        private int _armWindowBars = 30;
        [Display(GroupName = GrpSignal, Name = "MSS → entry window (bars)", Order = 830)]
        [Range(1, 500)]
        public int ArmWindowBars
        {
            get => _armWindowBars;
            set => Set(ref _armWindowBars, Math.Clamp(value, 1, 500));
        }

        private bool _entryNeedsPdAlignment = true;
        [Display(GroupName = GrpSignal, Name = "Respect premium/discount filter", Order = 840)]
        public bool EntryNeedsPdAlignment
        {
            get => _entryNeedsPdAlignment;
            set => Set(ref _entryNeedsPdAlignment, value);
        }

        private int _pdTolerancePercent = 10;
        [Display(GroupName = GrpSignal, Name = "PD tolerance (% of range around EQ)", Order = 842)]
        [Range(0, 50)]
        public int PdTolerancePercent
        {
            get => _pdTolerancePercent;
            set => Set(ref _pdTolerancePercent, Math.Clamp(value, 0, 50));
        }

        private bool _oteFilterEnabled;
        [Display(GroupName = GrpSignal, Name = "Require OTE (0.618–0.79 retracement)", Order = 843)]
        public bool OteFilterEnabled
        {
            get => _oteFilterEnabled;
            set => Set(ref _oteFilterEnabled, value);
        }

        private decimal _oteMinPercent = 61.8m;
        [Display(GroupName = GrpSignal, Name = "OTE band start (% retracement)", Order = 844)]
        [Range(0, 100)]
        public decimal OteMinPercent
        {
            get => _oteMinPercent;
            set => Set(ref _oteMinPercent, Math.Clamp(value, 0m, 100m));
        }

        private decimal _oteMaxPercent = 79m;
        [Display(GroupName = GrpSignal, Name = "OTE band end (% retracement)", Order = 845)]
        [Range(0, 100)]
        public decimal OteMaxPercent
        {
            get => _oteMaxPercent;
            set => Set(ref _oteMaxPercent, Math.Clamp(value, 0m, 100m));
        }

        private bool _cancelOnOppositeMss = true;
        [Display(GroupName = GrpSignal, Name = "Opposite MSS cancels armed setup", Order = 846)]
        public bool CancelOnOppositeMss
        {
            get => _cancelOnOppositeMss;
            set => Set(ref _cancelOnOppositeMss, value);
        }

        private bool _armOnFailedMss = true;
        [Display(GroupName = GrpSignal, Name = "Failed MSS arms opposite side (trap entry)", Order = 847)]
        public bool ArmOnFailedMss
        {
            get => _armOnFailedMss;
            set => Set(ref _armOnFailedMss, value);
        }

        private int _maxTrapChainHops = 1;
        [Display(GroupName = GrpSignal, Name = "Max consecutive trap-arms without a fresh sweep", Order = 848)]
        [Range(0, 5)]
        public int MaxTrapChainHops
        {
            get => _maxTrapChainHops;
            set => Set(ref _maxTrapChainHops, Math.Clamp(value, 0, 5));
        }

        private bool _killzoneFilterEnabled;
        [Display(GroupName = GrpSignal, Name = "Killzone filter (entries only inside sessions)", Order = 849)]
        public bool KillzoneFilterEnabled
        {
            get => _killzoneFilterEnabled;
            set => Set(ref _killzoneFilterEnabled, value);
        }

        private string _killzoneWindows = "02:00-05:00, 07:00-10:00, 13:30-16:00";
        [Display(GroupName = GrpSignal, Name = "Killzones (HH:mm-HH:mm, comma separated, platform time)", Order = 850)]
        public string KillzoneWindows
        {
            get => _killzoneWindows;
            set => Set(ref _killzoneWindows, value);
        }

        private int _slBufferTicks = 4;
        [Display(GroupName = GrpSignal, Name = "Stop-loss buffer (ticks)", Order = 852)]
        [Range(0, 100)]
        public int SlBufferTicks
        {
            get => _slBufferTicks;
            set => Set(ref _slBufferTicks, Math.Clamp(value, 0, 100));
        }

        private bool _continuationSignalsEnabled = true;
        [Display(GroupName = GrpSignal, Name = "C-tier continuation signals (Non-ICT)", Order = 860)]
        public bool ContinuationSignalsEnabled
        {
            get => _continuationSignalsEnabled;
            set => Set(ref _continuationSignalsEnabled, value);
        }

        private int _continuationMaxAgeBars = 20;
        [Display(GroupName = GrpSignal, Name = "Continuation: max zone age (bars)", Order = 862)]
        [Range(1, 200)]
        public int ContinuationMaxAgeBars
        {
            get => _continuationMaxAgeBars;
            set => Set(ref _continuationMaxAgeBars, Math.Clamp(value, 1, 200));
        }

        #endregion

        #region Alert settings

        [Display(GroupName = GrpAlerts, Name = "Popup alerts", Order = 900)]
        public bool UsePopupAlerts { get; set; } = false;

        [Display(GroupName = GrpAlerts, Name = "Alert sound file", Order = 905)]
        public string AlertFile { get; set; } = "alert1";

        [Display(GroupName = GrpAlerts, Name = "Alert: zone created", Order = 910)]
        public bool AlertOnZoneCreated { get; set; } = false;

        [Display(GroupName = GrpAlerts, Name = "Alert: zone touched (instant)", Order = 920)]
        public bool AlertOnZoneTouch { get; set; } = false;

        [Display(GroupName = GrpAlerts, Name = "Alert: liquidity sweep", Order = 930)]
        public bool AlertOnSweep { get; set; } = false;

        [Display(GroupName = GrpAlerts, Name = "Alert: BoS / MSS", Order = 940)]
        public bool AlertOnStructure { get; set; } = false;

        [Display(GroupName = GrpAlerts, Name = "Alert: entry-model signal", Order = 950)]
        public bool AlertOnEntry { get; set; } = true;

        [Display(GroupName = GrpAlerts, Name = "Alert: failed MSS", Order = 955)]
        public bool AlertOnFailedMss { get; set; } = false;

        [Display(GroupName = GrpAlerts, Name = "Alert: signal zone invalidated", Order = 957)]
        public bool AlertOnSignalZoneInvalidated { get; set; } = true;

        [Display(GroupName = GrpAlerts, Name = "Alert: zone re-touched (info only)", Order = 958)]
        public bool AlertOnZoneRetouch { get; set; } = false;

        [Display(GroupName = GrpAlerts, Name = "Alert: exit warning (open signal threatened)", Order = 959)]
        public bool AlertOnExitWarning { get; set; } = true;

        #endregion

        #region Telegram settings

        [Display(GroupName = GrpTelegram, Name = "Send Telegram alerts", Order = 1000)]
        public bool TelegramEnabled { get; set; } = true;

        [Display(GroupName = GrpTelegram, Name = "Bot token", Order = 1010)]
        public string TelegramBotToken
        {
            get => _telegramBotToken;
            set
            {
                _telegramBotToken = value;
                // A token change must reach the command hub without waiting for a
                // chart reload, so the right poller starts listening immediately.
                TelegramHub.Register(this);
            }
        }

        private string _telegramBotToken = "8903920388:AAHUoNC0pC9ImjZXmE_nlalUlp9vu8ayUjM";

        [Display(GroupName = GrpTelegram, Name = "Chat id", Order = 1020)]
        public string TelegramChatId { get; set; } = "-5306304855";

        #endregion

        public ICTSMCStrategy()
            : base(true)
        {
            DenyToChangePanel = true;

            var series = (ValueDataSeries)DataSeries[0];
            series.VisualType = VisualMode.Hide;
            series.IsHidden = true;

            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == 0)
            {
                ResetState();
                PublishRenderModel(0);
                return;
            }

            // Out-of-order / partial recalculation. ATAS may revisit a bar that has
            // already been finalized (amended history, provider corrections, a
            // partial refresh). Re-running the bar-close engine for it would
            // double-count swings, structure, zones and HTF candles — so an
            // already-consumed index is ignored outright.
            if (bar < _lastSeenBar)
                return;

            if (bar > _lastSeenBar)
            {
                // A new bar index means every bar before it is final.
                if (_lastSeenBar >= 0)
                    OnBarComplete(_lastSeenBar);

                _lastSeenBar = bar;
            }
            else
            {
                // Repeated calls on the same bar index normally mean live ticks —
                // but ONLY the newest bar of the series can genuinely be live. A
                // duplicated historical bar (some providers re-emit one during load)
                // must never latch realtime mode, or the entire replay would fire
                // alerts and journal itself as LIVE.
                if (bar >= CurrentBar - 1)
                    _realtime = true;
            }

            // Intrabar engine: touches, sweeps, mitigations and entry signals are
            // evaluated on EVERY tick of the developing candle — price often reacts
            // the moment a zone is tapped, so we never wait for the close.
            ProcessIntrabar(bar);

            PublishRenderModel(bar);
            _settingsLive = true;
        }

        private void ResetState()
        {
            _zones.Clear();
            _swingHighs.Clear();
            _swingLows.Clear();
            _liquidity.Clear();
            _structure.Clear();
            _htfAggregators.Clear();
            _barDeltaCounts.Clear();
            _barDeltaSamples.Clear();
            _htfConfigured = false;
            _htfInfo = "";
            _chartTfLabel = "";
            _chartMinutes = 0;
            _currentDayBucket = DateTime.MinValue;
            _currentWeekBucket = DateTime.MinValue;
            _dayHigh = _dayLow = _weekHigh = _weekLow = 0m;
            _dayOpen = false;
            _weekOpen = false;
            _lastSwingHigh = null;
            _lastSwingLow = null;
            _trend = 0;
            _atr = 0m;
            _atrSamples = 0;
            _atrSeedSum = 0m;
            _lastSeenBar = 0;
            _realtime = false;
            _pendingBullSweepBar = -1;
            _pendingBearSweepBar = -1;
            _pendingBullSweepLevel = null;
            _pendingBearSweepLevel = null;
            _armedBullUntil = -1;
            _armedBearUntil = -1;
            _armedBullAtBar = -1;
            _armedBearAtBar = -1;
            _armedBullSource = "";
            _armedBearSource = "";
            _armedBullTrapDepth = 0;
            _armedBearTrapDepth = 0;
            _legDirection = 0;
            _legAnchor = null;
            _legExtreme = null;
            _renderModel = RenderModel.Empty;
            _renderDirty = true;
            _killzonesParsedFrom = null;
            InitJournalSession();
        }

        #region Render model publication

        /// <summary>
        /// Marks the render snapshot stale. Called from every point that mutates
        /// engine state, so the renderer never has to look at a live collection.
        /// </summary>
        private void MarkRenderDirty() => _renderDirty = true;

        /// <summary>
        /// Builds an immutable snapshot of everything OnRender needs and publishes it
        /// with a single volatile reference write.
        ///
        /// ATAS calls OnRender on the chart's drawing thread while OnCalculate runs on
        /// the data thread. Enumerating the live List&lt;T&gt; state from the renderer is
        /// a genuine data race — not merely "collection was modified", but torn reads
        /// of the backing array during a resize, which can surface as a wrong price or
        /// a NullReferenceException with no exception at the enumeration site. Copying
        /// under the producer thread and handing over immutable value types removes the
        /// race entirely without ever locking the trading path.
        /// </summary>
        private void PublishRenderModel(int bar)
        {
            if (!_renderDirty)
                return;

            _renderDirty = false;

            var zones = new List<ZoneView>(_zones.Count);
            foreach (var z in _zones)
                zones.Add(new ZoneView(z));

            var liquidity = new List<LiquidityView>(_liquidity.Count);
            foreach (var l in _liquidity)
                liquidity.Add(new LiquidityView(l));

            var structure = new List<StructureView>(_structure.Count);
            foreach (var e in _structure)
                structure.Add(new StructureView(e));

            var lastBar = Math.Max(0, Math.Min(bar, CurrentBar - 1));
            var lastClose = CurrentBar > 0 ? GetCandle(lastBar).Close : 0m;

            var range = GetDealingRange();
            var hasRange = range.HasValue;
            var rangeHigh = hasRange ? range.Value.High.Price : 0m;
            var rangeLow = hasRange ? range.Value.Low.Price : 0m;
            var anchorBar = hasRange ? Math.Min(range.Value.High.Bar, range.Value.Low.Bar) : 0;

            var ote = GetOteBand();

            // Volatile write: publishes the fully-constructed snapshot so the render
            // thread can never observe a partially-initialised model.
            Volatile.Write(ref _renderModel, new RenderModel(zones, liquidity, structure, _atr, lastClose, lastBar,
                hasRange, rangeHigh, rangeLow, anchorBar,
                ote.HasValue, ote?.Top ?? 0m, ote?.Bottom ?? 0m, _htfInfo));
        }

        #endregion

        #region Killzones

        /// <summary>
        /// Parses the killzone setting ("02:00-05:00, 07:00-10:00") into windows.
        /// Re-parsed only when the string actually changes. An unparseable entry is
        /// skipped rather than throwing — a typo must never break the chart.
        /// </summary>
        private List<TimeWindow> Killzones()
        {
            var raw = KillzoneWindows ?? "";
            if (ReferenceEquals(_killzonesParsedFrom, raw) || string.Equals(_killzonesParsedFrom, raw, StringComparison.Ordinal))
                return _killzones;

            var parsed = new List<TimeWindow>();

            foreach (var part in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var halves = part.Split('-');
                if (halves.Length != 2)
                    continue;

                if (TryParseMinuteOfDay(halves[0], out var start) && TryParseMinuteOfDay(halves[1], out var end))
                    parsed.Add(new TimeWindow(start, end));
            }

            _killzones = parsed;
            _killzonesParsedFrom = raw;
            return _killzones;
        }

        private static bool TryParseMinuteOfDay(string text, out int minuteOfDay)
        {
            minuteOfDay = 0;
            var trimmed = (text ?? "").Trim();
            var bits = trimmed.Split(':');
            if (bits.Length != 2)
                return false;

            if (!int.TryParse(bits[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ||
                !int.TryParse(bits[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
                return false;

            if (h < 0 || h > 23 || m < 0 || m > 59)
                return false;

            minuteOfDay = h * 60 + m;
            return true;
        }

        /// <summary>
        /// True when the bar's open time falls inside a configured killzone, or when
        /// the filter is off / no window parsed (fail-open: a malformed setting must
        /// never silently mute every signal).
        /// </summary>
        private bool InKillzone(int bar)
        {
            if (!KillzoneFilterEnabled)
                return true;

            var windows = Killzones();
            if (windows.Count == 0)
                return true;

            var time = GetCandle(bar).Time;
            var minuteOfDay = time.Hour * 60 + time.Minute;

            foreach (var w in windows)
            {
                if (w.Contains(minuteOfDay))
                    return true;
            }

            return false;
        }

        #endregion

        /// <summary>
        /// Instrument tick size with a safe fallback.
        ///
        /// Deliberately NOT named TickSize: the ATAS indicator base class already
        /// exposes a TickSize member, and a same-named property here silently HID it
        /// (CS0108). Hiding a base member by accident is how two different tick sizes
        /// end up in play depending on which one a given call site happens to bind to.
        /// </summary>
        private decimal InstrumentTickSize => InstrumentInfo?.TickSize ?? 0.01m;

        private string FormatPrice(decimal price)
        {
            var ts = InstrumentTickSize;
            var decimals = 0;
            while (ts != Math.Truncate(ts) && decimals < 10)
            {
                ts *= 10;
                decimals++;
            }

            return Math.Round(price, decimals).ToString("0." + new string('#', Math.Max(1, decimals)));
        }
    }
}
