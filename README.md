# ICTSMC — ICT / Smart Money Concepts Zones for ATAS

A live ATAS indicator that implements the full playbook from *Mastering ICT & SMC Trading*
(Vineesh Rohini) and fires **instant popup + Telegram alerts** — including the moment price
merely **touches** a zone, because price often reacts immediately without waiting for the
candle to close.

## What it draws & detects

| Concept (book chapter) | Implementation |
|---|---|
| **Liquidity** (Ch. 3) | BSL above swing highs / SSL below swing lows, merged into **equal-highs/lows pools** (EQH/EQL), dashed lines until taken |
| **Liquidity sweep vs. run** (Ch. 3) | Intrabar cross detection; on candle close it is classified: closed back inside = **sweep (trap)**, closed through = **run** |
| **Fair Value Gaps** (Ch. 4) | 3-candle imbalance with tick + ATR minimum-size filters, midline (consequent encroachment) shown, configurable mitigation |
| **Inversion FVGs (IFVG)** | A body close through a gap flips its polarity: failed bullish FVG → resistance, failed bearish FVG → support; distinct teal/purple colors; failed MSS also auto-arms the opposite entry side (trap entry, toggleable) |
| **Order Blocks** (Ch. 5) | Last opposite-colored candle before a displacement that **breaks structure**; body-only (open↔close, as taught) or full-range zones |
| **BoS / MSS** (Ch. 6) | Fractal swings → close-through breaks of the **protected** swing (internal pullback highs/lows no longer count); continuation = BoS (dashed, light), reversal = MSS/CHoCH (solid, heavier) |
| **Entry model** (Ch. 7) | State machine: *liquidity sweep → MSS → price returns to aligned FVG/OB in the correct half of the range* → entry alert with SL + 2R/3R targets |
| **Premium / Discount, Power of 3** (Ch. 8) | Equilibrium (50%) of the current dealing range, subtle premium/discount shading; entry model only buys in discount / sells in premium |
| **Higher-timeframe framework** | **Auto mode (default):** the indicator *measures* the chart timeframe from the data itself and picks the institutional ladder (1m→15m+1H+4H, 2–5m→1H+4H+**D**, 6m–1H→4H+D, 2H–4H→D+W, >4H→W); HTF FVGs and OBs are mapped onto your chart with stronger styling. A third rung is added only where the first two would not reach a Daily layer, so the A++ tier is reachable on sub-5m charts too. An on-chart badge shows exactly what was detected/chosen. Manual mode with fixed minutes is still available |

## Alerts

All alerts fire **only in realtime** (never while history is replayed) and are de-duplicated
per zone/level/event. **Default delivery profile: Telegram-only** (popups off) carrying
just the actionable messages — entries and exit warnings; everything else is an opt-in
toggle:

**On by default:**

- 🟢/🔴 **Entry model** — sweep + MSS + return to zone, tiered by confluence
  (🟢🟢🟢 A++ with Daily/Weekly zone, 🟢🟢 A+ with any HTF zone, 🟢 B standalone;
  🟡 **C** = trend-aligned fresh-zone *continuation* touch outside the core model —
  explicitly labeled Non-ICT, lowest tier, tracked separately in analytics),
  with zone stack, PD status (Discount/Near EQ/Premium), entry, SL and 2R/3R targets
- ❌ **Signal zone invalidated** — the FVG/iFVG/OB behind a still-open signal got
  consumed: the structural basis of the trade is gone, consider exiting/tightening
- 🚨 **Exit warning** — an open signal is under threat: an opposing BoS/MSS printed,
  or displacement carved a fresh opposing zone. Shows the signal, unrealized R and
  the exact metrics; each threat class warns once per signal

**Off by default (toggles):**

- 🎯 **Zone touch** — instant, on the first tick into an FVG/OB (no close needed)
- 💧 **Liquidity taken** — BSL/SSL crossed, with the follow-up hint (watch for MSS)
- 📐 **BoS / MSS** — structure events with direction
- ⚠️ **Failed MSS** — an armed setup structurally invalidated by an opposite MSS
- 🔁 **Zone re-touched (info only)** — price returned to an already-touched zone
- 📦 **Zone created**

### What the engine adds beyond the book

- **Breaker blocks** — an order block a body closes through flips polarity and is traded
  from the other side on the retest (`BreakerBlocksEnabled`, on).
- **PDH / PDL / PWH / PWL** — previous day and previous week extremes mapped as liquidity
  pools, exempt from the swing-level cull (`SessionLevelsEnabled`, on).
- **OTE band** — the 0.618–0.79 retracement pocket of the current impulse leg, drawn
  always (`ShowOte`) and optionally enforced as an entry filter (`OteFilterEnabled`, off).
- **Killzone filter** — entries only inside your session windows (`KillzoneFilterEnabled`,
  **off** by default). It ships off only because the right clock times depend on your
  platform's timezone: set `KillzoneWindows` (default `02:00-05:00, 07:00-10:00,
  13:30-16:00`) to your chart's time, then switch it on. ICT practice is to use it.
- **Trap-arm budget** — `MaxTrapChainHops` (default 1) caps how far an armed setup may
  sit from a real liquidity sweep, so "require a sweep" stays true in chop.
- **Trap, not run** — `RequireTrapForEntry` (default on): a liquidity event only arms a
  reversal if price closed back *inside* the level. A clean break that closes through is a
  breakout, and a reversal armed off it trades against the move that just proved itself.

### Behaviour changes you should know about

**From the code audit (this build):**

- **Canonical order blocks are no longer rejected.** The imbalance scan started two
  candles after the order block, so the window that straddles the OB itself — the gap
  left by *last opposite candle, then one explosive candle* — was never examined. Whenever
  the OB sat immediately before the breaking candle the scan ran zero times and the most
  textbook shape in the book was journaled as "drift, not displacement". Expect **more**
  order blocks, and specifically the good ones; the gapless grind this filter exists to
  reject is still rejected. Same fix on the HTF series.
- **An MSS must displace** (`RequireDisplacementForMss`, **on**) — "MSS" previously meant
  nothing more than *this break went the other way to the last one*, so in a range every
  oscillation between the same two extremes armed the entry model. A reversal break now
  has to clear the same two proofs an order block demands — ATR magnitude and a genuine
  imbalance in the leg — or it is recorded as an ordinary BoS. It still flips the trend and
  still produces its order block; it simply does not arm a reversal. Demotions are
  journaled as `MssDemoted` with the numbers. **Expect materially fewer signals in chop.**
- **The entry edge must be crossed from OUTSIDE the zone.** The previous test asked only
  whether the bar had touched both sides of the proximal edge — equally true of price
  *leaving* the zone. A long could still fire quoting an entry below the market when the
  model armed while price sat inside a bullish zone. The bar must now open at or beyond
  the edge.
- **History replay no longer skips the signal bar.** Replayed signals had both excursion
  marks set to the completed candle's own extremes, so the signal bar contributed zero
  MAE/MFE and a same-bar stop-out could not be recorded — the `HIST` backfill was
  optimistic in exactly the way it was documented not to be. Replayed signals now treat the
  whole signal bar as exposure (stop-first, as everywhere else) and are tagged
  `bar-conservative` in the new `Sequenced` column.
- **Weekly buckets have a weekday anchor** (`WeeklyAnchorMode`, Auto). Weeks used to always
  open Monday, so for any session instrument PWH/PWL and the W layer folded roughly an
  extra day of the current week into "last week". Auto picks Sunday when a daily session
  gap is detected, Monday otherwise.
- **Killzones are matched against the tick, not the bar open.** On a 1H chart a
  `13:30-16:00` window admitted nothing before 14:00, because the 13:00 bar opens outside
  it. The window you configure is now the window that runs, on any timeframe.
- **The chart timeframe is measured even with HTF mapping off**, so alert identities keep
  their `GC 1H` suffix and `/shot` chart names stay unambiguous.
- **Telegram delivery is ordered, rate-limited and instrumented.** Sends were independent
  fire-and-forget tasks whose responses were discarded, so an exit warning could overtake
  its own entry and a throttled alert was indistinguishable from a delivered one. Failures
  now journal as `AlertFailed` with the status code.
- **New opt-in filters** (both off, both journaled when they veto): `HtfBiasFilterEnabled`
  refuses entries against a higher-timeframe layer's own structure, and
  `MinRiskTicks` / `MaxRiskAtr` reject plans whose stop distance is inside the noise or too
  wide for 3R to be reachable. The HTF bias is **recorded on every signal either way**, so
  its value can be measured from the journal before it is trusted to veto anything.

**From earlier builds:**

- **Protected-swing structure** (`UseProtectedSwings`, on) — breaking a minor pullback
  high is internal structure and no longer prints a BoS/MSS, creates an order block, or
  re-anchors the dealing range. Expect noticeably fewer, better structure events.
- **Entry needs an edge cross** — a signal now requires price to trade *through* the
  zone's entry edge on that bar, not merely to be in contact with the zone. This closes
  a real bug where a long could fire with its quoted entry above the market.
- **Order blocks need an imbalance** (`RequireImbalanceForOb`, on) — a slow grind that
  merely covers the ATR distance no longer qualifies as displacement.
- **HTF gaps are filtered against the HTF layer's own range**, not the chart ATR, so far
  fewer spurious HTF zones — and therefore fewer inflated A+/A++ tiers.
- **HTF order blocks now need a structure break**, not just a wide candle.
- **Display toggles no longer change detection** — hiding FVGs or OBs is purely visual.
- **Every detection setting triggers a clean recalculation** instead of leaving state
  built under the old rules.
- **MAE/MFE now include the signal bar**, so historical stats are stricter (and honest).
- **TP2 is no longer an outcome.** The raw model resolves SL / TP3 / Timeout only; a 2R
  exit is a management style and is reported in the Partial-at-+2R shadow column. Headline
  AvgR and win rate were previously biased upward by trades that merely tagged 2R.

### Expected build warning

Every build prints one `MSB3277`: a `WindowsBase` v4.0.0.0 vs v10.0.0.0 conflict. **This is
expected and harmless.** The conflict is between `ATAS.Indicators.dll`'s own reference and
the WindowsDesktop reference pack — this project uses no WPF and no WinForms type at all
(`UseWindowsForms` is set only to pull in `System.Drawing.Common` for the chart snapshot),
so the built DLL neither references nor ships `WindowsBase`, and ATAS resolves it from the
WPF assemblies already loaded in its process.

You can confirm it on your own build:

```powershell
[Reflection.Assembly]::LoadFrom("$env:APPDATA\ATAS\Indicators\ICTSMCStrategy.dll").GetReferencedAssemblies() |
  Select-Object Name, Version | Sort-Object Name
```

`WindowsBase` should not appear in that list. Don't "fix" the warning by changing
`UseWindowsForms` without testing the resulting DLL inside ATAS — see the comment in the
csproj.

### Telegram setup

1. Create a bot with [@BotFather](https://t.me/BotFather) → copy the **bot token**.
2. Message your bot once, then open `https://api.telegram.org/bot<TOKEN>/getUpdates`
   and copy your **chat id** (for a group, add the bot to the group and use the negative id).
3. In the indicator settings → *10. Telegram*: enable, paste token + chat id.

Messages are sent fire-and-forget on a background thread — network issues can never
freeze the chart. Every message is signed with the chart identity — instrument **and
timeframe** (`💹 GC 1H`, `💹 NQ 15m`) — so multi-chart setups stay unambiguous.
Different charts can use different bots/chats (e.g. Gold → group, NQ → private bot):
save per-chart settings as ATAS templates.

### Remote commands: /shot snapshots

With *Remote commands (/shot snapshots)* enabled (default), the bot also **listens**:

- Send **/shot** (or /charts) to the bot → it replies with a button list of every
  chart wired to that bot + chat (`📸 GC 1H`, `📸 NQ 15m`).
- Tap a button → a **fresh chart image renders on the spot** and arrives as a photo:
  last ~120 candles plus all active zones, liquidity lines, EQ and structure markers,
  in the indicator's visual style.

The image is drawn from live data, not screen-captured — it works with ATAS minimized
or the chart in a background tab. Commands from unknown chat ids are ignored. One
long-poll loop runs per bot token, shared by all charts in the ATAS instance (don't
run two ATAS terminals polling the same bot — Telegram allows one listener per token).

## Build & install

Requirements: ATAS installed on the build machine, plus the .NET **SDK** matching the
runtime your ATAS runs on.

The project reads `OFT.Platform.runtimeconfig.json` from your ATAS folder and targets that
framework automatically — the install *folder name* is not a reliable signal, and current
standard ATAS is a live example: `C:\Program Files (x86)\ATAS Platform` reports
`"tfm": "net10.0"` despite the `(x86)` path. The build prints both the resolved folder and
the framework on a line starting `ICTSMC:`; check it matches the ATAS you actually run.
Override with `-p:AtasTfm=net8.0-windows` if needed.

**One command (Windows)** — builds Release into `<repo>\dist` and installs the DLL into
your ATAS Indicators folder:

```bat
build.cmd
build.cmd "D:\my-deploy-folder"
build.cmd "D:\my-deploy-folder" "C:\Program Files (x86)\ATAS Platform"
build.cmd "" "" nocopy          REM build only, leave ATAS untouched
```

`build.cmd` also installs the DLL into the ATAS **Indicators** folder for you and prints
which one it used. Restart ATAS afterwards, then add **ICT/SMC Strategy** from the
*Order Flow* category.

> **Where the Indicators folder actually is.** Modern ATAS loads custom indicators from
> `%APPDATA%\ATAS\Indicators` (i.e. `C:\Users\<you>\AppData\Roaming\ATAS\Indicators`),
> **not** from `Documents`. Some older layouts used `%USERPROFILE%\Documents\ATAS\Indicators`.
> `build.cmd` prefers the Roaming path and falls back to Documents only if that is the one
> that already exists. If you copy the DLL by hand and the indicator never appears, you
> almost certainly used the wrong one of these two.
>
> The **journal** output is unrelated to this and goes to
> `%USERPROFILE%\Documents\ATAS\ICTSMC-Journal` (configurable in settings → *11. Journal*).

Manual equivalents:

```bash
# Standard ATAS (default — probed automatically, framework read from the install)
dotnet build src/ICTSMCStrategy/ICTSMCStrategy.csproj -c Release

# explicit path
dotnet build src/ICTSMCStrategy/ICTSMCStrategy.csproj -c Release -p:AtasPath="C:\Program Files (x86)\ATAS Platform"

# ATAS X, if that is what you actually run
dotnet build src/ICTSMCStrategy/ICTSMCStrategy.csproj -c Release -p:AtasPath="C:\Program Files\ATAS X"

# older .NET 8-based install
dotnet build src/ICTSMCStrategy/ICTSMCStrategy.csproj -c Release -p:AtasTfm=net8.0-windows -p:AtasPath="C:\Program Files (x86)\ATAS Platform"
```

> **Build against the platform you actually run.** Standard ATAS ("ATAS Platform") is
> probed first and wins whenever it and ATAS X are installed side by side; ATAS X is only
> a fallback for machines that have nothing else. This is not cosmetic — the two ship
> *different assembly versions* of the same assemblies (e.g. `ATAS.Indicators`
> 8.0.14.397 vs 8.0.14.647), so a DLL linked against one records references the other
> does not provide, and the indicator can fail to load. Each candidate folder is probed
> for `ATAS.Indicators.dll` rather than for the folder alone, so a leftover install
> directory can no longer win the probe and then fail the build.
>
> You can confirm what your build actually linked against:
>
> ```powershell
> [Reflection.Assembly]::LoadFrom("$env:APPDATA\ATAS\Indicators\ICTSMCStrategy.dll").GetReferencedAssemblies() |
>   Where-Object Name -match 'ATAS|OFT' | Select-Object Name, Version
> ```

Copy `src/ICTSMCStrategy/bin/Release/ICTSMCStrategy.dll` into
the ATAS Indicators folder (see the note above — normally
`%APPDATA%\ATAS\Indicators`), restart ATAS, and add
**“ICT/SMC Strategy”** (Order Flow category) to the chart.

> Upgrading from a build older than the rename? Delete the old `IctSmcZones.dll`
> from the Indicators folder first — leaving both DLLs in place makes ATAS load
> the same indicator twice (duplicate list entries, ambiguous chart bindings).
> Charts configured against the old assembly need the indicator re-added once.

> The project references `ATAS.Indicators.dll` and `OFT.Rendering.dll` from your ATAS
> installation. ATAS occasionally moves types between versions — if the compiler
> complains about a member (e.g. an `AddAlert` overload), adjust to the signature your
> ATAS version exposes; the code keeps the API surface deliberately small.

## Chart history depth — the one thing the indicator cannot do for you

Everything else self-configures from the data. This cannot: HTF candles are aggregated from
the chart bars **ATAS has loaded**, so a chart set to a short history silently produces a
thin top layer that still feeds the confluence tier and the bias filter. It does not warn,
because from the engine's point of view the data simply isn't there.

A layer needs roughly 40 of its own candles before its ATR is seeded, its fractal swings
confirm and its noise floor means anything. The Daily layer is the binding constraint on
both intraday setups:

| Chart | Layers | Bars per Daily candle | Minimum bars | Set history to |
|---|---|---|---|---|
| 5m | 1H + 4H + D | 288 | ~11,500 | **60 days** |
| 15m | 4H + D | 96 | ~3,850 | **60 days** |

Both land on the same ~42 trading sessions because the Daily layer sets the requirement in
each case — only the bar count differs. Set it once per chart in ATAS's own history settings;
60 calendar days gives headroom over the ~42-session floor.

## Key settings

- **General** — swing (fractal) period, ATR period, max zones, mitigated-zone retention
- **FVG / OB** — min gap size (ticks + ATR fraction), displacement filter (ATR ×),
  zone style (body vs full range), mitigation rule per type:
  `AnyTouch`, `Midline` (50%), `FullFill`, `BodyClose`
- **Liquidity** — equal-level tolerance in ticks, max levels per side
- **Market structure** — swing period, protected swings, and `RequireDisplacementForMss`
  (on): a reversal break must displace or it is recorded as a BoS
- **HTF** — Auto/Manual selection, optional second HTF layer, daily session anchor
  (e.g. 1080 = 18:00 platform time for futures), **weekly weekday anchor**
  (`WeeklyAnchorMode`, Auto → Sunday for session instruments), HTF FVG/OB toggles,
  per-layer zone cap, info badge. In Auto mode the chart TF is estimated from
  the mode of bar-open time deltas (robust to session gaps); on tick/volume/range
  charts the median bar duration is rounded up to a standard TF — verify via the badge
- **Entry model** — require sweep, sweep→MSS and MSS→entry windows,
  premium/discount filter + tolerance, opposite-MSS cancellation, failed-MSS trap
  arming (IFVG logic), SL buffer ticks, **HTF bias filter** (off) and
  **min/max risk bounds** (off). Every one of these leaves the setup armed when it
  vetoes, and writes an `EntryRejected` row explaining why
- **Palette** — every zone family has its own hue so the chart reads at a glance:
  OB green/red, FVG blue/orange, IFVG teal/purple, breakers green/red, HTF zones rendered as gold 2px frames; EQH/EQL pools and PDH/PDL/PWH/PWL draw with a heavier stroke; labels sit on a translucent backdrop pill and auto-hide when a zone is too small,
  so zooming out never leaves orphaned text

## Journal / audit pipeline

With journaling on (default), every session writes CSVs to
`Documents\ATAS\ICTSMC-Journal\<instrument>\` (new file set per recalculation —
no duplicate rows). File names are `<yyyyMMdd-HHmmss>-<id>-*.csv`, where `<id>`
is a short per-chart-instance suffix so two charts on the same instrument can
never collide into one file even when they recalculate in the same second:

- `*-events.csv` — zone created/touched/mitigated/inverted, sweeps, BoS/MSS, failed MSS,
  **plus the full decision log**: `Armed` (source, sweep age, window), `ArmRejected`
  (exact reason a MSS failed to arm), `ArmExpired` (bars waited), `EntryRejected`
  (PD filter veto with zone mid vs EQ±tolerance and the exact excess), `SweepExpired` —
  every signal that did NOT fire is explained with numbers, zero ambiguity
- `*-signals.csv` — every entry signal with tier, arm source (Sweep / TrapArm /
  Sweep+Trap), trigger zone, entry/SL/TP2/TP3, PD status, confluence stack, the
  **HTF bias stack** at the moment of the signal (`4H↑ D↓`), and a `Sequenced` column
  reading `intrabar` for live signals or `bar-conservative` for replayed ones, whose
  signal bar is resolved stop-first because intrabar ordering is unknowable in history
- `*-outcomes.csv` — resolution per signal (SL / TP2 / TP3 / Timeout, conservative
  SL-first on ambiguous bars), R-multiple, **MAE/MFE in R**, bars held, plus two
  **shadow trade-management results** simulated in parallel for every signal
  (columns `BE1R_R` / `Partial2R_R`): what the trade would have made with the stop
  moved to breakeven at +1R, and with half banked at +2R (stop to entry on the rest,
  runner to TP3)
- `*-analytics.csv` — win-rate & expectancy grouped by zone family (OB/FVG/iFVG),
  layer (LTF/4H/D…), arm source, tier, direction — answers "does TrapArm beat Sweep?"
  and "which zones hit best?" directly; `AvgBE1R_R` / `AvgPartial2R_R` sit next to
  `AvgR` in every row so the three management styles are directly comparable per group

**LIVE rows only by default** (`Journal LIVE rows only`, on): the history replay is
journal-silent, so files stay lean and every row is something that actually happened
while the chart was live. Flip the toggle off for one session to regenerate the full
`HIST` backfill — a deterministic backtest of the exact same code path the live
signals use; nothing is ever lost because replay always rebuilds it from the candles.

## Repository layout

```
src/ICTSMCStrategy/
  ICTSMCStrategy.csproj            project (references ATAS DLLs)
  Models.cs                     zone / liquidity / structure / HTF models
  ICTSMCStrategy.cs             settings + lifecycle + state
  ICTSMCStrategy.Detection.cs      bar-close engine: swings, BoS/MSS, FVG, OB, HTF
  ICTSMCStrategy.Intrabar.cs       tick engine: touches, sweeps, entry model, alerts, Telegram
  ICTSMCStrategy.Rendering.cs      custom drawing: zones, liquidity, structure, premium/discount
  ICTSMCStrategy.Journal.cs        audit pipeline: events, signals, outcomes, analytics CSVs
  ICTSMCStrategy.TelegramRemote.cs /shot command hub + self-rendered chart snapshots
docs/STRATEGY.md                how each book concept maps to code
```

## Disclaimer

Educational tooling only. Trading involves risk; nothing here is financial advice.
Follow the book’s risk rules: ≤1–2% risk per trade, RRR ≥ 2:1, daily loss limit, no
revenge trades.
