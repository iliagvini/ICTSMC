using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ICTSMC
{
    /// <summary>
    /// Journaling / audit pipeline.
    ///
    /// Everything the engine does is written to CSV files so the live system can be
    /// audited after the fact:
    ///  • events.csv    — zone lifecycle (created / touched / mitigated / inverted),
    ///                    liquidity sweeps, BoS/MSS, failed MSS
    ///  • signals.csv   — every entry-model signal with its full trade plan
    ///  • outcomes.csv  — resolution of each signal: SL / TP2 / TP3 / Timeout,
    ///                    R-multiple, MAE/MFE in R, bars held, plus two shadow
    ///                    trade-management results simulated in parallel (never
    ///                    traded): BE-at-+1R and partial-at-+2R
    ///  • analytics.csv — aggregated win-rate / expectancy per zone family,
    ///                    layer (LTF/4H/D…), arm source (Sweep vs TrapArm) and tier
    ///
    /// Files are per session (a new set on every full recalculation). By default only
    /// LIVE rows are written (JournalLiveOnly) — the history replay is journal-silent,
    /// keeping files lean. Switching the toggle off regenerates the full HIST backfill
    /// (a deterministic backtest of the exact same code path the live signals use) on
    /// the next recalculation. All IO is buffered and flushed off the chart thread; an
    /// IO failure can never touch trading logic.
    /// </summary>
    public partial class ICTSMCStrategy
    {
        private const string GrpJournal = "11. Journal";

        #region Settings

        [Display(GroupName = GrpJournal, Name = "Enable journaling", Order = 1100)]
        public bool JournalEnabled { get; set; } = true;

        [Display(GroupName = GrpJournal, Name = "Journal folder (empty = Documents\\ATAS\\ICTSMC-Journal)", Order = 1110)]
        public string JournalPath { get; set; } = "";

        // LIVE-only keeps the files lean (a handful of rows per session instead of
        // hundreds of replay rows). Switch off for a session to regenerate the full
        // deterministic HIST backtest on demand — replay always rebuilds it.
        [Display(GroupName = GrpJournal, Name = "Journal LIVE rows only (no historical backfill)", Order = 1115)]
        public bool JournalLiveOnly { get; set; } = true;

        [Display(GroupName = GrpJournal, Name = "Signal timeout (bars)", Order = 1120)]
        [Range(10, 2000)]
        public int SignalTimeoutBars { get; set; } = 100;

        #endregion

        #region State

        private readonly object _journalLock = new();
        private readonly Dictionary<string, List<string>> _journalPending = new();
        private readonly HashSet<string> _journalHeaderWritten = new();

        // All file IO is chained into a strict FIFO queue: rows land on disk in the
        // exact order they were journaled, and no two writes ever touch a file
        // concurrently (unordered Task.Run appends caused out-of-order rows and, on
        // IO contention, silently dropped batches — unacceptable for an audit trail).
        private Task _ioChain = Task.CompletedTask;
        private readonly object _ioChainLock = new();

        private readonly List<SignalRecord> _openSignals = new();
        private readonly List<SignalRecord> _resolvedSignals = new();

        private string _sessionStamp = "";
        // Stable per-chart-instance suffix baked into the session stamp: two charts
        // on the same instrument that recalculate in the same second would otherwise
        // collide on identical yyyyMMdd-HHmmss filenames and interleave their rows
        // (duplicate headers + colliding signal ids — observed in the field).
        private string _journalInstanceId;
        private int _nextZoneId;
        private int _nextSignalId;

        private const string EventsHeader =
            "Time,Mode,Instrument,Event,Direction,ZoneId,ZoneTag,Layer,Top,Bottom,Price,Extra";
        private const string SignalsHeader =
            "SignalId,Time,Mode,Instrument,Direction,Tier,ArmSource,TriggerTag,Layer,ZoneTop,ZoneBottom,PlannedEntry,FillPrice,SL,TP2,TP3,FillStatus,DataQuality,FillBar,FillSequence,ExitPlan,PdStatus,Confluence";
        private const string OutcomesHeader =
            "SignalId,ResolvedTime,Mode,Outcome,ExitPrice,RealizedR,MAE_R,MFE_R,BarsHeld,Direction,Tier,ArmSource,TriggerTag,Layer,FillStatus,DataQuality,ExitPlan";

        #endregion

        #region Session / plumbing

        private void InitJournalSession()
        {
            lock (_journalLock)
            {
                _journalPending.Clear();
                _journalHeaderWritten.Clear();
            }

            _openSignals.Clear();
            _resolvedSignals.Clear();
            _journalInstanceId ??= Guid.NewGuid().ToString("N")[..4];
            _sessionStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                            + "-" + _journalInstanceId;
            _nextZoneId = 0;
            _nextSignalId = 0;
        }

        private string JournalDir
        {
            get
            {
                var dir = string.IsNullOrWhiteSpace(JournalPath)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ATAS", "ICTSMC-Journal")
                    : JournalPath.Trim();

                var instrument = InstrumentInfo?.Instrument ?? "chart";
                foreach (var c in Path.GetInvalidFileNameChars())
                    instrument = instrument.Replace(c, '_');

                return Path.Combine(dir, instrument);
            }
        }

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private string Num(decimal value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

        private void JournalWrite(string file, string header, string line)
        {
            if (!JournalEnabled)
                return;

            // LIVE-only mode: backfill rows never even enqueue — zero replay IO.
            if (JournalLiveOnly && !_realtime)
                return;

            List<string> toFlush = null;
            string path = null;

            lock (_journalLock)
            {
                if (!_journalPending.TryGetValue(file, out var pending))
                {
                    pending = new List<string>();
                    _journalPending[file] = pending;
                }

                if (!_journalHeaderWritten.Contains(file))
                {
                    pending.Add(header);
                    _journalHeaderWritten.Add(file);
                }

                pending.Add(line);

                // Live rows land on disk immediately; historical backfill batches.
                if (_realtime || pending.Count >= 200)
                {
                    toFlush = new List<string>(pending);
                    pending.Clear();
                    path = Path.Combine(JournalDir, $"{_sessionStamp}-{file}");
                }
            }

            if (toFlush != null)
                FlushAsync(path, toFlush);
        }

        private void FlushAsync(string path, List<string> lines)
        {
            EnqueueIo(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllLines(path, lines);
            });
        }

        private void EnqueueIo(Action work)
        {
            lock (_ioChainLock)
            {
                _ioChain = _ioChain.ContinueWith(_ =>
                {
                    try
                    {
                        work();
                    }
                    catch
                    {
                        // Journaling must never disturb the chart thread or trading logic.
                    }
                }, TaskScheduler.Default);
            }
        }

        /// <summary>Flush any batched historical rows (called when a bar completes).</summary>
        private void FlushJournalBuffers()
        {
            List<(string Path, List<string> Lines)> work = null;

            lock (_journalLock)
            {
                foreach (var kv in _journalPending.Where(kv => kv.Value.Count > 0))
                {
                    work ??= new List<(string, List<string>)>();
                    work.Add((Path.Combine(JournalDir, $"{_sessionStamp}-{kv.Key}"), new List<string>(kv.Value)));
                    kv.Value.Clear();
                }
            }

            if (work == null)
                return;

            foreach (var (path, lines) in work)
                FlushAsync(path, lines);
        }

        #endregion

        #region Event logging

        private DateTime BarTime(int bar) => GetCandle(Math.Max(0, Math.Min(bar, CurrentBar - 1))).Time;

        private void JournalEvent(int bar, string evt, string direction, Zone zone, decimal price, string extra)
        {
            if (!JournalEnabled)
                return;

            var line = string.Join(",",
                BarTime(bar).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                _realtime ? "LIVE" : "HIST",
                Csv(InstrumentInfo?.Instrument ?? ""),
                evt,
                direction,
                zone?.Id.ToString(CultureInfo.InvariantCulture) ?? "",
                Csv(zone?.Tag ?? ""),
                Csv(zone == null ? "" : zone.IsHtf ? zone.HtfLabel : "LTF"),
                zone == null ? "" : Num(zone.Top),
                zone == null ? "" : Num(zone.Bottom),
                price == 0m ? "" : Num(price),
                Csv(extra));

            JournalWrite("events.csv", EventsHeader, line);
        }

        #endregion

        #region Signal tracking (MAE/MFE + outcomes)

        private void JournalSignal(SignalRecord record)
        {
            // A setup, an OHLC-only candidate, and an executable fill are distinct
            // audit objects. Only an ordered, verified fill may enter outcome
            // tracking or headline performance statistics.
            if (record.FillStatus == SignalFillStatus.Filled && !record.Resolved)
                _openSignals.Add(record);

            if (!JournalEnabled)
                return;

            var line = string.Join(",",
                record.Id.ToString(CultureInfo.InvariantCulture),
                record.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                record.Live ? "LIVE" : "HIST",
                Csv(InstrumentInfo?.Instrument ?? ""),
                record.Long ? "Long" : "Short",
                record.Tier,
                record.ArmSource,
                Csv(record.TriggerTag),
                Csv(record.Layer),
                Num(record.ZoneTop),
                Num(record.ZoneBottom),
                Num(record.PlannedEntry),
                Num(record.Entry),
                Num(record.Sl),
                Num(record.Tp2),
                Num(record.Tp3),
                record.FillStatus.ToString(),
                record.DataQuality.ToString(),
                record.FillBar.ToString(CultureInfo.InvariantCulture),
                record.FillSequence.ToString(CultureInfo.InvariantCulture),
                record.ExitPlan.ToString(),
                record.PdStatus,
                Csv(record.Confluence));

            JournalWrite("signals.csv", SignalsHeader, line);
        }

        /// <summary>
        /// Bar-close maintenance is intentionally timeout-only. Price-path outcome
        /// handling lives in the ordered-observation V2 execution pipeline; using
        /// OHLC extremes here would overwrite a known tick path with an invented one.
        /// </summary>
        private void UpdateOpenSignals(int bar)
        {
            if (_openSignals.Count == 0)
                return;

            var candle = GetCandle(bar);
            foreach (var s in _openSignals.ToList())
            {
                if (s.Resolved || s.FillStatus != SignalFillStatus.Filled || bar - s.FillBar < SignalTimeoutBars)
                    continue;

                var closeR = s.Risk > 0m
                    ? (s.Long ? candle.Close - s.Entry : s.Entry - candle.Close) / s.Risk
                    : 0m;
                var realized = s.ExitPlan == ExitPlan.PartialAtTp2RunnerToTp3 && s.PartialTaken
                    ? 1m + 0.5m * closeR
                    : closeR;
                var outcome = s.ExitPlan == ExitPlan.PartialAtTp2RunnerToTp3 && s.PartialTaken
                    ? "TimeoutAfterTP2"
                    : "Timeout";

                ResolveFilledSignal(s, bar, outcome, candle.Close, realized);
            }
        }

        /// <summary>
        /// Bar-by-bar simulation of the two virtual management styles, run BEFORE the
        /// raw resolution checks each bar. Same conservative rule as the raw engine:
        /// whenever a bar touches both the (virtual) stop and a target, the stop is
        /// assumed to have been hit first — including the very bar a trigger level is
        /// reached. Exact definitions:
        ///  • BE-at-+1R  — once the bar range reaches entry + 1R (long; mirrored for
        ///    shorts) the virtual stop moves to entry. From that bar on (inclusive),
        ///    a return to entry exits at 0R; otherwise TP3 = +3R, TP2 latch resolves
        ///    +2R at timeout, timeout without TP2 = close-based R. If +1R is never
        ///    reached the shadow result equals the raw outcome.
        ///  • Partial-at-+2R — when the bar range reaches entry + 2R, half the
        ///    position is banked (0.5 × 2R = +1R locked) and the remaining half runs
        ///    with its stop at entry. Remainder: return to entry → total +1R;
        ///    TP3 → +1R + 0.5 × 3R = +2.5R; timeout → +1R + 0.5 × close-based R.
        ///    If +2R is never reached the shadow result equals the raw outcome.
        /// </summary>
        private static void UpdateShadowManagement(SignalRecord s, decimal high, decimal low, bool slHit, bool tp2Hit, bool tp3Hit)
        {
            var risk = s.Risk;
            if (risk <= 0)
                return; // degenerate; Resolve() falls back to the raw R for both shadows

            var be1Hit = s.Long ? high >= s.Entry + risk : low <= s.Entry - risk;
            var backToEntry = s.Long ? low <= s.Entry : high >= s.Entry;

            // ---- BE-at-+1R shadow ----
            if (!s.BeDone)
            {
                if (!s.BeArmed && slHit)
                {
                    s.BeDone = true;
                    s.BeR = -1m; // stop-first: raw SL before the +1R trigger
                }
                else
                {
                    if (!s.BeArmed && be1Hit)
                        s.BeArmed = true;

                    if (s.BeArmed)
                    {
                        if (backToEntry)
                        {
                            s.BeDone = true;
                            s.BeR = 0m; // virtual stop at entry hit
                        }
                        else if (tp3Hit)
                        {
                            s.BeDone = true;
                            s.BeR = 3m;
                        }
                        else if (tp2Hit)
                        {
                            s.BeTp2 = true; // resolves +2R at timeout, like the raw engine
                        }
                    }
                }
            }

            // ---- Partial-at-+2R shadow ----
            if (!s.PartialDone)
            {
                if (!s.PartialTaken && slHit)
                {
                    s.PartialDone = true;
                    s.PartialR = -1m; // stop-first: full position stopped before +2R
                }
                else
                {
                    if (!s.PartialTaken && tp2Hit)
                        s.PartialTaken = true; // +1R banked, remainder stop at entry

                    if (s.PartialTaken)
                    {
                        if (backToEntry)
                        {
                            s.PartialDone = true;
                            s.PartialR = 1m; // banked half only; remainder out at entry
                        }
                        else if (tp3Hit)
                        {
                            s.PartialDone = true;
                            s.PartialR = 2.5m; // +1R banked + 0.5 × 3R runner
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Early-exit radar, run each completed bar AFTER UpdateOpenSignals (so a
        /// signal resolved this bar never warns). Two deterministic threat
        /// classes, each warning AT MOST ONCE per signal:
        ///  1. opposing structure — a BoS/MSS printed against the trade this bar;
        ///  2. opposing pattern  — displacement against the position just carved a
        ///     fresh opposing zone (StartBar within the last bar).
        /// Warnings are journaled (HIST and LIVE) so their predictive value is
        /// measurable later; the 🚨 alert itself is realtime-gated as usual.
        /// </summary>
        private void CheckOpenSignalThreats(int bar)
        {
            if (!AlertOnExitWarning || _openSignals.Count == 0)
                return;

            var candle = GetCandle(bar);

            foreach (var s in _openSignals)
            {
                if (s.Resolved || bar <= s.SignalBar)
                    continue;

                if (!s.WarnedStructure)
                {
                    var evt = _structure.LastOrDefault(e => e.Bar == bar && e.Bullish != s.Long);
                    if (evt != null)
                    {
                        s.WarnedStructure = true;
                        EmitExitWarning(s, bar, candle.Close,
                            $"{(evt.IsMss ? "MSS" : "BoS")} printed against the trade @ {FormatPrice(evt.Level)}" +
                            (evt.IsMss ? " — structure shifted" : " — opposing momentum confirmed"));
                    }
                }

                if (!s.WarnedZone)
                {
                    var opp = _zones.LastOrDefault(z =>
                        z.State != ZoneState.Mitigated &&
                        z.StartBar >= bar - 1 &&
                        z.Id != s.TriggerZoneId &&
                        z.IsBullish != s.Long);
                    if (opp != null)
                    {
                        s.WarnedZone = true;
                        EmitExitWarning(s, bar, candle.Close,
                            $"fresh opposing {opp.Tag} carved at {FormatPrice(opp.Bottom)}–{FormatPrice(opp.Top)}");
                    }
                }

            }
        }

        private void EmitExitWarning(SignalRecord s, int bar, decimal close, string reason)
        {
            var unrealized = s.Risk > 0
                ? (s.Long ? close - s.Entry : s.Entry - close) / s.Risk
                : 0m;
            var rTxt = unrealized.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + "R";

            JournalEvent(bar, "ExitWarning", s.Long ? "Bull" : "Bear", null, close,
                $"signal #{s.Id} ({s.Tier} {s.ArmSource} {s.TriggerTag}); entry {Num(s.Entry)}; unrealized {rTxt}; {reason}");

            Fire($"🚨 EXIT WARNING — open {(s.Long ? "LONG" : "SHORT")} (signal #{s.Id}, {s.Tier}) under threat\n" +
                 $"📍 Entry {FormatPrice(s.Entry)} · now {FormatPrice(close)} ({rTxt})\n" +
                 $"⚠️ Reason: {reason}\n" +
                 "👋 Consider exiting, tightening the stop, or stepping aside");
        }

        private void ResolveFilledSignal(SignalRecord s, int bar, string outcome, decimal exit, decimal realizedR)
        {
            if (s.Resolved || s.FillStatus != SignalFillStatus.Filled)
                return;

            s.Resolved = true;
            s.Outcome = outcome;
            s.Exit = exit;
            s.ResolvedBar = bar;
            s.RealizedR = realizedR;

            var risk = s.Risk;
            s.BeDone = true;
            s.PartialDone = true;
            s.BeR = realizedR;
            s.PartialR = realizedR;

            _openSignals.Remove(s);
            _resolvedSignals.Add(s);

            if (!JournalEnabled)
                return;

            // A backfill-fired signal can resolve during live ticks; its signal row
            // was never journaled, so skip the outcome too — no orphan ids.
            if (JournalLiveOnly && !s.Live)
                return;

            var maeR = risk > 0 ? s.Mae / risk : 0m;
            var mfeR = risk > 0 ? s.Mfe / risk : 0m;

            var line = string.Join(",",
                s.Id.ToString(CultureInfo.InvariantCulture),
                BarTime(bar).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                s.Live ? "LIVE" : "HIST",
                outcome,
                Num(exit),
                Num(realizedR),
                Num(maeR),
                Num(mfeR),
                (bar - s.FillBar).ToString(CultureInfo.InvariantCulture),
                s.Long ? "Long" : "Short",
                s.Tier,
                s.ArmSource,
                Csv(s.TriggerTag),
                Csv(s.Layer),
                s.FillStatus.ToString(),
                s.DataQuality.ToString(),
                s.ExitPlan.ToString());

            JournalWrite("outcomes.csv", OutcomesHeader, line);
            WriteAnalytics();
        }

        // Compatibility shim for legacy helpers that remain compiled but are no
        // longer reachable from the V2 execution pipeline.
        private void Resolve(SignalRecord s, int bar, string outcome, decimal exit, decimal rMultiple) =>
            ResolveFilledSignal(s, bar, outcome, exit, rMultiple);

        #endregion

        #region Analytics

        /// <summary>
        /// Rewrites the aggregated performance file after every resolution.
        /// Win rate = TP2+ resolutions ÷ (wins + SL losses); timeouts are excluded
        /// from win rate but included in expectancy (AvgR).
        /// </summary>
        private void WriteAnalytics()
        {
            if (!JournalEnabled)
                return;

            // Analytics must mirror what outcomes.csv contains: in LIVE-only mode
            // that means live-fired signals exclusively.
            var pool = (JournalLiveOnly
                    ? _resolvedSignals.Where(s => s.Live)
                    : _resolvedSignals)
                .Where(s => s.IsAnalyticsEligible)
                .ToList();

            if (pool.Count == 0)
                return;

            var sb = new StringBuilder();
            sb.AppendLine("GroupBy,Key,Signals,Wins,Losses,Timeouts,WinRatePct,AvgR,AvgBE1R_R,AvgPartial2R_R,AvgMAE_R,AvgMFE_R");

            var strict = pool.Where(s => s.Tier != "C").ToList();
            var experimental = pool.Where(s => s.Tier == "C").ToList();

            AppendGroup(sb, strict, "Strict.ZoneFamily", s => s.ZoneFamily);
            AppendGroup(sb, strict, "Strict.Layer", s => s.Layer);
            AppendGroup(sb, strict, "Strict.ArmSource", s => s.ArmSource);
            AppendGroup(sb, strict, "Strict.Tier", s => s.Tier);
            AppendGroup(sb, strict, "Strict.Direction", s => s.Long ? "Long" : "Short");
            AppendGroup(sb, strict, "Strict.ALL", _ => "ALL");
            AppendGroup(sb, experimental, "Experimental.ZoneFamily", s => s.ZoneFamily);
            AppendGroup(sb, experimental, "Experimental.ArmSource", s => s.ArmSource);
            AppendGroup(sb, experimental, "Experimental.ALL", _ => "ALL");

            var path = Path.Combine(JournalDir, $"{_sessionStamp}-analytics.csv");
            var text = sb.ToString();

            EnqueueIo(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text);
            });
        }

        private void AppendGroup(StringBuilder sb, List<SignalRecord> pool, string groupName, Func<SignalRecord, string> selector)
        {
            if (pool.Count == 0)
                return;

            foreach (var group in pool.GroupBy(selector).OrderBy(g => g.Key))
            {
                var total = group.Count();
                var wins = group.Count(s => s.RealizedR > 0m);
                var losses = group.Count(s => s.RealizedR < 0m);
                var timeouts = group.Count(s => s.Outcome.StartsWith("Timeout", StringComparison.Ordinal));
                var decisive = wins + losses;
                var winRate = decisive > 0 ? wins * 100m / decisive : 0m;

                var avgR = group.Average(s => s.RealizedR);

                var avgBe = group.Average(s => s.BeR);
                var avgPartial = group.Average(s => s.PartialR);
                var avgMae = group.Average(s => s.Risk > 0 ? s.Mae / s.Risk : 0m);
                var avgMfe = group.Average(s => s.Risk > 0 ? s.Mfe / s.Risk : 0m);

                sb.AppendLine(string.Join(",",
                    groupName,
                    Csv(group.Key),
                    total.ToString(CultureInfo.InvariantCulture),
                    wins.ToString(CultureInfo.InvariantCulture),
                    losses.ToString(CultureInfo.InvariantCulture),
                    timeouts.ToString(CultureInfo.InvariantCulture),
                    winRate.ToString("0.#", CultureInfo.InvariantCulture),
                    avgR.ToString("0.##", CultureInfo.InvariantCulture),
                    avgBe.ToString("0.##", CultureInfo.InvariantCulture),
                    avgPartial.ToString("0.##", CultureInfo.InvariantCulture),
                    avgMae.ToString("0.##", CultureInfo.InvariantCulture),
                    avgMfe.ToString("0.##", CultureInfo.InvariantCulture)));
            }
        }

        #endregion
    }
}
