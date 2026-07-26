# Strategy → Code mapping

This document maps every concept from *Mastering ICT & SMC Trading* (and the standard
institutional ICT extensions the book only hints at) to the exact place in the code.

## 1. Liquidity (BSL / SSL, equal highs/lows)

> “Liquidity = stop losses + pending orders sitting at obvious levels.”

- Every confirmed fractal swing high registers a **BSL** level at its high; every swing
  low registers **SSL** (`IctSmcZones.Detection.cs → RegisterLiquidity`).
- Swings within `EqualLevelTicks` of an existing unswept level are merged into a single
  pool flagged **EQH/EQL** — rendered thicker because clustered stops are a stronger draw.
- **Sweep detection is intrabar** (`Intrabar.cs → CheckLiquiditySweeps`): the alert fires
  on the tick that crosses the level. On the candle **close** the event is classified:
  - close back inside → **sweep (trap)** — the book’s manipulation phase;
  - close through → **run** (real breakout / BoS fuel).

## 2. Fair Value Gaps

> “Candle 3 leaves a gap between the wick of Candle 1 and Candle 3.”

`Detection.cs → DetectFvg`: for finalized candles `b-2, b-1, b`
- bullish: `Low[b] > High[b-2]`, zone `[High[b-2] … Low[b]]`
- bearish: `High[b] < Low[b-2]`, zone `[High[b] … Low[b-2]]`
- noise filter: gap ≥ max(`MinFvgTicks × tick`, `ATR × MinFvgAtrFraction`)
- the 50% midline (ICT “consequent encroachment”) is drawn dotted inside each zone.

## 3. Order Blocks

> “The last opposite-colored candle before a big price move … mark that candle’s
> open-to-close zone.”

`Detection.cs → CreateOrderBlock`, triggered **only when structure actually breaks**
(BoS or MSS), which encodes the book’s “strong market move” requirement:
- walk back ≤ `ObLookback` bars from the break candle to the last opposite candle;
- displacement filter: impulse from the OB extreme to the breaking close must be
  ≥ `ATR × DisplacementAtrFactor`;
- zone = open↔close body (default, as taught) or full range (`ObStyle`).

## 4. BoS & MSS

> “BoS = trend continuation signal. MSS = trend reversal signal — stronger. Wait for it
> before trading reversals.”

`Detection.cs → DetectStructureBreak`: close beyond the most recent unbroken swing
high/low. If it flips the tracked trend it is an **MSS** (solid, bold), otherwise a
**BoS** (dashed). MSS arms the entry model; BoS does not.

## 5. Premium / Discount (PD arrays, Power of 3)

> “Premium (above 50% of range) → look for shorts. Discount (below 50%) → longs.”

Dealing range = last confirmed swing low ↔ swing high. `EQ 50%` line plus subtle
premium/discount shading (`Rendering.cs → RenderPremiumDiscount`). With
`EntryNeedsPdAlignment` on, long signals only fire from zones whose midpoint sits at or
below equilibrium, shorts at or above — the accumulation→manipulation→distribution cycle
is traded on the correct side of the range.

## 6. Higher-timeframe framework

The book stresses HTF alignment without spelling out the mechanics, so the indicator
implements the standard institutional approach: chart candles are aggregated into
`HtfMinutes` buckets (`Detection.cs → UpdateHtf`), and the same FVG/OB logic runs on that
synthetic series. HTF zones are drawn over the LTF chart with thicker borders and higher
opacity — LTF entries inside an HTF zone are the highest-quality setups.

## 7. Entry model (Ch. 7, “Basic Entry Model — SMC style”)

State machine in `Intrabar.cs`:

1. **Liquidity sweep** — SSL taken primes longs, BSL taken primes shorts.
2. **MSS** in the opposite direction within `SweepToMssWindow` bars → model armed for
   `ArmWindowBars`.
3. **Return to zone** — first tick into an aligned, unmitigated FVG/OB on the correct
   side of equilibrium → 🟢/🔴 alert with entry, SL (`zone edge ± SlBufferTicks`) and
   2R / 3R targets (book: RRR ≥ 2:1–3:1).

The alert deliberately asks for lower-timeframe confirmation (rejection wick /
candlestick pattern / LTF MSS) — the book’s checklist requires confirmation before entry.

## 8. Reacting on touch, not close

Zone touches, sweeps, and touch-based mitigation all run in `ProcessIntrabar`, which
ATAS calls on **every tick** of the developing candle. Only pattern *formation* (swings,
FVGs, OBs, structure, BodyClose mitigation) waits for finalized candles, because those
definitions require a completed bar.

## Mitigation rules

| Rule | Meaning |
|---|---|
| `AnyTouch` | first touch consumes the zone |
| `Midline` | reaching 50% (consequent encroachment) consumes it |
| `FullFill` | trading through the far edge consumes it (default for FVG) |
| `BodyClose` | candle body closes beyond the far edge (default for OB — wick-throughs keep the OB alive) |
