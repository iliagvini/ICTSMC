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
| **Order Blocks** (Ch. 5) | Last opposite-colored candle before a displacement that **breaks structure**; body-only (open↔close, as taught) or full-range zones |
| **BoS / MSS** (Ch. 6) | Fractal swings → close-through breaks; continuation = BoS (dashed), reversal = MSS/CHoCH (solid, stronger) |
| **Entry model** (Ch. 7) | State machine: *liquidity sweep → MSS → price returns to aligned FVG/OB in the correct half of the range* → entry alert with SL + 2R/3R targets |
| **Premium / Discount, Power of 3** (Ch. 8) | Equilibrium (50%) of the current dealing range, subtle premium/discount shading; entry model only buys in discount / sells in premium |
| **Higher-timeframe framework** | **Auto mode (default):** the indicator *measures* the chart timeframe from the data itself and picks the institutional ladder (1m→15m+1H, 5m→1H+4H, 15m–1H→4H+D, 4H→D+W); HTF FVGs and OBs are mapped onto your chart with stronger styling. An on-chart badge shows exactly what was detected/chosen. Manual mode with fixed minutes is still available |

## Alerts

All alerts fire **only in realtime** (never while history is replayed) and are de-duplicated
per zone/level/event:

- 🎯 **Zone touch** — instant, on the first tick into an FVG/OB (no close needed)
- 💧 **Liquidity taken** — BSL/SSL crossed, with the follow-up hint (watch for MSS)
- 📐 **BoS / MSS** — structure events with direction
- 🟢/🔴 **Entry model** — sweep + MSS + return to zone, tiered by confluence
  (🟢🟢🟢 A++ with Daily/Weekly zone, 🟢🟢 A+ with any HTF zone, 🟢 B standalone),
  with zone stack, PD status (Discount/Near EQ/Premium), entry, SL and 2R/3R targets
- ⚠️ **Failed MSS** — an armed setup structurally invalidated by an opposite MSS
  (often the seed of the reverse trade)
- 📦 **Zone created** (off by default)

### Telegram setup

1. Create a bot with [@BotFather](https://t.me/BotFather) → copy the **bot token**.
2. Message your bot once, then open `https://api.telegram.org/bot<TOKEN>/getUpdates`
   and copy your **chat id** (for a group, add the bot to the group and use the negative id).
3. In the indicator settings → *10. Telegram*: enable, paste token + chat id.

Messages are sent fire-and-forget on a background thread — network issues can never
freeze the chart.

## Build & install

Requirements: .NET 8 SDK, ATAS platform installed.

```bash
dotnet build src/IctSmcZones/IctSmcZones.csproj -c Release -p:AtasPath="C:\Program Files (x86)\ATAS Platform"
```

Copy `src/IctSmcZones/bin/Release/IctSmcZones.dll` into
`%USERPROFILE%\Documents\ATAS\Indicators`, restart ATAS, and add
**“ICT/SMC Zones + Telegram”** (Order Flow category) to the chart.

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
  premium/discount filter, SL buffer ticks

## Repository layout

```
src/IctSmcZones/
  IctSmcZones.csproj            project (references ATAS DLLs)
  Models.cs                     zone / liquidity / structure / HTF models
  IctSmcZones.cs                settings + lifecycle + state
  IctSmcZones.Detection.cs      bar-close engine: swings, BoS/MSS, FVG, OB, HTF
  IctSmcZones.Intrabar.cs       tick engine: touches, sweeps, entry model, alerts, Telegram
  IctSmcZones.Rendering.cs      custom drawing: zones, liquidity, structure, premium/discount
docs/STRATEGY.md                how each book concept maps to code
```

## Disclaimer

Educational tooling only. Trading involves risk; nothing here is financial advice.
Follow the book’s risk rules: ≤1–2% risk per trade, RRR ≥ 2:1, daily loss limit, no
revenge trades.
