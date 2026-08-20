using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ICTSMC
{
    /// <summary>
    /// Journaling / audit pipeline.
    ///
    /// Everything the engine does is written to CSV files so the live system can be
    /// audited after the fact:
    ///  • events.csv    — zone lifecycle (created / touched / mitigated / inverted /
    ///                    broken), liquidity sweeps, session levels, BoS/MSS, failed MSS
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

        [Display(GroupName = GrpJournal, Name = "Analytics: max resolved signals retained", Order = 1125)]
        [Range(100, 100000)]
        public int AnalyticsMaxSignals { get; set; } = 5000;

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

        // Analytics rewrite coalescing. Recomputing eight groupings over the whole
        // resolved pool on the chart thread after EVERY resolution was O(n) per
        // signal and O(n²) per session; the pool itself was unbounded. Now the
        // chart thread only takes a bounded snapshot, and at most one rewrite is in
        // flight at a time — later resolutions during a burst simply refresh the
        // snapshot that the queued rewrite will pick up.
        private SignalRecord[] _analyticsSnapshot = Array.Empty<SignalRecord>();
        private int _analyticsPending;
        private int _analyticsTrimmed;

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
            "SignalId,Time,Mode,Instrument,Direction,Tier,ArmSource,TriggerTag,Layer,ZoneTop,ZoneBottom,Entry,SL,TP2,TP3,PdStatus,Confluence";
        private const string OutcomesHeader =
            "SignalId,ResolvedTime,Mode,Outcome,ExitPrice,RMultiple,BE1R_R,Partial2R_R,MAE_R,MFE_R,BarsHeld,Direction,Tier,ArmSource,TriggerTag,Layer";

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
            _analyticsSnapshot = Array.Empty<SignalRecord>();
            Interlocked.Exchange(ref _analyticsPending, 0);
            _analyticsTrimmed = 0;
            _kzRejectBullBar = -1;
            _kzRejectBearBar = -1;
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

        /// <summary>
        /// RFC-4180 quoting. Carriage returns matter as much as line feeds: a lone CR
        /// inside a value (Windows text pasted into an instrument alias, for instance)
        /// silently split the row for every downstream CSV reader.
        /// </summary>
        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
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
                Num(record.Entry),
                Num(record.Sl),
                Num(record.Tp2),
                Num(record.Tp3),
                record.PdStatus,
                Csv(record.Confluence));

            JournalWrite("signals.csv", SignalsHeader, line);
        }

        /// <summary>
        /// Runs on every COMPLETED bar: updates MAE/MFE for open signals and resolves
        /// them. Resolution is deliberately conservative: if a bar touches both the
        /// stop and a target, the stop is assumed to have been hit first.
        ///
        /// The SIGNAL BAR is included. Skipping it (the previous behaviour) meant the
        /// interval between the intrabar tick that fired the signal and that candle's
        /// close — the single most adverse stretch of a tap-and-fail — was invisible:
        /// MAE was systematically understated and a same-bar stop-out was never
        /// recorded as one, so it resolved later as a timeout or even a win. Because
        /// the signal fires intrabar, the candle's extremes AT THAT INSTANT are
        /// captured on the record, and only excursion beyond them counts as the
        /// trade's own.
        /// </summary>
        private void UpdateOpenSignals(int bar)
        {
            if (_openSignals.Count == 0)
                return;

            var candle = GetCandle(bar);

            for (var i = _openSignals.Count - 1; i >= 0; i--)
            {
                var s = _openSignals[i];
                if (bar < s.SignalBar)
                    continue;

                decimal high, low;
                if (bar == s.SignalBar)
                {
                    // Pre-entry price action on the signal bar is not the trade's.
                    high = candle.High > s.HighAtSignal ? candle.High : s.Entry;
                    low = candle.Low < s.LowAtSignal ? candle.Low : s.Entry;
                }
                else
                {
                    high = candle.High;
                    low = candle.Low;
                }

                if (s.Long)
                {
                    s.Mfe = Math.Max(s.Mfe, high - s.Entry);
                    s.Mae = Math.Max(s.Mae, s.Entry - low);
                }
                else
                {
                    s.Mfe = Math.Max(s.Mfe, s.Entry - low);
                    s.Mae = Math.Max(s.Mae, high - s.Entry);
                }

                var slHit = s.Long ? low <= s.Sl : high >= s.Sl;
                var tp3Hit = s.Long ? high >= s.Tp3 : low <= s.Tp3;
                var tp2Hit = s.Long ? high >= s.Tp2 : low <= s.Tp2;

                UpdateShadowManagement(s, high, low, slHit, tp2Hit, tp3Hit);

                if (slHit)
                {
                    Resolve(s, bar, "SL", s.Sl, -1m);
                }
                else if (tp3Hit)
                {
                    Resolve(s, bar, "TP3", s.Tp3, 3m);
                }
                else
                {
                    // TP2 is a MARKER, never a resolution. The raw model is a single
                    // fixed-stop position running to TP3; booking a clean +2R because
                    // price merely tagged 2R and then drifted sideways to the timeout
                    // credited profit that an unmanaged position never realised, and
                    // biased AvgR and win rate upward. Taking money off at 2R IS
                    // modelled — that is exactly what the Partial-at-+2R shadow does,
                    // and it is reported in its own column.
                    if (tp2Hit)
                        s.Tp2Hit = true;

                    if (bar - s.SignalBar >= SignalTimeoutBars)
                    {
                        var r = s.Risk > 0
                            ? (s.Long ? candle.Close - s.Entry : s.Entry - candle.Close) / s.Risk
                            : 0m;
                        Resolve(s, bar, "Timeout", candle.Close, r);
                    }
                }
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
        ///
        /// Warnings are ALWAYS journaled (HIST and LIVE) so their predictive value is
        /// measurable later; only the 🚨 alert itself honours AlertOnExitWarning. The
        /// previous guard sat on the whole method, so silencing the notification also
        /// silenced the data that would have told you whether the notification was
        /// worth keeping.
        /// </summary>
        private void CheckOpenSignalThreats(int bar)
        {
            if (_openSignals.Count == 0)
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

            if (!AlertOnExitWarning)
                return;

            Fire($"🚨 EXIT WARNING — open {(s.Long ? "LONG" : "SHORT")} (signal #{s.Id}, {s.Tier}) under threat\n" +
                 $"📍 Entry {FormatPrice(s.Entry)} · now {FormatPrice(close)} ({rTxt})\n" +
                 $"⚠️ Reason: {reason}\n" +
                 "👋 Consider exiting, tightening the stop, or stepping aside");
        }

        private void Resolve(SignalRecord s, int bar, string outcome, decimal exit, decimal rMultiple)
        {
            s.Resolved = true;
            s.Outcome = outcome;
            s.Exit = exit;
            s.ResolvedBar = bar;

            var risk = s.Risk;

            // Finalize shadows still open at raw resolution (timeout / TP2-latch paths;
            // SL and TP3 shadows always close bar-by-bar before this point). A shadow
            // whose trigger never fired simply mirrors the raw outcome.
            var close = GetCandle(bar).Close;
            var closeR = risk > 0 ? (s.Long ? close - s.Entry : s.Entry - close) / risk : 0m;

            if (!s.BeDone)
            {
                s.BeDone = true;
                s.BeR = s.BeTp2 ? 2m : s.BeArmed ? closeR : rMultiple;
            }

            if (!s.PartialDone)
            {
                s.PartialDone = true;
                s.PartialR = s.PartialTaken ? 1m + 0.5m * closeR : rMultiple;
            }

            _openSignals.Remove(s);
            _resolvedSignals.Add(s);

            if (!JournalEnabled)
                return;

            // A backfill-fired signal can resolve during live ticks; its signal row
            // was never journaled, so skip the outcome too — no orphan ids.
            if (JournalLiveOnly && !s.Live)
            {
                RequestAnalytics();
                return;
            }

            var maeR = risk > 0 ? s.Mae / risk : 0m;
            var mfeR = risk > 0 ? s.Mfe / risk : 0m;

            var line = string.Join(",",
                s.Id.ToString(CultureInfo.InvariantCulture),
                BarTime(bar).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                s.Live ? "LIVE" : "HIST",
                outcome,
                Num(exit),
                Num(rMultiple),
                Num(s.BeR),
                Num(s.PartialR),
                Num(maeR),
                Num(mfeR),
                (bar - s.SignalBar).ToString(CultureInfo.InvariantCulture),
                s.Long ? "Long" : "Short",
                s.Tier,
                s.ArmSource,
                Csv(s.TriggerTag),
                Csv(s.Layer));

            JournalWrite("outcomes.csv", OutcomesHeader, line);
            RequestAnalytics();
        }

        #endregion

        #region Analytics

        /// <summary>
        /// Queues an analytics rewrite. The chart thread only bounds the pool and takes
        /// a shallow snapshot; grouping, formatting and the file write all happen on the
        /// serialized IO chain, and concurrent requests coalesce into one write.
        /// </summary>
        private void RequestAnalytics()
        {
            if (!JournalEnabled)
                return;

            // Bound the retained pool: it used to grow for the entire session while
            // every resolution re-grouped all of it.
            var cap = Math.Max(100, AnalyticsMaxSignals);
            if (_resolvedSignals.Count > cap)
            {
                var excess = _resolvedSignals.Count - cap;
                _resolvedSignals.RemoveRange(0, excess);
                _analyticsTrimmed += excess;
            }

            // Analytics must mirror what outcomes.csv contains: in LIVE-only mode
            // that means live-fired signals exclusively.
            var pool = JournalLiveOnly
                ? _resolvedSignals.Where(s => s.Live).ToArray()
                : _resolvedSignals.ToArray();

            if (pool.Length == 0)
                return;

            var trimmed = _analyticsTrimmed;
            Volatile.Write(ref _analyticsSnapshot, pool);

            // One rewrite in flight at a time; a burst refreshes the snapshot instead
            // of queueing a full recomputation per signal.
            if (Interlocked.Exchange(ref _analyticsPending, 1) == 1)
                return;

            // JournalDir touches InstrumentInfo, so the path is resolved here on the
            // chart thread rather than inside the background task.
            var path = Path.Combine(JournalDir, $"{_sessionStamp}-analytics.csv");

            EnqueueIo(() =>
            {
                Interlocked.Exchange(ref _analyticsPending, 0);

                var snapshot = Volatile.Read(ref _analyticsSnapshot);
                if (snapshot.Length == 0)
                    return;

                var text = BuildAnalytics(snapshot, trimmed);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text);
            });
        }

        /// <summary>
        /// Aggregated performance report.
        ///
        /// Win rate = TP3 resolutions ÷ (TP3 + SL); timeouts are excluded from win rate
        /// but included in expectancy (AvgR) at their close-based R. The raw model is a
        /// single fixed-stop position running to TP3 — deliberately the least flattering
        /// reading. Management styles that bank profit earlier are reported separately in
        /// AvgBE1R_R and AvgPartial2R_R, which is where a 2R exit belongs.
        /// </summary>
        private static string BuildAnalytics(IReadOnlyList<SignalRecord> pool, int trimmed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("GroupBy,Key,Signals,Wins,Losses,Timeouts,WinRatePct,AvgR,AvgBE1R_R,AvgPartial2R_R,AvgMAE_R,AvgMFE_R");

            AppendGroup(sb, pool, "ZoneFamily", s => s.ZoneFamily);
            AppendGroup(sb, pool, "Layer", s => s.Layer);
            AppendGroup(sb, pool, "ArmSource", s => s.ArmSource);
            AppendGroup(sb, pool, "Tier", s => s.Tier);
            AppendGroup(sb, pool, "Direction", s => s.Long ? "Long" : "Short");
            AppendGroup(sb, pool, "Family+ArmSource", s => $"{s.ZoneFamily}/{s.ArmSource}");
            AppendGroup(sb, pool, "Family+Layer", s => $"{s.ZoneFamily}/{s.Layer}");
            AppendGroup(sb, pool, "ALL", _ => "ALL");

            if (trimmed > 0)
                sb.AppendLine($"# note,{trimmed} oldest resolved signals were trimmed from the in-memory pool " +
                              "(raise \"Analytics: max resolved signals retained\" to keep more); outcomes.csv remains complete");

            return sb.ToString();
        }

        private static void AppendGroup(StringBuilder sb, IReadOnlyList<SignalRecord> pool, string groupName,
            Func<SignalRecord, string> selector)
        {
            foreach (var group in pool.GroupBy(selector).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var total = group.Count();
                var wins = group.Count(s => s.Outcome == "TP3");
                var losses = group.Count(s => s.Outcome == "SL");
                var timeouts = group.Count(s => s.Outcome == "Timeout");
                var decisive = wins + losses;
                var winRate = decisive > 0 ? wins * 100m / decisive : 0m;

                var avgR = group.Average(s => s.Outcome switch
                {
                    "SL" => -1m,
                    "TP3" => 3m,
                    _ => s.Risk > 0 ? (s.Long ? s.Exit - s.Entry : s.Entry - s.Exit) / s.Risk : 0m
                });

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
