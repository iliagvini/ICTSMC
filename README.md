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
| **BoS / MSS** (Ch. 6) | Fractal swings → close-through breaks; continuation = BoS (dashed), reversal = MSS/CHoCH (solid, stronger) |
| **Entry model** (Ch. 7) | State machine: *liquidity sweep → MSS → price returns to aligned FVG/OB in the correct half of the range* → entry alert with SL + 2R/3R targets |
| **Premium / Discount, Power of 3** (Ch. 8) | Equilibrium (50%) of the current dealing range, subtle premium/discount shading; entry model only buys in discount / sells in premium |
| **Higher-timeframe framework** | **Auto mode (default):** the indicator *measures* the chart timeframe from the data itself and picks the institutional ladder (1m→15m+1H, 5m→1H+4H, 15m–1H→4H+D, 4H→D+W); HTF FVGs and OBs are mapped onto your chart with stronger styling. An on-chart badge shows exactly what was detected/chosen. Manual mode with fixed minutes is still available |

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

Requirements: .NET 10 SDK (ATAS X) or .NET 8 SDK (older ATAS Platform), ATAS installed.

```bash
# ATAS X (default — targets net10.0-windows, probes "C:\Program Files\ATAS X" automatically)
dotnet build src/ICTSMCStrategy/ICTSMCStrategy.csproj -c Release

# explicit path
dotnet build src/ICTSMCStrategy/ICTSMCStrategy.csproj -c Release -p:AtasPath="C:\Program Files\ATAS X"

# older .NET 8-based ATAS Platform
dotnet build src/ICTSMCStrategy/ICTSMCStrategy.csproj -c Release -p:AtasTfm=net8.0-windows -p:AtasPath="C:\Program Files (x86)\ATAS Platform"
```

Copy `src/ICTSMCStrategy/bin/Release/IctSmcZones.dll` into
`%USERPROFILE%\Documents\ATAS\Indicators`, restart ATAS, and add
**“ICT/SMC Strategy”** (Order Flow category) to the chart.

> The output DLL keeps its original `IctSmcZones` name on purpose: ATAS keys each
> chart's saved indicator settings and templates to the compiled assembly/type
> identity, so renaming the binary would orphan every configured chart.

> The project references `ATAS.Indicators.dll` and `OFT.Rendering.dll` from your ATAS
> installation. ATAS occasionally moves types between versions — if the compiler
> complains about a member (e.g. an `AddAlert` overload), adjust to the signature your
> ATAS version exposes; the code keeps the API surface deliberately small.

## Key settings

- **General** — swing (fractal) period, ATR period, max zones, mitigated-zone retention
- **FVG / OB** — min gap size (ticks + ATR fraction), displacement filter (ATR ×),
  zone style (body vs full range), mitigation rule per type:
  `AnyTouch`, `Midline` (50%), `FullFill`, `BodyClose`
- **Liquidity** — equal-level tolerance in ticks, max levels per side
- **HTF** — Auto/Manual selection, optional second HTF layer, daily session anchor
  (e.g. 1080 = 18:00 platform time for futures), HTF FVG/OB toggles, displacement
  factor, per-layer zone cap, info badge. In Auto mode the chart TF is estimated from
  the mode of bar-open time deltas (robust to session gaps); on tick/volume/range
  charts the median bar duration is rounded up to a standard TF — verify via the badge
- **Entry model** — require sweep, sweep→MSS and MSS→entry windows,
  premium/discount filter + tolerance, opposite-MSS cancellation, failed-MSS trap
  arming (IFVG logic), SL buffer ticks
- **Palette** — every zone family has its own hue so the chart reads at a glance:
  OB green/red, FVG blue/orange, IFVG teal/purple, HTF zones rendered as gold frames (same 1px weight as LTF borders); labels auto-hide when a zone is too small,
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
  Sweep+Trap), trigger zone, entry/SL/TP2/TP3, PD status, confluence stack
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
