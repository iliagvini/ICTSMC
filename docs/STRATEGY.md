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
- **Inversion FVGs** (`Detection.cs → ApplyBodyCloseMitigation`): a body close through
  a gap flips it (BullFvg → BearIfvg resistance, BearFvg → BullIfvg support), carrying
  any HTF layer label; inversions never re-invert. Toggle `IfvgEnabled`.

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
implements the standard institutional approach: chart candles are aggregated into HTF
time buckets (`Detection.cs → UpdateHtf`), and the same FVG/OB logic runs on that
synthetic series. HTF zones are drawn over the LTF chart as gold frames — LTF entries inside an HTF zone are the highest-quality setups.

### Auto HTF selection

- The chart timeframe is **measured from the data**, never read from platform strings:
  the mode of consecutive bar-open time deltas wins (session/weekend gaps are outvoted).
  If no delta dominates (tick/volume/range/renko charts), the **median** bar duration is
  rounded up to the next standard TF as a conservative basis.
- Ladder: `≤1m → 15m (+1H)`, `≤5m → 1H (+4H)`, `≤1H → 4H (+D)`, `≤4H → D (+W)`,
  `>4H → W`. The chosen HTF is guaranteed to sit strictly above the chart TF; a second
  layer is optional (`AutoSecondLayer`).
- Buckets are truncated from absolute ticks, so they always align to clock boundaries
  (:00 for 1H, 00:00 for D, Monday 00:00 for W — .NET tick zero is a Monday). Daily and
  weekly buckets can be shifted with `DailyAnchorMinutes` to match a futures session
  open (e.g. 1080 = 18:00 platform time).
- On configuration the aggregators are **retro-fed the entire chart history**, so HTF
  zones are identical whether you loaded the chart fresh or watched it live all day —
  no path dependence.
- Any HTF setting change triggers a full recalculation from bar 0 (never a patched
  state), and the on-chart badge shows `HTF auto: 4H + D · chart 1H` so the selection
  is verifiable at a glance on every timeframe switch.

## 7. Entry model (Ch. 7, “Basic Entry Model — SMC style”)

State machine in `Intrabar.cs`:

1. **Liquidity sweep** — SSL taken primes longs, BSL taken primes shorts. Unconsumed
   sweeps expire after `SweepToMssWindow` bars.
2. **MSS** in the opposite direction within `SweepToMssWindow` bars → model armed for
   `ArmWindowBars`. An opposite MSS while armed = **failed shift**: the setup is
   cancelled immediately (`CancelOnOppositeMss`) and a ⚠️ Failed MSS alert fires —
   structural invalidation, not just a clock timeout. With `ArmOnFailedMss` (default
   on) the trapped traders count as the liquidity precursor and the opposite side is
   auto-armed on the spot (trap/IFVG continuation entry).
3. **Return to zone** — first tick into aligned, unmitigated FVG/OB zone(s) on the
   correct side of equilibrium (± `PdTolerancePercent` band) → tiered 🟢/🔴 alert:
   Daily/Weekly confluence = A++ (🟢🟢🟢), any HTF = A+ (🟢🟢), LTF-only = B (🟢),
   listing the full zone stack and PD status, with entry, SL
   (`zone edge ± SlBufferTicks`) and 2R / 3R targets (book: RRR ≥ 2:1–3:1).
   Equilibrium always comes from an order-corrected dealing range (never inverted).

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
