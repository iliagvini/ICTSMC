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
- A bar is known to be final when ATAS first calls with the *next* bar index. Repeated
  calls on the same index can only happen live — that's how the indicator knows history
  ended, and **alerts are hard-gated to realtime only** (a chart reload can never spam
  Telegram with historical events).

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
tracks the most recent *unbroken* swing high and swing low.

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
- Only the `MaxLiquidityPerSide` (default 8) most recent unswept levels per side are
  kept — old, distant liquidity is noise.

### 3.2 Sweep detection (intrabar) and classification (on close)

- The **instant** any tick trades above a BSL (or below an SSL), the level is marked
  swept and the 💧 alert fires — *no waiting for the close*, because the reversal often
  launches within seconds of the stop-run.
- When that candle closes, the sweep is classified:
  - closed **back inside** the level → **sweep (trap)** — manipulation confirmed; the
    market showed its hand; drawn as `✕ sweep`;
  - closed **through** → **run** — real breakout pressure, drawn as `✕ run`.
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
3. **Displacement filter:** the impulse from that candle's extreme to the breaking close
   must be ≥ **ATR₁₄ × `DisplacementAtrFactor`** (1.5). A structure break without
   displacement is a grind, not an institutional entry — no OB is drawn. This filter
   is what keeps the chart clean of the "every candle is an OB" clutter that plagues
   retail SMC charts.

### 5.2 Zone construction & lifecycle

- Default zone = the candle's **open↔close body** (exactly as the book teaches);
  `FullRange` (high↔low) available.
- Default mitigation = **BodyClose**: a wick punching through the OB does *not* kill
  it — that wick is often the stop-hunt into the zone that precedes the reversal. Only
  a candle **body closing beyond the far edge** invalidates the block.
- Overlapping duplicates of the same type are suppressed; per-type caps keep at most
  `MaxZonesPerType` (25) active zones.

---

## 6. Layer 5 — Premium / Discount (Ch. 8, PD arrays)

> "Premium (above 50% of range) → look for shorts. Discount (below 50%) → longs."

- **Dealing range** = most recent confirmed swing low ↔ swing high.
- **Equilibrium (EQ)** = its 50% line, drawn dash-dot with subtle red shading above
  (premium) and green below (discount).
- The pair is **order-corrected**: after a strong one-way run the latest swing high can
  sit below the latest swing low; the engine walks back to the nearest consistent pair,
  so equilibrium is never computed from an inverted range.
- This layer is a **filter, not a signal**: with `EntryNeedsPdAlignment` on (default),
  long entries only fire from zones whose midpoint sits at or below EQ, shorts at or
  above. A **tolerance band** (`PdTolerancePercent`, default 10% of the range) keeps
  strong zones sitting *near* equilibrium tradable instead of losing them to a
  2-tick technicality; every entry alert reports where the zone actually sat:
  `PD: Discount / Near EQ / Premium`. Buying deep in premium is paying retail prices —
  the exact mistake the cycle is designed to punish.

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

- HTF candles are built by truncating candle open-times from **absolute ticks** — a 4H
  bucket always starts 00/04/08/12/16/20, daily at midnight, weekly Monday 00:00 (.NET
  tick zero is a Monday). Zero drift by construction.
- `DailyAnchorMinutes` shifts daily+ buckets to a futures session open (e.g. 1080 =
  18:00 platform time) so "daily" zones match the exchange session, not calendar
  midnight.
- On configuration the aggregators are **retro-fed the entire loaded history**, making
  HTF zones *path-independent*: identical whether the chart was just opened or watched
  live all day.
- Any HTF setting change rebuilds everything from bar 0 — state is never patched.
- The **on-chart badge** (`HTF auto: 4H + D · chart 1H`) makes the selection verifiable
  at a glance after every timeframe switch.

### 7.4 HTF detection

The *same* FVG rule runs on the synthetic HTF series. HTF OBs use a displacement proxy
(HTF candle range ≥ 1.3 × its 10-candle average, origin = last opposite HTF candle).
HTF zones map back to the chart bar where the HTF candle began, draw with thicker
borders/higher opacity, and are labeled `4H FVG▲`, `D OB▼`, etc.

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

**Trap arming (`ArmOnFailedMss`, default on).** The traders trapped in the failed shift
*are* the liquidity — so the failure itself counts as the sweep precursor and the new
side is **auto-armed** on the spot. Sweep → MSS → failure → the opposite entry model is
live immediately, typically resolving into a retest of an IFVG or breaker-style zone.
Turn it off to require a literal BSL/SSL sweep on the new side before arming.

**Stage 3 — Entry: return to the footprint.**
While armed, the **first tick** into *aligned* zones triggers the signal. Aligned =
all of:
- bullish zone (bullish OB or bullish FVG, LTF or HTF), formed before the current bar;
- **unmitigated**;
- midpoint at or below equilibrium + tolerance band — if PD alignment is on.

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

---

## 9. Layer 8 — Alerts & delivery

| Alert | Timing | Default |
|---|---|---|
| 💧 liquidity taken (side, EQH/EQL, next step hint) | tick of the cross | on |
| 🎯 zone touch (zone id + expected reaction) | first tick into zone | on |
| 📐 BoS / MSS (direction + meaning) | candle close | on |
| 🟢/🔴 entry model (tiered, full trade plan + confluence + PD status) | tick of the return | on |
| ⚠️ failed MSS (armed setup structurally invalidated) | candle close of the opposite MSS | on |
| 📦 zone created | candle close | off |

- **Realtime-gated** — never fires during history replay.
- **De-duplicated** — one touch alert per zone lifetime, one sweep alert per level, one
  entry per cycle.
- Delivery: ATAS popup + **Telegram** (background fire-and-forget HTTP; a network stall
  can never freeze the chart thread).

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
fresh previous-day OB stays visible); HTF zones draw as gold frames rather than
fills; labels are precisely centered on backdrop pills and auto-hide when they
don't fit; only the last `MaxStructureLabels` BoS/MSS events and recently swept
liquidity remain. `Detailed` mode disables culling for post-session review.

**Journal.** Four CSVs per session under `Documents\ATAS\ICTSMC-Journal`:
events (zone lifecycle, sweeps, structure, failed MSS), signals (full trade plan +
arm source + confluence), outcomes (SL/TP2/TP3/Timeout with conservative SL-first
resolution, R-multiple, MAE/MFE in R, bars held), and analytics (win-rate and
expectancy grouped by zone family, layer, arm source, tier, direction). Historical
rows are flagged HIST and act as a backtest of the identical live code path.

## 13. Honest limitations

- Swings confirm with a `SwingPeriod` lag; structure events therefore lag pivots. This
  is the cost of zero repainting.
- The first HTF candle of loaded history can be partial if the history starts
  mid-bucket (true of any aggregator). Everything after is exact.
- Synthetic HTF candles are built from loaded chart bars — load enough history for the
  Daily/Weekly layers to be meaningful.
- The entry alert is a *setup detector with a plan*, not an auto-trader: the final
  confirmation step is deliberately human, per the book's checklist.
