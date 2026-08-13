using ICTSMC;

var tests = new (string Name, Action Run)[]
{
    ("Buy-side trap requires penetration and reclaim", BuySideTrapRequiresReclaim),
    ("Sell-side trap mirrors buy-side rule", SellSideTrapMirrors),
    ("Exact liquidity close remains indeterminate", ExactCloseIsIndeterminate),
    ("Observed zone contact rejects gap-through fill", GapThroughIsNotFill),
    ("Observed zone contact detects expected-side entry", ExpectedSideEntryIsDetected),
    ("OHLC interval requires real price overlap", OhlcIntersectionIsGeometric),
    ("BodyClose uses close rather than body extrema", BodyCloseUsesClose),
    ("Premium/discount validates actual entry", PremiumDiscountUsesEntry),
    ("OHLC ambiguity is explicitly recognized", OhlcAmbiguityIsRecognized)
};

var failures = new List<string>();
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.Error.WriteLine($"FAIL  {name}: {ex.Message}");
    }
}

return failures.Count == 0 ? 0 : 1;

static void BuySideTrapRequiresReclaim()
{
    Equal(LiquidityDisposition.ConfirmedTrap,
        StrictRules.ClassifyLiquidity(true, 101.00m, 99m, 99.50m, 100m, .25m, 1, 1));
    Equal(LiquidityDisposition.Run,
        StrictRules.ClassifyLiquidity(true, 101.00m, 99m, 100.50m, 100m, .25m, 1, 1));
    Equal(LiquidityDisposition.Indeterminate,
        StrictRules.ClassifyLiquidity(true, 100.10m, 99m, 99.50m, 100m, .25m, 1, 1));
}

static void SellSideTrapMirrors()
{
    Equal(LiquidityDisposition.ConfirmedTrap,
        StrictRules.ClassifyLiquidity(false, 101m, 99m, 100.50m, 100m, .25m, 1, 1));
    Equal(LiquidityDisposition.Run,
        StrictRules.ClassifyLiquidity(false, 101m, 99m, 99.50m, 100m, .25m, 1, 1));
}

static void ExactCloseIsIndeterminate() =>
    Equal(LiquidityDisposition.Indeterminate,
        StrictRules.ClassifyLiquidity(true, 101m, 99m, 100m, 100m, .25m, 1, 0));

static void GapThroughIsNotFill()
{
    Equal(ZoneContactKind.GapThrough,
        StrictRules.ClassifyObservedContact(true, 103m, 99m, 102m, 100m));
    Equal(ZoneContactKind.GapThrough,
        StrictRules.ClassifyObservedContact(false, 97m, 103m, 102m, 100m));
}

static void ExpectedSideEntryIsDetected()
{
    Equal(ZoneContactKind.EnteredFromExpectedSide,
        StrictRules.ClassifyObservedContact(true, 103m, 101m, 102m, 100m));
    Equal(ZoneContactKind.EnteredFromExpectedSide,
        StrictRules.ClassifyObservedContact(false, 99m, 101m, 102m, 100m));
}

static void OhlcIntersectionIsGeometric()
{
    True(StrictRules.HasOhlcIntersection(103m, 101m, 102m, 100m));
    False(StrictRules.HasOhlcIntersection(99m, 97m, 102m, 100m));
    False(StrictRules.IntervalsOverlap(110m, 108m, 107m, 105m));
    True(StrictRules.IntervalsOverlap(110m, 108m, 109m, 107m));
}

static void BodyCloseUsesClose()
{
    False(StrictRules.IsBodyCloseInvalidated(true, 100.50m, 102m, 100m));
    True(StrictRules.IsBodyCloseInvalidated(true, 99.75m, 102m, 100m));
    False(StrictRules.IsBodyCloseInvalidated(false, 101.50m, 102m, 100m));
    True(StrictRules.IsBodyCloseInvalidated(false, 102.25m, 102m, 100m));
}

static void PremiumDiscountUsesEntry()
{
    True(StrictRules.PassesPremiumDiscount(true, 104m, 110m, 100m, 0m));
    False(StrictRules.PassesPremiumDiscount(true, 106m, 110m, 100m, 0m));
    True(StrictRules.PassesPremiumDiscount(false, 106m, 110m, 100m, 0m));
    False(StrictRules.PassesPremiumDiscount(false, 104m, 110m, 100m, 0m));
}

static void OhlcAmbiguityIsRecognized()
{
    True(StrictRules.IsPotentialOhlcAmbiguity(true, 107m, 98m, 101m, 99m, 105m, 107m));
    False(StrictRules.IsPotentialOhlcAmbiguity(true, 104m, 100m, 101m, 99m, 105m, 107m));
}

static void True(bool value)
{
    if (!value)
        throw new InvalidOperationException("Expected true.");
}

static void False(bool value)
{
    if (value)
        throw new InvalidOperationException("Expected false.");
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; got {actual}.");
}
