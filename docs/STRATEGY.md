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

  Which windows count is decided by `Detection.cs → ImbalanceScanStart`, and it matters.
  A three-candle window ending at bar `b` spans `[b-2, b]`, so the window ending one
  candle after the order block **straddles the OB itself** — which is exactly where the
  gap sits in the textbook shape: the last opposite candle, then one explosive candle
  whose low opens clear of the high before it. The scan used to start two candles after
  the OB and therefore skipped that window entirely, so whenever the order block sat
  immediately before the breaking candle the loop ran zero times and the most canonical
  OB in the book was rejected as drift. The scan now starts at the leg's first candle and
  is clamped so the window ending at the break bar is always examined, which also covers
  a leg that is a single engulfing candle. The identical rule runs on the HTF series
  (`HtfLegHasImbalance`), so the two layers cannot disagree.

  **And when the gap has not formed yet, the proof is held over one candle**
  (`ResolvePendingOrderBlocks`, `ResolvePendingHtfOrderBlocks`). Widening the window was
  necessary but not sufficient: when the displacement *is* the breaking candle, the imbalance
  it leaves spans `[break-1, break, break+1]`, so at the moment structure breaks it does not
  exist and no scan can find it. Field journals showed the cost — 11 of 12 rejections had the
  block one or two candles before the break, roughly 46% of all candidates discarded. The
  block is therefore parked rather than refused, and re-tested once the next candle closes:
  one chart candle for chart-TF blocks, one candle *of that layer* for HTF blocks. Magnitude
  is still decided immediately, because it is fully determined at the break and cannot
  improve — and a magnitude failure now journals rather than returning silently. Held-over
  blocks that qualify journal as `ObConfirmed`. HTF candidates are keyed by first-chart-bar
  rather than buffer index, so a trim between break and re-test cannot mis-resolve them.
  The cost, accepted deliberately: an order block can now appear one bar after its break.
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
A break that flips the tracked trend is a *candidate* MSS (solid, heavier stroke);
everything else is a **BoS** (dashed, light). MSS arms the entry model; BoS does not.

**An MSS must also displace** (`Detection.cs → ClassifyBreak`, `BreakDisplaced`, toggle
`RequireDisplacementForMss`, default on). Direction alone made "MSS" a synonym for *this
break went the other way to the last one* — so in a range every oscillation between the
same two extremes was a structural shift, and because an MSS is what arms the entry model,
the machine was most active exactly where the book says to stand aside. `MaxTrapChainHops`
does not help here: it bounds the trap-arm chain, while range extremes are swept constantly
and those sweeps close back inside, so `RequireTrapForEntry` passes too.

The proof is the pair `CreateOrderBlock` already demands, applied to the break's own leg —
magnitude (`ATR × DisplacementAtrFactor` from the leg's origin) **and** velocity (the leg
left an unfilled imbalance). A reversal that fails it is recorded as a BoS: it still flips
the trend and still produces its order block, it simply does not arm a reversal setup.
Demotions are journaled as `MssDemoted` with both numbers. The imbalance scan always
reaches the break bar's own three-candle window, so a single engulfing displacement candle
— which *is* the leg — is not mistaken for drift.

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
first break — **or whenever the leg is too small to mean anything**
(`Intrabar.cs → LegIsUsableAsRange`, `MinDealingRangeBars` 5, `MinDealingRangeAtr` 1.5×).
The leg re-anchors on every structure break, so a break that lands right after the extreme
produces a leg one or two bars long, and everything downstream inherits the distortion:
equilibrium, the premium/discount verdict, the PD tolerance band — which is a *percentage of
that range*, so it collapsed to well under a point — and the OTE pocket. Field journals show
spans of 1 and 3 bars and heights swinging between 7.9 and 258 points on one instrument, with
an entry vetoed by 0.2 points against an EQ derived from a single candle. A leg that fails the
guard is not discarded; the range simply falls back to the confirmed swing pair, the OTE band
reports no band rather than a fictitious one, and the `RangeAnchored` row records which range
was actually used. `EQ 50%` line plus optional premium/discount shading
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
- **HTF order blocks run the real structure engine** (`Detection.cs → UpdateHtfStructure`,
  `CreateHtfOrderBlock`), not a proxy. Each layer keeps its own ATR, fractal swings and
  protected-swing state, and an OB requires a close beyond the protected swing plus the
  same magnitude and imbalance proofs the chart timeframe demands. The previous shortcut
  used a Donchian rolling-extreme breakout as its "structure break", which fires where no
  swing exists. Verified: an M15 chart with a 4H layer now reproduces a native 4H chart's
  FVGs, iFVGs and OBs with identical counts and identical boundaries.

### Auto HTF selection

- The chart timeframe is **measured from the data**, never read from platform strings:
  the mode of consecutive bar-open time deltas wins (session/weekend gaps are outvoted).
  If no delta dominates (tick/volume/range/renko charts), the **median** bar duration is
  rounded up to the next standard TF as a conservative basis. Sub-minute charts keep
  their real label (`30s`) instead of collapsing to `1m`.
- Ladder (`Detection.cs → AutoLadder`): `≤1m → 15m + 1H + 4H`, `≤5m → 1H + 4H + D`,
  `6m–1H → 4H + D`, `2H–4H → D + W`, `>4H → W`. Every layer is guaranteed to sit strictly
  above the chart TF, and the extra context layers are optional (`AutoSecondLayer`, on).
- A **third** rung is added only when the first two fail to reach a Daily-or-higher layer.
  The A++ tier is defined as "a zone from a layer ≥ 1440 minutes", so a two-rung 5m chart
  (1H + 4H) had the top tier permanently unreachable and no Daily context at all, while a
  15m chart beside it had both. Charts that already reach Daily on their second rung —
  15m, 30m, 1H, 4H — are deliberately left untouched, because appending a further rung
  there would mean Weekly and roughly 27,000 chart bars to feed it. `MaxAutoLayers` caps
  the climb at three, so a 1m chart still cannot reach Daily; that is a stated limit, not
  an oversight.
- The chart timeframe is measured by `Detection.cs → UpdateChartTimeframe`, which runs on
  every bar close **regardless of whether HTF mapping is enabled** — it is a property of the
  chart, not of the feature. Deriving it inside `ConfigureHtfLayers` meant that turning HTF
  off stripped the timeframe out of every alert identity (`GC` instead of `GC 1H`) and out
  of the `/shot` chart names, and left the mitigated-zone retention maths without a scale.
- Buckets are truncated from absolute ticks, so they never drift. Phase is controlled by
  two independent settings: `IntradayAnchorMinutes` (15m/1H/4H) and `DailyAnchorMinutes`
  (D/W, and PDH/PDL/PWH/PWL). Both default to 0 = clock-aligned, which matches ATAS
  (4H opens 00/04/08/12/16/20, 1H on the hour). They are separate because an instrument
  can be clock-aligned intraday and session-based daily; one shared anchor could not serve
  both. Phase matters — a two-hour shift changes 100% of the detected 4H FVG boundaries.
- Weekly buckets carry a **weekday** anchor as well (`WeeklyAnchorMode`, Auto). .NET tick
  zero is a Monday, so an unshifted week always opened Monday at the daily anchor — which
  folded roughly an extra day of the current week into "last week" for any instrument whose
  week opens Sunday evening, and with it PWH/PWL and the W layer. Auto resolves to Sunday
  when a recurring daily session gap was detected and Monday otherwise, keeping 24/7 and
  cash instruments on the calendar week.
- Each layer's swing bookkeeping stores **indices into that layer's candle buffer**, and the
  buffer is trimmed at 400 candles — about four days on a 15m layer. `RebaseHtfSwings`
  shifts the stored indices to follow the trim and drops pivots whose candle is gone.
  Without it the pivot de-duplication check compared a fresh index against a stale one.
- The daily anchor is measured, not configured (`DailyAnchorMode` = Auto):
  `Detection.cs → DetectSessionAnchorMinutes` finds the trading day's start from the
  recurring daily gap in bar timestamps, over ~30 days so the window stays inside one
  daylight-saving regime. A fixed value cannot be correct year-round — GC's 17:00 Chicago
  open is 00:00 on a UTC+2 chart in summer and 01:00 in winter. Falls back to the calendar
  day when the evidence is thin (e.g. a 24/7 instrument). Reported on the badge and
  journaled as `SessionAnchor`.
- HTF zones are mitigated/inverted on **their own layer's candle bodies**
  (`Detection.cs → ApplyHtfBodyClose`); wick-based rules stay intrabar on chart bars.
  Mitigated HTF zones are retained for four candles of their own layer so the layer's
  body-close pass can still reach them.
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
3. **Return to zone** — the first tick that trades **through the proximal edge, approaching
   from outside** (`Intrabar.cs → EntryEdgeTraded`) triggers the signal. Two separate
   mistakes have been closed here. Contact alone is not enough: the one-sided test stayed
   true while price was anywhere *below* a bullish zone, so a long could fire with its
   quoted entry above the market. And the straddle test that replaced it was
   direction-blind — `high >= Top && low <= Top` is equally true of price *leaving* the
   zone upward, which is the common shape when the MSS printed on the retest and the model
   armed with price already inside. The bar must now **open** at or beyond the edge, which
   also makes the trigger-selection rule (highest top for a long, on the assumption of a
   falling tap) true by construction. Either failure is journaled as `EntryRejected` and
   leaves the setup armed.
4. **Risk bounds** (`MinRiskTicks`, `MaxRiskAtr`, both 0 = off). Risk is entirely the
   trigger zone's height plus the buffer, and zone heights span orders of magnitude: a
   2-tick imbalance yields a stop inside the spread, a Daily order block one so wide that
   3R cannot be reached before the signal times out, so the trade resolves by clock rather
   than by outcome. Applied as a filter over candidate zones, so a rejection never consumes
   the armed setup.
5. **HTF bias** (`HtfBiasFilterEnabled`, off). Every HTF layer already runs the full
   swing/protected-swing/break engine, but the resulting bias used to be computed and
   discarded — HTF touched nothing but the confluence tier, so an A++ short against bullish
   Daily structure was indistinguishable from one aligned with it. The bias is now recorded
   on **every** signal (`HtfBias` in signals.csv, and in the alert) whether or not the
   filter is on, so its value is measurable from the journal before it is trusted to veto.
6. **Killzone gate** (`ICTSMCStrategy.cs → InKillzone`, `KillzoneFilterEnabled`, off by
   default): entries only inside the configured session windows. Off by default only
   because the correct times depend on your platform's timezone, which the indicator
   cannot know — ICT practice is to enable it. Outside a window the setup stays armed.
   The time tested is the candle's `LastTime`, not its open: testing the open quantised
   every window to the bar grid, so on a 1H chart a `13:30-16:00` killzone admitted
   nothing before 14:00 and on a 4H chart the configured times were close to meaningless.

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

`OnRender` runs on the chart's drawing thread; `OnCalculate` runs on the data thread; the
Telegram `/shot` renderer runs on a poller thread. **None of them touch the engine's live
collections.** The calculation thread publishes an immutable `RenderModel` snapshot
(`ICTSMCStrategy.cs → PublishRenderModel`) with a single volatile write, and every consumer
performs a single volatile read and works from value types only. Enumerating the live
`List<T>` state across threads was a genuine data race — not merely “collection was
modified”, but torn reads of the backing array during a resize. `OnRender` is additionally
wrapped in a catch-all, because ATAS gives it no exception boundary of its own and a throw
there degrades the chart for the session.

The `/shot` snapshot renderer used to be the exception: it read `_zones`, `_liquidity` and
`_structure` directly and called `GetCandle`/`GetDealingRange` from the poller thread, and
answered the race with a three-attempt retry. A retry catches an exception; it cannot catch
a torn read, and a `decimal` is 16 bytes, so a price written concurrently could be observed
half-updated and drawn into an image the user then trades from. It now renders from the
same published snapshot — which carries a rolling buffer of completed candles plus the
live one — and needs no retries at all.

The snapshot is also **cheap to republish**: the collections are rebuilt only when engine
state actually changed, while the price-relative scalars refresh on every tick. Marking the
model dirty on every tick that merely touched a zone meant copying up to a few hundred
`ZoneView`s plus three list allocations per tick, on the data thread, for an identical
result.

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
