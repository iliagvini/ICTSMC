# Strategy → Code mapping

This document maps every concept from *Mastering ICT & SMC Trading* (and the standard
institutional ICT extensions the book only hints at) to the exact place in the code.
It is kept literal on purpose: if a claim here disagrees with the code, the claim is
the bug.

## 1. Liquidity (BSL / SSL, equal highs/lows, session extremes)

> “Liquidity = stop losses + pending orders sitting at obvious levels.”

- Every confirmed fractal swing high registers a **BSL** level at its high; every swing
  low registers **SSL** (`ICTSMCStrategy.Detection.cs → RegisterLiquidity`).
- Swings within `EqualLevelTicks` of an existing unswept level are merged into a single
  pool flagged **EQH/EQL** — drawn with a heavier stroke because clustered stops are a
  stronger draw.
- **Previous day / previous week extremes** (`Detection.cs → UpdateSessionLevels`) are
  registered as **PDH / PDL / PWH / PWL** using the same bucket anchoring as the HTF
  aggregator, so `DailyAnchorMinutes` shifts them to a futures session open. Only the
  most recent extreme per side is live, and session levels are exempt from the
  `MaxLiquidityPerSide` cull — they are the canonical ICT draws, not noise.
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

Detection is deliberately **not** gated on the `ShowFvg` display toggle — the entry
model, confluence scoring and the journal all consume these zones, so hiding them on
the chart must never change what the strategy does. The same holds for `ShowOb`.

## 3. Order Blocks & Breakers

> “The last opposite-colored candle before a big price move … mark that candle’s
> open-to-close zone.”

`Detection.cs → CreateOrderBlock`, triggered **only when structure actually breaks**
(BoS or MSS), which encodes the book’s “strong market move” requirement:
- walk back ≤ `ObLookback` bars from the break candle to the last opposite candle
  (the breaking candle itself is never a candidate — the move *is* that candle);
- **magnitude:** impulse from the OB extreme to the breaking close ≥ `ATR × DisplacementAtrFactor`;
- **velocity:** the leg must contain a genuine 3-candle imbalance
  (`Detection.cs → LegHasImbalance`, toggle `RequireImbalanceForOb`). Distance alone is
  not displacement — without this a 15-bar grind covering 1.5 × ATR passed exactly like
  one violent candle, and a *longer* lookback made the filter *easier*, which is
  backwards. Rejections are journaled as `ObRejected`.
- zone = open↔close body (default, as taught) or full range (`ObStyle`).

**Breaker blocks** (`Detection.cs → ApplyBodyCloseMitigation`, toggle
`BreakerBlocksEnabled`): an order block a candle body closes through has *failed*, and
the participants trapped inside it defend it from the other side on the retest. A
violated bullish OB flips into a **bearish breaker** (resistance) and vice versa —
the same mechanic as the IFVG, applied to blocks. Breakers are full zones: coloured,
touch-alerted, and eligible for entry matches and confluence scoring.

## 4. BoS & MSS

> “BoS = trend continuation signal. MSS = trend reversal signal — stronger. Wait for it
> before trading reversals.”

`Detection.cs → DetectStructureBreak`: close beyond the **protected** swing high/low.
If it flips the tracked trend it is an **MSS** (solid, heavier stroke), otherwise a
**BoS** (dashed, light). MSS arms the entry model; BoS does not.

**Protected swings** (`Detection.cs → AdoptProtectedHigh/AdoptProtectedLow`, toggle
`UseProtectedSwings`, default on). Taking the most *recent* pivot unconditionally meant
a lower high printed during a pullback replaced the real, still-unbroken structural high
above it — so breaking that minor high registered a BoS/MSS, created an order block,
re-anchored the dealing range and flipped the trend. That is **internal** structure, not
a break. The defended swing is now only replaced when the old one has actually been
broken, or when the new pivot is more extreme.

## 5. Premium / Discount (PD arrays, Power of 3) and OTE

> “Premium (above 50% of range) → look for shorts. Discount (below 50%) → longs.”

Dealing range = the **current impulse leg** (origin extreme ↔ running extreme,
re-anchored on every BoS/MSS — `Detection.cs → AnchorLeg`; toggle
`DealingRangeFromLeg`), falling back to the last confirmed swing pair before the
first break. `EQ 50%` line plus optional premium/discount shading
(`Rendering.cs → RenderPremiumDiscount`). With `EntryNeedsPdAlignment` on, long signals
only fire from zones whose midpoint sits at or below equilibrium, shorts at or above.

**OTE** (`Intrabar.cs → GetOteBand`, `FilterByOte`): the optimal-trade-entry pocket —
the `OteMinPercent`–`OteMaxPercent` (0.618–0.79) retracement band of the current leg.
Drawn as a shaded band (`ShowOte`, on) and optionally enforced as an entry filter
(`OteFilterEnabled`, off by default). It only applies when the leg direction matches
the trade side, so it narrows entries without ever silently muting a whole side.

## 6. Higher-timeframe framework

The book stresses HTF alignment without spelling out the mechanics, so the indicator
implements the standard institutional approach: chart candles are aggregated into HTF
time buckets (`Detection.cs → UpdateHtf`), and the same FVG/OB logic runs on that
synthetic series. HTF zones are drawn over the LTF chart as gold 2px frames — LTF
entries inside an HTF zone are the highest-quality setups.

- **HTF FVG noise filter scales to the HTF layer's own average range**
  (`HtfAggregator.AverageRange`), never the chart-TF ATR. Measuring a 4H gap against
  0.15 × a 5m ATR was no filter at all, and because HTF zones drive the A+/A++ tier,
  that inflated the very tiering the analytics exist to compare.
- **HTF order blocks require a structure break too** (`Detection.cs → HtfBrokeStructure`):
  the displacement candle must close beyond the prior `HtfStructureLookback` candles'
  extreme. Size alone qualified before — and a wide-range candle is very often a
  *reversal* (an engulfing top, a news spike), whose “last opposite candle” is not an
  institutional origin block at all.

### Auto HTF selection

- The chart timeframe is **measured from the data**, never read from platform strings:
  the mode of consecutive bar-open time deltas wins (session/weekend gaps are outvoted).
  If no delta dominates (tick/volume/range/renko charts), the **median** bar duration is
  rounded up to the next standard TF as a conservative basis. Sub-minute charts keep
  their real label (`30s`) instead of collapsing to `1m`.
- Ladder: `≤1m → 15m (+1H)`, `≤5m → 1H (+4H)`, `6m–1H → 4H (+D)`, `2H–4H → D (+W)`,
  `>4H → W`. The chosen HTF is guaranteed to sit strictly above the chart TF; a second
  layer is optional (`AutoSecondLayer`).
- Buckets are truncated from absolute ticks, so they always align to clock boundaries
  (:00 for 1H, 00:00 for D, Monday 00:00 for W — .NET tick zero is a Monday). Daily and
  weekly buckets can be shifted with `DailyAnchorMinutes` to match a futures session
  open (e.g. 1080 = 18:00 platform time).
- On configuration the aggregators are **retro-fed the entire chart history**, so HTF
  zones are identical whether you loaded the chart fresh or watched it live all day —
  no path dependence.
- Any setting that participates in DETECTION triggers a full recalculation from bar 0
  (`ICTSMCStrategy.cs → Set<T>`) — state is never patched to a rule it wasn't built
  under. The trigger only arms after the first calculation, so restoring a saved chart
  does not rebuild once per persisted property.

## 7. Entry model (Ch. 7, “Basic Entry Model — SMC style”)

State machine in `Intrabar.cs`:

1. **Liquidity sweep** — SSL taken primes longs, BSL taken primes shorts. Unconsumed
   sweeps expire after `SweepToMssWindow` bars.
2. **MSS** in the opposite direction within `SweepToMssWindow` bars → model armed for
   `ArmWindowBars`. An opposite MSS while armed = **failed shift**: the setup is
   cancelled immediately (`CancelOnOppositeMss`) and a ⚠️ Failed MSS alert fires. With
   `ArmOnFailedMss` the trapped traders count as the liquidity precursor and the
   opposite side is auto-armed — **but that hand-off is budgeted** by
   `MaxTrapChainHops` (default 1). Unbounded, it let the model bootstrap itself:
   sweep → arm long → bearish MSS traps it → arm short (no sweep) → bullish MSS traps
   that → arm long (no sweep) → … Because the tracked trend flips on every alternating
   break, every alternating break is an MSS, so in a range the machine ping-ponged
   forever off one historical sweep while `RequireSweepForEntry` was on. Each arming
   now records how many trap hops it sits from a real sweep (`TrapDepth` in the
   journal), and arming is refused past the budget.
3. **Return to zone** — the first tick that trades **through the proximal edge** of an
   aligned, unmitigated zone (`Intrabar.cs → EntryEdgeTraded`) triggers the signal.
   Contact alone is not enough: the one-sided test it replaced stayed true while price
   was anywhere *below* a bullish zone, so a long could fire with its quoted entry above
   the market and its stop already traded through. Contact without an edge cross is
   journaled as `EntryRejected` and leaves the setup armed.
4. **Killzone gate** (`ICTSMCStrategy.cs → InKillzone`, `KillzoneFilterEnabled`, off by
   default): entries only inside the configured session windows. Off by default only
   because the correct times depend on your platform's timezone, which the indicator
   cannot know — ICT practice is to enable it. Outside a window the setup stays armed.

Confluence tiering: Daily/Weekly zone in the stack → **A++**, any HTF zone → **A+**,
LTF-only → **B**; C-tier is the explicitly demoted non-ICT continuation play.
SL = opposite zone edge ± `SlBufferTicks`; TP at 2R and 3R.

## 8. Reacting on touch, not close

Zone touches, sweeps, and touch-based mitigation all run in `ProcessIntrabar`, which
ATAS calls on **every tick** of the developing candle. Only pattern *formation* (swings,
FVGs, OBs, structure, BodyClose mitigation) waits for finalized candles, because those
definitions require a completed bar.

`OnCalculate` ignores an already-consumed bar index outright: ATAS may revisit a
finalized bar (amended history, provider corrections, a partial refresh), and re-running
the bar-close engine would double-count swings, structure, zones and HTF candles.
Realtime mode is only latched by a repeated call on the **newest** bar, so a duplicated
historical bar can no longer make the whole replay fire alerts and journal itself as LIVE.

## 9. Threading

`OnRender` runs on the chart's drawing thread; `OnCalculate` runs on the data thread.
The renderer never touches the engine's live collections. The calculation thread
publishes an immutable `RenderModel` snapshot (`ICTSMCStrategy.cs → PublishRenderModel`)
with a single volatile write, and `OnRender` performs a single volatile read and works
from value types only. Enumerating the live `List<T>` state across threads was a genuine
data race — not merely “collection was modified”, but torn reads of the backing array
during a resize. `OnRender` is additionally wrapped in a catch-all, because ATAS gives
it no exception boundary of its own and a throw there degrades the chart for the session.

## Mitigation rules

| Rule | Meaning |
|---|---|
| `AnyTouch` | first touch consumes the zone |
| `Midline` | reaching 50% (consequent encroachment) consumes it |
| `FullFill` | trading through the far edge consumes it (default for FVG) |
| `BodyClose` | candle body closes beyond the far edge (default for OB — wick-throughs keep the OB alive) |

`Midline` and `FullFill` are level-crossing tests and are evaluated whether or not the
candle's range *overlaps* the zone — price can cross clean past a zone in one gap or one
violent bar, and gating them on contact would leave such a zone alive and tradeable.
