# V2 Correctness Audit

## Scope and preservation contract

V2 is a correctness release, not a visual redesign. `ICTSMCStrategy.Rendering.cs`
is unchanged from V1, and `Zone.StartBar` still represents the original source bar
used by the renderer. Zone geometry, colors, labels, midlines, borders, clean-mode
selection, and draw order therefore retain the existing chart presentation.

`ICTSMCStrategy.TelegramRemote.cs` is unchanged from V1. No Telegram credential,
transport, command, or message-delivery behavior was changed in V2.

V2 adds `ConfirmedBar`, `EligibleFromBar`, first-presentation metadata, and a
separate core-entry latch. These fields govern decisions without changing the
renderer's source geometry.

## Strict execution contract

The V2 core setup is:

1. A confirmed swing-liquidity level is crossed.
2. On the completed candle, the event is classified as a confirmed trap, run, or
   indeterminate result. A strict reversal setup can begin only from a confirmed
   trap. A close through the level is a run and cannot arm the opposite reversal.
3. A displacement-qualified external MSS must follow while the trap is valid.
   Internal breaks remain chart context only.
4. The setup snapshots a dealing range and waits for a linked LTF POI confirmed by
   the MSS/displacement leg. HTF zones are confluence by default, not standalone
   execution POIs.
5. The first actual observed price entering that POI from the expected side can
   produce one filled signal. A price jump completely through the range becomes an
   `UnfilledGap`, not an invented limit fill.

The strict defaults require one-tick sweep penetration, one-tick reclaim, an
external dealing range, exact premium/discount alignment, and one core attempt per
zone. The optional C-tier continuation path is disabled by default, explicitly
labeled experimental/non-ICT, and reported separately from strict analytics.

## Important lifecycle rules

- A POI may draw from its origin bar while becoming trade-eligible only after its
  confirmation bar.
- A zone touched before eligibility is visibly retained but permanently disqualified
  from strict first-touch execution.
- First valid contact is evaluated before AnyTouch/Midline/FullFill consumption;
  that same contact then consumes the zone for future core entries.
- `BodyClose` means the candle **close** finishes beyond the far edge. It does not
  mean merely that the candle opened outside the zone.
- IFVG conversion requires a source-timeframe body close and cannot re-invert.

## Historical and live data quality

ATAS provides `OnRecalculate` / `OnFinishRecalculate` lifecycle callbacks but no
direct historical/realtime flag. V2 uses that explicit lifecycle boundary:

- completed historical bars are evaluated as OHLC observations;
- after `OnFinishRecalculate`, the current bar is processed as ordered live price
  observations;
- an OHLC presentation that could not establish entry/exit ordering is recorded as
  `AmbiguousOhlc`, never counted as a filled/scored trade.

This avoids the V1 error where the first live touch could be consumed by an
accumulated OHLC snapshot. It does not turn candle history into tick history. To
validate fills and outcomes, use ATAS live/replay tick data.

## Outcomes and analytics

Only `Filled` records from ordered observations can enter outcome tracking and
headline analytics. The base exit model is explicit:

- `FullAtTp2`
- `FullAtTp3`
- `PartialAtTp2RunnerToTp3` (default: half at 2R, runner to breakeven and 3R)

There is no TP2 latch. A partial TP2 then breakeven produces +1R; a partial TP2 then
TP3 produces +2.5R. Analytics use the recorded `RealizedR`, not an outcome-label
shortcut, and keep experimental C-tier results separate from the strict pool.

## Higher-timeframe controls

Each HTF layer now owns a separate ATR, confirmed swings, trend, and
displacement-qualified structure. An HTF OB requires an HTF structural break;
an HTF FVG uses that layer's ATR. HTF buckets are closed at the opening of the next
chart bar, before that new bar's first execution observation.

Initial HTF construction proceeds chronologically and reconciles historical LTF
contact/mitigation state, so a reload cannot leave a previously presented HTF zone
incorrectly active. Synthetic HTF is disabled by default for irregular or
non-divisible charts. Daily session anchoring no longer shifts weekly bucket starts.

## Automated checks

`src/ICTSMCStrategy.Tests` is a dependency-free .NET 10 regression runner for the
host-independent decision rules. It covers trap versus run classification, expected
zone entry versus gap-through, OHLC intersection, close-based BodyClose behavior,
premium/discount at the actual entry, and OHLC ambiguity.

Run:

```powershell
dotnet build src\ICTSMCStrategy\ICTSMCStrategy.csproj -c Release
dotnet run --project src\ICTSMCStrategy.Tests\ICTSMCStrategy.Tests.csproj -c Release
```

Host integration still requires ATAS replay/manual validation, because the indicator
API, chart rendering, and platform price callbacks cannot be instantiated in a plain
.NET unit test.
