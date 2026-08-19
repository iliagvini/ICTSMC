# The ICTSMC Playbook — Full Strategy Specification

This is the complete, extreme-detail specification of the strategy the indicator trades,
layer by layer, exactly as implemented in code. Where a rule comes from the book
(*Mastering ICT & SMC Trading*), the chapter is cited. Where the book was silent, the
standard institutional ICT convention was researched and adopted — those spots are marked
**[extension]**.

---

## 0. The market model (why any of this works)

The strategy rests on one core belief (Ch. 1–2): **institutions move markets, not retail
traders** — and institutions have a mechanical problem: their orders are too big to fill
without a counterparty. So they engineer liquidity:

1. **Accumulation** — build positions quietly in a range; the chart looks like "nothing".
2. **Manipulation** — push price *through* obvious levels (recent highs/lows) to trigger
   retail stop-losses and breakout orders. Those triggered orders are the counterparty
   that fills the institutional position. This looks like a breakout — it's a trap.
3. **Distribution** — the real move runs in the intended direction, leaving behind
   footprints: imbalances (FVGs) and origin candles (order blocks).

This is the **Power of 3 / AMD cycle** (Ch. 8). Every layer of the indicator exists to
detect one phase of this cycle, and the entry model chains them in the exact order the
cycle unfolds: *liquidity taken → structure shifts → price retraces to the footprint →
the distribution leg begins*.

---

## 1. Layer 0 — The two-speed processing pipeline

Everything else sits on top of a strict separation of concerns:

| Engine | Runs | Responsibility |
|---|---|---|
| **Bar-close engine** (`OnBarComplete`) | once per finalized candle | pattern *formation*: ATR, swings, BoS/MSS, FVGs, OBs, HTF candles, body-close mitigation, sweep classification |
| **Intrabar engine** (`ProcessIntrabar`) | **every tick** of the live candle | pattern *reaction*: zone touches, liquidity sweeps, touch-based mitigation, entry-model triggers |

Why the split matters:

- Patterns are **defined** on completed candles (an FVG literally requires three closed
  candles), so detecting them earlier would be guessing.
- But price **reacts** to a level the instant it is touched — waiting for a 1H candle to
  close after price tapped your FVG means being 59 minutes late. So every reaction runs
  tick-by-tick.
- A bar is known to be final when ATAS first calls with the *next* bar index. An
  already-consumed index is **ignored outright** — ATAS may revisit a finalized bar
  (amended history, provider corrections, a partial refresh), and re-running the
  bar-close engine there would double-count swings, structure, zones and HTF candles.
- Repeated calls on the same index mean live ticks, but only the **newest** bar of the
  series can genuinely be live, and the latch checks that. **Alerts are hard-gated to
  realtime only**, and a duplicated historical bar (some providers re-emit one during
  load) can no longer flip the whole replay into LIVE mode and spam Telegram with
  history.

---

## 2. Layer 1 — Swings & market structure (the skeleton)

Everything references swings, so they come first.

### 2.1 Swing detection (fractals)

A bar `p` is a **swing high** when its high is not exceeded by any bar within
`SwingPeriod` (default 3) bars on either side. Mirror rule for swing lows.

- A swing is only **confirmed** after `SwingPeriod` more bars close — a 3-bar fractal
  confirms 3 bars after the pivot. This lag is unavoidable and honest: any "faster"
  swing logic repaints.
- Every confirmed swing feeds two consumers: the structure tracker and the liquidity
  engine.

### 2.2 Trend state, BoS and MSS (Ch. 6)

The indicator maintains one trend state: **+1 bullish / −1 bearish / 0 undefined**, and
tracks the **protected** swing high and swing low (`UseProtectedSwings`, default on).

Taking the most *recent* pivot unconditionally is wrong: a lower high printed during a
pullback would replace the real, still-unbroken structural high above it, so breaking
that minor high registered a BoS/MSS, created an order block, re-anchored the dealing
range and flipped the trend. In ICT terms that is **internal** structure, not a break.
The defended swing is now replaced only when the old one has actually been broken, or
when the new pivot is more extreme (in which case price already traded through the old
one and it is no longer protecting anything).

On every candle **close**:

- Close **above** the last unbroken swing high →
  - trend was bearish → **MSS** (Market Structure Shift, CHoCH) — *reversal signal*,
    drawn solid & bold; **this is what arms the entry model**;
  - otherwise → **BoS** (Break of Structure) — *continuation signal*, drawn dashed.
  - Either way trend becomes +1 and that swing is marked broken (each swing can break
    exactly once — no duplicate events).
- Mirror logic for closing below the last unbroken swing low.

Book rule encoded: *"An MSS is stronger than a BoS — wait for it before trading
reversals"* — BoS events are informational; only MSS can arm an entry.

Close-through is required (not a wick-through): a wick beyond a swing that closes back
is precisely a *liquidity sweep*, and Layer 2 will catch it as such. This distinction —
**wick = sweep, body close = break** — is the hinge of the whole method.

---

## 3. Layer 2 — Liquidity (Ch. 3)

> "Liquidity = stop losses + pending orders sitting at obvious levels."

### 3.1 Where liquidity is mapped

- Every confirmed swing **high** ⇒ **BSL** (buy-side liquidity: buy-stops of shorts +
  breakout buy orders resting *above* it).
- Every confirmed swing **low** ⇒ **SSL** (sell-side liquidity below it).
- A new swing within `EqualLevelTicks` (default 3 ticks) of an existing unswept level is
  **merged** into it and the pool is flagged **EQH/EQL** (equal highs/lows), anchored at
  the extreme of the cluster and drawn thicker — clustered stops are a stronger magnet.
- **Previous day and previous week extremes** are mapped as **PDH / PDL / PWH / PWL**
  (`SessionLevelsEnabled`, default on) using the same bucket anchoring as the HTF
  aggregator, so `DailyAnchorMinutes` shifts them to a futures session open. These are
  the canonical ICT draws on liquidity — the levels every retail stop sits against — so
  only the most recent per side is live, and they are **exempt from the per-side cull**.
- Only the `MaxLiquidityPerSide` (default 8) most recent unswept *swing* levels per side
  are kept — old, distant swing liquidity is noise.

### 3.2 Sweep detection (intrabar) and classification (on close)

- The **instant** any tick trades above a BSL (or below an SSL), the level is marked
  swept and the 💧 alert fires — *no waiting for the close*, because the reversal often
  launches within seconds of the stop-run.
- When that candle closes, the sweep is classified:
  - closed **back inside** the level → **sweep (trap)** — manipulation confirmed; the
    market showed its hand; the level keeps a centered `Sweep` tag;
  - closed **through** → **run** — real breakout pressure; runs are unlabeled on the
    chart (the alert and journal still classify them).
- Simultaneously the sweep **primes the entry model**: SSL taken primes **longs**
  (institutions just bought retail's panic), BSL taken primes **shorts**.

---

## 4. Layer 3 — Fair Value Gaps (Ch. 4)

> "Price moves too quickly in one direction → not everyone got to enter → price often
> comes back to fill that gap."

### 4.1 Detection (on the 3rd candle's close)

For finalized candles `A, B, C`:

- **Bullish FVG:** `Low(C) > High(A)` → zone `[High(A) … Low(C)]` — the untraded void
  under the impulse candle B.
- **Bearish FVG:** `High(C) < Low(A)` → zone `[High(C) … Low(A)]`.

### 4.2 Noise filter

The gap must be ≥ **max(`MinFvgTicks` × tick, ATR₁₄ × `MinFvgAtrFraction`)**
(defaults: 2 ticks, 0.15 × ATR). The ATR leg auto-scales the filter across instruments
and volatility regimes; micro-gaps that get filled by spread noise never plot.

### 4.3 Anatomy & lifecycle

- Each FVG renders with its **50% midline** (ICT "consequent encroachment") dotted —
  the classic refined entry inside the gap. **[extension]**
- States: **Active** (full opacity) → **Touched** (first tap, dimmed, 🎯 alert fired
  once) → **Mitigated** (per rule below; faded further, then pruned).
- Mitigation rule, configurable, default **FullFill**: the FVG stays tradable until
  price has traded through its far edge — partial fills leave the rest of the gap valid.
  Alternatives: `AnyTouch`, `Midline`, `BodyClose`.

### 4.4 Inversion FVGs (IFVG) **[extension]**

When a candle **body closes through** a fair value gap, the gap has *failed* — and the
traders who bought/sold inside it are trapped. The zone flips polarity:

- broken **bullish** FVG → **IFVG▼** resistance (trapped longs sell into any retest);
- broken **bearish** FVG → **IFVG▲** support.

Rules: only plain FVGs invert (an inversion never re-inverts); the flip must happen at
the break or within 3 bars of a wick-based mitigation; HTF gaps invert into HTF iFVGs,
inheriting their layer label. IFVGs are full zones — drawn in their own colors
(teal ▲ / purple ▼), touch-alerted, and eligible for entry-model matches and confluence
scoring like any other zone. Toggle: `IfvgEnabled` (default on).

---

## 5. Layer 4 — Order Blocks (Ch. 5)

> "The last opposite-colored candle before a big price move — banks entered there, and
> price often comes back."

### 5.1 Detection — three conditions, all mandatory

An OB is only created when a **structure break** happens (Layer 1 fires BoS/MSS):

1. **Context:** the close broke a swing — this encodes the book's "big price move"
   objectively, instead of eyeballing "big".
2. **Origin candle:** walk back ≤ `ObLookback` (15) bars from the break candle to the
   *last opposite-colored candle* — last red before the bullish break, last green before
   the bearish one.
3. **Displacement filter — two independent proofs, because raw distance is not
   displacement:**
   - *magnitude* — the impulse from that candle's extreme to the breaking close must be
     ≥ **ATR₁₄ × `DisplacementAtrFactor`** (1.5);
   - *velocity* — the leg must have left a genuine **3-candle imbalance** behind
     (`RequireImbalanceForOb`, default on). An unfilled gap is ICT's own definition of a
     move too fast for the book to keep up with.

   Magnitude alone let a 15-bar grind covering 1.5 × ATR pass exactly like one violent
   displacement candle — and because the distance is measured from wherever the OB
   candle happens to sit, a *longer* `ObLookback` made the filter *easier*, which is
   precisely backwards. Rejections are journaled as `ObRejected` with both numbers.
   Together these keep the chart clean of the "every candle is an OB" clutter that
   plagues retail SMC charts.

   The **breaking candle itself is never a candidate**: the OB precedes the
   displacement, and the move *is* that candle.

### 5.2 Zone construction & lifecycle

- Default zone = the candle's **open↔close body** (exactly as the book teaches);
  `FullRange` (high↔low) available.
- Default mitigation = **BodyClose**: a wick punching through the OB does *not* kill
  it — that wick is often the stop-hunt into the zone that precedes the reversal. Only
  a candle **body closing beyond the far edge** invalidates the block.
- Duplicates are suppressed only when a same-kind active zone covers **substantially the
  same territory** (≥70% of the smaller zone). Zones that merely graze each other are
  kept: consecutive FVGs inside one impulse leg routinely touch, and in ICT a stacked
  cluster is a *stronger* draw, not a duplicate — silently dropping them under-counted
  the confluence stack the entry alert reports. Per-type caps keep at most
  `MaxZonesPerType` (25) active zones.

### 5.3 Breaker blocks **[extension]**

An order block that a candle **body closes through** has failed, and the participants
trapped inside it defend it from the other side on the retest. The zone flips polarity —
exactly the IFVG mechanic, applied to blocks:

- violated **bullish** OB → **bearish breaker** (resistance);
- violated **bearish** OB → **bullish breaker** (support).

Each block breaks at most once. Breakers are full zones — own colours, `BRK` tag,
touch-alerted, eligible for entry-model matches and confluence scoring like any other
zone. This is the structure the entry model's trap-arming already assumed existed.
Toggle: `BreakerBlocksEnabled` (default on).

---

## 6. Layer 5 — Premium / Discount (Ch. 8, PD arrays)

> "Premium (above 50% of range) → look for shorts. Discount (below 50%) → longs."

- **Dealing range** = most recent confirmed swing low ↔ swing high.
- **Equilibrium (EQ)** = its 50% line, drawn dash-dot with subtle red shading above
  (premium) and green below (discount).
- **Impulse-leg anchoring (default).** The range is re-anchored on every BoS/MSS to
  the CURRENT leg: origin extreme (the swing the move started from, extended to any
  unconfirmed higher-high/lower-low up to the break) ↔ the running extreme since the
  break, which extends bar by bar as the impulse grows. Equilibrium therefore tracks
  the leg you are actually trading. Journal audit of real sessions showed the old
  behavior (EQ hung from the stale pre-break extreme) vetoing valid post-MSS retrace
  shorts by 32–66 points; every re-anchor is journaled as `RangeAnchored` with leg
  high/low/EQ. Toggle: `DealingRangeFromLeg`.
- Fallback (before the first structure break, or with the toggle off): the confirmed
  swing pair, **order-corrected** — the engine walks back to the nearest consistent
  high>low pair, so equilibrium is never computed from an inverted range.
- This layer is a **filter, not a signal**: with `EntryNeedsPdAlignment` on (default),
  long entries only fire from zones whose midpoint sits at or below EQ, shorts at or
  above. A **tolerance band** (`PdTolerancePercent`, default 10% of the range) keeps
  strong zones sitting *near* equilibrium tradable instead of losing them to a
  2-tick technicality; every entry alert reports where the zone actually sat:
  `PD: Discount / Near EQ / Premium`. Buying deep in premium is paying retail prices —
  the exact mistake the cycle is designed to punish.
- **OTE (optimal trade entry)** **[extension]** — the `OteMinPercent`–`OteMaxPercent`
  band (0.618–0.79 by default) of the current impulse leg, ICT's refined entry pocket.
  Drawn as a shaded band (`ShowOte`, on) and optionally enforced as an entry filter
  (`OteFilterEnabled`, **off** by default — it is a genuine additional restriction).
  It applies only when the leg direction matches the trade side, because a retracement
  pocket is undefined against the leg, so it narrows entries without ever silently
  muting a whole side. Vetoes journal as `EntryRejected` with the exact band.

---

## 7. Layer 6 — The higher-timeframe framework **[extension, researched]**

The book repeatedly demands HTF alignment ("OBs are more powerful when aligned with
higher timeframes") without giving mechanics. The implemented mechanics:

### 7.1 Auto timeframe measurement

- The chart's timeframe is **measured from the data**: the mode (most frequent value)
  of consecutive bar-open time deltas. Session and weekend gaps are rare deltas and get
  outvoted. No platform API string is trusted — this cannot desync from reality.
- Tick/volume/range/renko charts have no dominant delta → detected as **irregular**,
  and the **median** bar duration rounded *up* to a standard TF becomes the basis
  (conservative: HTF too high is safe, too low is meaningless).

### 7.2 The institutional ladder

| Chart TF (measured) | Primary HTF | Second layer (`AutoSecondLayer`, default on) |
|---|---|---|
| ≤ 1m | 15m | 1H |
| 2–5m | 1H | 4H |
| 15m–1H | 4H | Daily |
| 2H–4H | Daily | Weekly |
| Daily+ | Weekly | — |

The chosen HTF is *guaranteed* strictly above the chart TF. Manual mode (fixed minutes)
remains available.

### 7.3 Aggregation correctness guarantees

- HTF candles are built by truncating candle open-times from **absolute ticks**, so a
  layer never drifts. With the default anchor of 0 a 4H bucket starts 00/04/08/12/16/20,
  daily at midnight, weekly Monday 00:00 (.NET tick zero is a Monday).
- **The session anchor applies to EVERY layer, not just daily and above.** This matters
  more than it sounds. Bucket *phase* is not a detail: measured on identical price data,
  shifting the 4H buckets by two hours changed **100% of the detected 4H FVG
  boundaries** — same prices, same rules, a completely different set of gaps. A
  session-based instrument (futures opening 18:00) whose HTF layers are hard-anchored to
  midnight therefore produces 4H zones that do not exist on the platform's own H4 chart.
  Set `Session anchor` so the synthetic buckets line up with your platform's HTF candles.

  **How to find your value:** open the HTF chart, read any candle's open time, convert to
  minutes after midnight, and take it modulo the layer size. H4 candles opening at
  01:00/05:00/09:00… → `60 mod 240` = **60**. Candles opening on the even 4-hour marks →
  **0** (the default).
- On configuration the aggregators are **retro-fed the entire loaded history**, making
  HTF zones *path-independent*: identical whether the chart was just opened or watched
  live all day.
- Any HTF setting change rebuilds everything from bar 0 — state is never patched.
- The **on-chart badge** (`HTF auto: 4H + D · chart 1H`) makes the selection verifiable
  at a glance after every timeframe switch.

### 7.4 Body-close semantics belong to the layer

An HTF zone is judged on ITS OWN candle bodies. A 4H fair value gap is broken when a 4H
candle body closes through it — not when some 15-minute candle does. Running the
chart-timeframe body-close pass over HTF zones gave a 4H gap sixteen inversion
opportunities per 4H candle on an M15 chart, and let a brief dip that a real 4H candle
would have absorbed as a wick flip the zone permanently. Each layer now settles accounts
with its own zones when its candle closes, before that candle is allowed to mint new ones.

*Wick*-based mitigation (`FullFill`, `Midline`, `AnyTouch`) deliberately stays on chart
bars: those are price-level tests, and "price traded through X" is timeframe-independent —
detecting it immediately is the point of the intrabar engine. Only body-close semantics
are timeframe-dependent.

Mitigated HTF zones are also retained for at least four candles **of their own layer**
rather than `KeepMitigatedBars` chart bars, or a filled 4H gap would be pruned before its
own candle ever closed on it and could never invert.

**Verified:** on identical price data, an M15 chart with a 4H layer now reproduces a native
4H chart's fair value gaps exactly — 16 of 16 with identical boundaries — and every iFVG
the native chart produces, with identical boundaries.

### 7.5 HTF detection

The *same* FVG rule runs on the synthetic HTF series, with its noise filter scaled to
**that layer's own average range** — never the chart-TF ATR. Measuring a 4H gap against
`0.15 × a 5m ATR` is no filter at all, so every micro-imbalance qualified as an "HTF
FVG"; and because HTF zones drive the A+/A++ tier, that inflated the very tiering the
analytics exist to compare.

HTF order blocks need displacement, a structure break, **and** an imbalance in the leg —
the same three-part proof the chart-TF rule demands: the candle's range ≥
`HtfDisplacementFactor` × its 10-candle average, its close beyond the prior
`HtfStructureLookback` candles' extreme, and a 3-candle gap left behind in the leg.

**Known divergence:** even so, HTF order blocks are detected by a *displacement proxy*,
while chart-TF order blocks come from the full swing/BoS structure engine, which is not
run on the synthetic HTF series. So a `4H OB` drawn on an M15 chart is not guaranteed to
be an `OB` a native H4 chart would draw — on test data the proxy produced roughly twice as
many. Fair value gaps, which are purely geometric, match exactly; order blocks are the
one family where the two paths can legitimately disagree. Size alone
qualified before — and a wide-range HTF candle is very often a *reversal* (an engulfing
top, a news spike), whose "last opposite candle" is not an institutional origin block
at all.

HTF zones map back to the chart bar where the HTF candle began, draw as gold 2px
frames, and are labeled `4H FVG`, `D OB`, etc.

**How to weigh them:** an LTF entry landing *inside* an HTF zone is the A+ setup. The
HTF zone is the reason for the trade; the LTF zone is the trigger.

---

## 8. Layer 7 — The entry model (Ch. 7, the strategy's spine)

A three-stage state machine per direction, chaining the AMD cycle in order. Long side
shown; shorts mirror.

**Stage 1 — Manipulation: liquidity sweep.**
Any tick below an SSL primes the long side (records the sweep bar). With
`RequireSweepForEntry` on (default), no sweep = no long, period — an MSS without a prior
stop-run is a much weaker reversal.

**Stage 2 — Confirmation: MSS.**
A *bullish MSS* (close above the last swing high while trend was bearish) within
`SweepToMssWindow` (40) bars of the sweep **arms** the long model for `ArmWindowBars`
(30) bars. BoS does not arm. Timeouts guarantee stale sweeps can't produce entries days
later, and unconsumed sweeps expire on their own after the window passes.

**Structural invalidation (failed MSS).** The clock is not the only way out: an MSS is
fresh information for *both* sides. If a bearish MSS prints while the long model is
armed (or vice versa), the armed setup was built on a **failed shift** — it is cancelled
immediately (`CancelOnOppositeMss`, default on) and a ⚠️ *Failed MSS* alert fires,
because a failed shift is itself one of the strongest seeds of the opposite setup (it is
how breaker-block reversals form). The stale sweep priming is cleared with it.

**Trap arming (`ArmOnFailedMss`, default on) — and its budget.** The traders trapped in
the failed shift *are* the liquidity, so the failure itself counts as the sweep
precursor and the new side is **auto-armed** on the spot, typically resolving into a
retest of an IFVG or breaker zone.

Left unbounded, that hand-off **defeats `RequireSweepForEntry` entirely**. The model
bootstraps itself: sweep → arm long → bearish MSS traps it → arm short *without a
sweep* → bullish MSS traps that → arm long *without a sweep* → … And because the tracked
trend flips on every alternating break, **every alternating break is an MSS**, so in a
range the machine ping-pongs between sides indefinitely off one historical sweep — in
exactly the chop the book tells you to avoid.

`MaxTrapChainHops` (default 1) caps how many consecutive trap-arms may sit between an
arming and a **real** liquidity sweep. Every arming records its `TrapDepth` in the
journal, so `ArmSource=TrapArm` rows can be scored against genuine `Sweep` rows honestly.
Set it to 0 to require a literal BSL/SSL sweep on the new side before arming.

**Stage 3 — Entry: return to the footprint.**
While armed, the first tick that trades **through the proximal edge** of an *aligned*
zone triggers the signal. Aligned = all of:
- bullish zone (bullish OB, FVG, iFVG or breaker, LTF or HTF), formed before the current bar;
- **unmitigated**;
- the candle actually **crossed the zone's entry edge this bar** — not merely "is in
  contact with it";
- midpoint at or below equilibrium + tolerance band — if PD alignment is on;
- inside the OTE band — if the OTE filter is on;
- inside a killzone — if the killzone filter is on.

The edge-cross requirement is not pedantry. The test it replaced asked only whether the
candle's low had reached the zone top, which stays true while price is anywhere *below*
a bullish zone. FVGs self-healed (FullFill mitigation kills them in the same pass) but
order blocks did not, because BodyClose mitigation can only be judged on a finished
candle — so while price sliced *down* through a bullish OB, the block still counted as
touched and a LONG could fire quoting an entry **above the market** with a stop price
that had already traded. It then resolved in the journal as an ordinary −1R loss rather
than as the malformed signal it was.

Contact without an edge cross (price was already inside the zone when the model armed)
is journaled as `EntryRejected` and **leaves the setup armed** for a clean re-entry.

**Stage 4 — Killzone gate (`KillzoneFilterEnabled`, default off).** ICT is fundamentally
time-based; entries fire only inside the configured `KillzoneWindows`
(`02:00-05:00, 07:00-10:00, 13:30-16:00` by default — London, NY AM, NY PM in platform
time). It ships **off** only because the correct clock times depend on your platform's
timezone, which the indicator cannot know: set the windows to your chart's time, then
turn it on. Outside a window the setup stays armed rather than being discarded, and the
suppression is journaled once per bar. A malformed window string fails **open** — a typo
must never silently mute every signal.

**Confluence scoring.** All zones touched by that tick are collected and the setup is
tiered: any Daily/Weekly zone in the stack → **A++** (🟢🟢🟢), any other HTF zone →
**A+** (🟢🟢), LTF-only → **B** (🟢). The alert lists the full stack
(`Confluence: D OB▲ + 4H FVG▲ + FVG▲`) so a naked 15m tap and a triple-stacked A++
are unmistakably different messages. The trade plan is built from the **trigger zone**
(the one price physically touched first), keeping the stop structural and tight.

The 🟢/🔴 alert ships a complete trade plan (tier marks repeated by confluence —
🟢🟢🟢 = A++):

- **Entry** ≈ zone top (bull) / zone bottom (bear) — the edge price just touched;
- **SL** = opposite zone edge ± `SlBufferTicks` (4) — *structure-based, beyond the zone*
  (Ch. 9: "SL based on structure, not emotion");
- **TP(2R) and TP(3R)** — the book's minimum RRR of 2:1–3:1 pre-computed;
- an explicit reminder to confirm with a **rejection wick / candlestick pattern / lower-TF
  MSS** before executing — the model finds the setup; the checklist's confirmation step
  (Ch. 10) stays with the trader.

Firing consumes the armed state — one signal per sweep→MSS cycle, no machine-gunning
the same zone.

**C-tier: continuation signals (Non-ICT concept).** The core model is a
reversal-retracement machine — it will never buy a fresh premium FVG mid-rally, and
that discipline is correct per the book. But those momentum-continuation touches
exist and some of them work, so instead of excluding them untracked, they fire as an
explicitly demoted tier (🟡 **C**, `ArmSource=Continuation`). A C-tier signal fires
on the **first touch** of a zone when ALL of these hold (toggle
`ContinuationSignalsEnabled`, default on):

1. **trend-aligned** — bullish zone with bullish tracked structure (mirror for
   shorts); never counter-trend;
2. **fresh** — the zone is at most `ContinuationMaxAgeBars` (20) bars old;
3. **outside the core model** — that side is unarmed (no sweep→MSS chain) *or*
   armed but the zone fails the PD limit (the premium-continuation case). If the
   armed model can fire from the zone, C stays silent — never a double signal;
4. **once per zone**.

The alert and journal row state the exact exclusion reason (`no sweep→MSS chain` /
`PD override`), and the plan math (entry/SL/TP2/TP3) is identical to A/B signals.
C rows flow through the full pipeline — outcomes, BE/partial shadow management,
and the Tier/ArmSource analytics — so after a few weeks the data will say exactly
what the continuation play earns versus the core model.
A++/A+/B behavior is completely unchanged.

---

## 9. Layer 8 — Alerts & delivery

| Alert | Timing | Default |
|---|---|---|
| 🟢/🔴 entry model (tiered, full trade plan + confluence + PD status) | tick of the return | **on** |
| 💧 liquidity taken (side, pool label, next step hint) | tick of the cross | off |
| 🎯 zone touch (zone tag + expected reaction) | first tick into zone | off |
| 📐 BoS / MSS (direction + meaning) | candle close | off |
| ⚠️ failed MSS (armed setup structurally invalidated) | candle close of the opposite MSS | off |
| ❌ signal zone invalidated — the zone behind a still-open signal was consumed (exit/tighten cue) | tick for touch-based rules; candle close for BodyClose | **on** |
| 🔁 zone re-touched — info only, no trade plan (first-touch signals stay exclusive; episodes separated by ≥1 clean bar away) | tick of re-entry | off |
| 🚨 exit warning — an OPEN signal is threatened: an opposing BoS/MSS printed, or displacement carved a fresh opposing zone; each threat class warns once per signal | candle close | **on** |
| 📦 zone created | candle close | off |

**Default delivery profile: Telegram-only, entries + exit warnings.** Popup alerts
default OFF, and the only alert types on by default are the entry signals
(🟢/🔴/🟡), the ❌ signal-zone invalidation and the 🚨 exit warning — the messages
you act on. Sweeps, structure, touches and re-touches remain available as toggles
for anyone who wants the full narration back.

- **Realtime-gated** — never fires during history replay.
- **De-duplicated** — one touch alert per zone lifetime, one sweep alert per level, one
  entry per cycle.
- Delivery: ATAS popup + **Telegram** (background fire-and-forget HTTP; a network stall
  can never freeze the chart thread). Telegram messages are signed with the chart
  identity — instrument + measured timeframe (`💹 GC 1H`) — so multiple charts and
  bots stay unambiguous.
- **Remote /shot command** — the bot also listens (one long-poll loop per bot token,
  process-wide hub shared by all charts): send `/shot`, get an inline-button list of
  the charts wired to that bot/chat, tap one, and receive a freshly self-rendered
  PNG of that chart (candles + zones + liquidity + EQ + structure in the indicator's
  own style). Rendered from data, not screen-captured — works with ATAS minimized.
  Unknown chat ids are ignored. Toggle: *Remote commands (/shot snapshots)*.

---

## 10. Layer 9 — Risk management (Ch. 9; executed by the trader)

The indicator computes SL/TP; the discipline is yours. Non-negotiable rules from the
book:

1. **Risk per trade: 1–2%** of capital. Position size = risk amount ÷ SL distance —
   *calculated, never guessed*. (₹20,000 × 1% = ₹200; SL 10 pts → 20 units.)
2. **RRR ≥ 2:1**, prefer 3:1 — at 3:1 you're profitable winning only ~30% of trades.
3. **SL is structural** (beyond the zone, where the idea is wrong) and **never moved**.
4. **Daily loss limit ~3%** → stop for the day. No revenge trades.
5. Trade **London / New York** sessions; avoid sideways chop (Ch. 7 entry tips).
6. Checklist before entry (Ch. 10): liquidity taken ✓ · FVG present ✓ · OB nearby ✓ ·
   MSS visible ✓ · premium/discount correct ✓ · TP & SL defined ✓ · news checked ✓.

---

## 11. A complete long trade, step by step

1H chart → badge reads `HTF auto: 4H + D · chart 1H`.

1. Price grinds down; trend = −1. Two swing lows form within 3 ticks → merged **EQL
   pool** below.
2. A 1H wick spikes below the EQL → 💧 *"Sell-side liquidity taken (equal lows). Watch
   for bullish MSS → long setup."* Candle closes back above → classified `✕ sweep` —
   manipulation confirmed. Long side primed.
3. The reversal leg displaces upward, leaving a bullish FVG, and 6 bars later closes
   above the last swing high → 📐 **bullish MSS**. Model armed for 30 bars. The impulse
   also broke structure with 1.5×ATR displacement → the last red candle before it is
   marked **OB▲**. Suppose it sits inside a **4H FVG▲** — HTF confluence.
4. EQ of the new dealing range is drawn; the OB▲ sits in **discount**. All checklist
   boxes are filling.
5. Price retraces; the *first tick* into the OB▲ →
   🟢 *"ENTRY MODEL LONG — sweep + MSS + return to OB▲ 6041.25–6044.50. Entry ~6044.50 |
   SL 6040.25 | TP(2R) 6053.00 | TP(3R) 6057.25. Confirm with a rejection wick /
   lower-TF MSS before executing."*
6. You check: rejection wick forming on the retest, no red-folder news, size = 1% ÷ SL
   distance → execute, set-and-forget SL/TP. Win or lose, the process was correct — and
   at 3:1 the math only needs ~1 win in 3.

---

## 12. Live rendering & the audit trail

**Clean display mode (default).** The chart shows only what is tradeable now:
mitigated/invalidated zones vanish instantly (kept briefly in data for the iFVG
window and the journal); zones stop one bar past the live candle instead of smearing
to the right edge; only the nearest `MaxVisibleZonesPerSide` unmitigated zones per
side within `ZoneVisibilityAtrRange × ATR` render (HTF zones get double range so a
fresh previous-day OB stays visible); HTF zones draw as gold 2px frames rather than
fills; EQH/EQL pools and session extremes draw with a heavier stroke; MSS lines are
solid and heavier while BoS lines stay dashed and light; labels are precisely centered
on translucent backdrop pills and auto-hide when they don't fit; only the last
`MaxStructureLabels` BoS/MSS events and recently swept liquidity remain. `Detailed`
mode disables culling for post-session review.

**Rendering is snapshot-based.** `OnRender` runs on the chart's drawing thread while
`OnCalculate` runs on the data thread. The renderer never touches the engine's live
collections: the calculation thread publishes an immutable `RenderModel` with one
volatile write, and the renderer takes one volatile read and works from value types.
Enumerating live `List<T>` state across threads is a real data race — not just
"collection was modified", but torn reads of the backing array mid-resize, which can
surface as a wrong price with no exception at all. `OnRender` is also wrapped in a
catch-all: ATAS gives it no exception boundary, so a throw there degrades the chart for
the rest of the session, and dropping one frame is always the better trade.

**Journal.** Four CSVs per session under `Documents\ATAS\ICTSMC-Journal`:
events (zone lifecycle, sweeps, structure, failed MSS), signals (full trade plan +
arm source + confluence), outcomes (SL/TP2/TP3/Timeout with conservative SL-first
resolution, R-multiple, MAE/MFE in R, bars held), and analytics (win-rate and
expectancy grouped by zone family, layer, arm source, tier, direction).

**Excursion tracking includes the signal bar.** Signals fire *intrabar*, so skipping
that bar hid the single most adverse stretch of a tap-and-fail: MAE came out
systematically understated, and a same-bar stop-out was never recorded as one — it
survived to resolve later as a timeout or even a win. The candle's extremes at the
firing instant are captured on the record, and only excursion **beyond** them counts as
the trade's own, so pre-entry price action on that bar is excluded in both directions.

**Analytics are bounded and off-thread.** The report used to be rebuilt on the chart
thread after *every* resolution — eight groupings over an unbounded, ever-growing pool,
so cost per signal grew linearly and per session quadratically, with memory to match.
The chart thread now only bounds the pool (`AnalyticsMaxSignals`, default 5000) and
takes a shallow snapshot; grouping, formatting and the file write happen on the
serialized IO chain, and a burst of resolutions coalesces into one rewrite. If trimming
ever occurs it is stated in the file — `outcomes.csv` always stays complete.

By default
only LIVE rows are written (`Journal LIVE rows only`, on) — files stay lean and
every row is a real-time event. Turning the toggle off makes the next
recalculation regenerate the full HIST backfill: a deterministic backtest of the
identical live code path, available on demand because replay always rebuilds it
from the candles.

**Shadow trade management.** Alongside the raw fixed-stop outcome, every signal is
also resolved under two virtual management styles — never traded, only logged — so
the journal accumulates a three-way comparison (`RMultiple` vs `BE1R_R` vs
`Partial2R_R` in outcomes; `AvgR` vs `AvgBE1R_R` vs `AvgPartial2R_R` in analytics).
Both simulations run bar-by-bar on completed bars with the same conservative rule
as the raw engine: if a bar touches both the (virtual) stop and a target, the stop
counts first — including the very bar a trigger level is reached. Exact rules:

- **BE-at-+1R** — the moment a bar's range reaches `entry + 1R` (mirrored for
  shorts), the virtual stop jumps to entry. From that bar on (inclusive): a return
  to entry exits at **0R**; TP3 = **+3R**; TP2 reached latches and resolves **+2R**
  at timeout; timeout without TP2 = close-based R. If price is stopped before ever
  reaching +1R, the shadow result is the raw **−1R**; if +1R is never reached at
  all, it simply equals the raw outcome.
- **Partial-at-+2R** — when a bar's range reaches `entry + 2R`, half the position
  is banked (0.5 × 2R = **+1R locked**) and the remaining half runs toward TP3 with
  its stop moved to entry. Remainder outcomes: return to entry → total **+1R**;
  TP3 → +1R + 0.5 × 3R = **+2.5R**; timeout → +1R + 0.5 × close-based R. Stopped
  before +2R is ever reached → raw **−1R**; +2R never reached → equals the raw
  outcome.

This is the data that answers, over weeks of live signals, whether the MFE giveback
seen in early sessions (winners retracing to the fixed stop after being +1R/+2R in
profit) makes breakeven or partial management strictly better than the raw plan —
per zone family, layer, arm source and tier, not as an anecdote.

**Decision log.** Non-fires are logged as explicitly as fires — every suppression
point in the entry model writes an event with exact metrics:

| Event | Meaning | Metrics recorded |
|---|---|---|
| `Armed` | a side armed after MSS | source, MSS bar/level, sweep bar, sweep age vs window, armed-until bar |
| `ArmRejected` | MSS printed but arming failed | MSS level, whether a sweep existed, its age vs `SweepToMssWindow`, trap-arm status |
| `ArmExpired` | armed window ran out untouched | source, armed-at bar, bars waited vs `ArmWindowBars` |
| `EntryRejected` | armed + zone touched, PD filter vetoed | zone id/tag, zone mid, EQ, tolerance, exact excess/shortfall beyond the limit, arm source (latched once per zone) |
| `SweepExpired` | sweep aged out with no MSS | sweep bar, age vs window |
| `RangeAnchored` | dealing range re-anchored on BoS/MSS | leg high/low with their bars, resulting EQ |
| `FailedMSS` | armed setup structurally cancelled | cancelling MSS level, armed-at bar, bars in, window remaining, trap-arm result |
| `ExitWarning` | open signal threatened (structure / opposing zone) | signal id + tier + source + trigger, entry, unrealized R at warning, exact threat metrics |
| `ObRejected` | structure broke but no order block was drawn | leg bars, impulse distance vs ATR threshold, whether the leg left an imbalance |
| `SessionLevel` | a PDH/PDL/PWH/PWL was registered | side, price, which session |
| `ZoneBroken` | an order block was violated and flipped into a breaker | flipped zone id/tag and range |

`EntryRejected` now also covers three new suppression reasons, each with its numbers:
the **killzone** gate, the **OTE** band, and **contact without an edge cross** (price was
already inside the zone when the model armed). `Armed` and `ArmRejected` additionally
carry `TrapDepth` — how many failed-MSS hops that arming sits from a real sweep.

`ExitWarning` rows are journaled whether or not the 🚨 alert is enabled. The alert toggle
used to gate the whole check, so silencing the notification also destroyed the data that
would have told you whether the notification was worth keeping.

Every `ZoneTouch` is therefore classifiable post-hoc: fired / PD-vetoed /
model-not-armed / model-expired — with the numbers to prove which. Subsequent
touch episodes journal as `ZoneRetouch` (touch number + zone age), so re-touch
quality — does touch #2 reject or break through? — is measurable from the data.

## 13. Honest limitations

- Swings confirm with a `SwingPeriod` lag; structure events therefore lag pivots. This
  is the cost of zero repainting.
- **SMT divergence is not implemented.** It requires a second, correlated instrument's
  series (ES vs NQ, EURUSD vs DXY), and this indicator is single-series by construction.
  It is a genuine ICT gap, not an oversight — implementing it needs a data-subscription
  design, not a filter.
- Killzone times are **platform-local**. The indicator measures the bar timeframe from
  the data but cannot know your platform's timezone, which is why the killzone filter
  ships off: set `KillzoneWindows` to your chart's clock first.
- The trap-arm budget bounds the ping-pong but there is still **no explicit
  chop/consolidation detector**. In a genuinely rangebound market the model will produce
  fewer signals than before, not zero — the killzone filter is the practical second
  line of defence.
- `Nullable` is left disabled project-wide. Null-safety here is by convention and
  reviewed by hand, not enforced by the compiler.
- The first HTF candle of loaded history can be partial if the history starts
  mid-bucket (true of any aggregator). Everything after is exact.
- Synthetic HTF candles are built from loaded chart bars — load enough history for the
  Daily/Weekly layers to be meaningful.
- The entry alert is a *setup detector with a plan*, not an auto-trader: the final
  confirmation step is deliberately human, per the book's checklist.
