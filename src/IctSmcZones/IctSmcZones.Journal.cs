using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IctSmc
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
    ///                    R-multiple, MAE/MFE in R, bars held
    ///  • analytics.csv — aggregated win-rate / expectancy per zone family,
    ///                    layer (LTF/4H/D…), arm source (Sweep vs TrapArm) and tier
    ///
    /// Files are per session (a new set on every full recalculation), so historical
    /// backfill and live rows never duplicate. Rows carry a LIVE/HIST flag: HIST rows
    /// come from replaying chart history — a built-in backtest of the exact same code
    /// path the live signals use. All IO is buffered and flushed off the chart thread;
    /// an IO failure can never touch trading logic.
    /// </summary>
    public partial class IctSmcZones
    {
        private const string GrpJournal = "11. Journal";

        #region Settings

        [Display(GroupName = GrpJournal, Name = "Enable journaling", Order = 1100)]
        public bool JournalEnabled { get; set; } = true;

        [Display(GroupName = GrpJournal, Name = "Journal folder (empty = Documents\\ATAS\\ICTSMC-Journal)", Order = 1110)]
        public string JournalPath { get; set; } = "";

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
        private int _nextZoneId;
        private int _nextSignalId;

        private const string EventsHeader =
            "Time,Mode,Instrument,Event,Direction,ZoneId,ZoneTag,Layer,Top,Bottom,Price,Extra";
        private const string SignalsHeader =
            "SignalId,Time,Mode,Instrument,Direction,Tier,ArmSource,TriggerTag,Layer,ZoneTop,ZoneBottom,Entry,SL,TP2,TP3,PdStatus,Confluence";
        private const string OutcomesHeader =
            "SignalId,ResolvedTime,Mode,Outcome,ExitPrice,RMultiple,MAE_R,MFE_R,BarsHeld,Direction,Tier,ArmSource,TriggerTag,Layer";

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
            _sessionStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
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
        /// </summary>
        private void UpdateOpenSignals(int bar)
        {
            if (_openSignals.Count == 0)
                return;

            var candle = GetCandle(bar);

            for (var i = _openSignals.Count - 1; i >= 0; i--)
            {
                var s = _openSignals[i];
                if (bar <= s.SignalBar)
                    continue;

                if (s.Long)
                {
                    s.Mfe = Math.Max(s.Mfe, candle.High - s.Entry);
                    s.Mae = Math.Max(s.Mae, s.Entry - candle.Low);
                }
                else
                {
                    s.Mfe = Math.Max(s.Mfe, s.Entry - candle.Low);
                    s.Mae = Math.Max(s.Mae, candle.High - s.Entry);
                }

                var slHit = s.Long ? candle.Low <= s.Sl : candle.High >= s.Sl;
                var tp3Hit = s.Long ? candle.High >= s.Tp3 : candle.Low <= s.Tp3;
                var tp2Hit = s.Long ? candle.High >= s.Tp2 : candle.Low <= s.Tp2;

                if (slHit)
                {
                    Resolve(s, bar, "SL", s.Sl, -1m);
                }
                else if (tp3Hit)
                {
                    Resolve(s, bar, "TP3", s.Tp3, 3m);
                }
                else if (tp2Hit)
                {
                    s.Tp2Hit = true;
                    if (bar - s.SignalBar >= SignalTimeoutBars)
                        Resolve(s, bar, "TP2", s.Tp2, 2m);
                }
                else if (bar - s.SignalBar >= SignalTimeoutBars)
                {
                    if (s.Tp2Hit)
                    {
                        Resolve(s, bar, "TP2", s.Tp2, 2m);
                    }
                    else
                    {
                        var r = s.Risk > 0
                            ? (s.Long ? candle.Close - s.Entry : s.Entry - candle.Close) / s.Risk
                            : 0m;
                        Resolve(s, bar, "Timeout", candle.Close, r);
                    }
                }
            }
        }

        private void Resolve(SignalRecord s, int bar, string outcome, decimal exit, decimal rMultiple)
        {
            s.Resolved = true;
            s.Outcome = outcome;
            s.Exit = exit;
            s.ResolvedBar = bar;

            _openSignals.Remove(s);
            _resolvedSignals.Add(s);

            if (!JournalEnabled)
                return;

            var risk = s.Risk;
            var maeR = risk > 0 ? s.Mae / risk : 0m;
            var mfeR = risk > 0 ? s.Mfe / risk : 0m;

            var line = string.Join(",",
                s.Id.ToString(CultureInfo.InvariantCulture),
                BarTime(bar).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                s.Live ? "LIVE" : "HIST",
                outcome,
                Num(exit),
                Num(rMultiple),
                Num(maeR),
                Num(mfeR),
                (bar - s.SignalBar).ToString(CultureInfo.InvariantCulture),
                s.Long ? "Long" : "Short",
                s.Tier,
                s.ArmSource,
                Csv(s.TriggerTag),
                Csv(s.Layer));

            JournalWrite("outcomes.csv", OutcomesHeader, line);
            WriteAnalytics();
        }

        #endregion

        #region Analytics

        /// <summary>
        /// Rewrites the aggregated performance file after every resolution.
        /// Win rate = TP2+ resolutions ÷ (wins + SL losses); timeouts are excluded
        /// from win rate but included in expectancy (AvgR).
        /// </summary>
        private void WriteAnalytics()
        {
            if (!JournalEnabled || _resolvedSignals.Count == 0)
                return;

            var sb = new StringBuilder();
            sb.AppendLine("GroupBy,Key,Signals,Wins,Losses,Timeouts,WinRatePct,AvgR,AvgMAE_R,AvgMFE_R");

            AppendGroup(sb, "ZoneFamily", s => s.ZoneFamily);
            AppendGroup(sb, "Layer", s => s.Layer);
            AppendGroup(sb, "ArmSource", s => s.ArmSource);
            AppendGroup(sb, "Tier", s => s.Tier);
            AppendGroup(sb, "Direction", s => s.Long ? "Long" : "Short");
            AppendGroup(sb, "Family+ArmSource", s => $"{s.ZoneFamily}/{s.ArmSource}");
            AppendGroup(sb, "Family+Layer", s => $"{s.ZoneFamily}/{s.Layer}");
            AppendGroup(sb, "ALL", _ => "ALL");

            var path = Path.Combine(JournalDir, $"{_sessionStamp}-analytics.csv");
            var text = sb.ToString();

            EnqueueIo(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text);
            });
        }

        private void AppendGroup(StringBuilder sb, string groupName, Func<SignalRecord, string> selector)
        {
            foreach (var group in _resolvedSignals.GroupBy(selector).OrderBy(g => g.Key))
            {
                var total = group.Count();
                var wins = group.Count(s => s.Outcome is "TP2" or "TP3");
                var losses = group.Count(s => s.Outcome == "SL");
                var timeouts = group.Count(s => s.Outcome == "Timeout");
                var decisive = wins + losses;
                var winRate = decisive > 0 ? wins * 100m / decisive : 0m;

                var avgR = group.Average(s => s.Outcome switch
                {
                    "SL" => -1m,
                    "TP2" => 2m,
                    "TP3" => 3m,
                    _ => s.Risk > 0 ? (s.Long ? s.Exit - s.Entry : s.Entry - s.Exit) / s.Risk : 0m
                });

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
                    avgMae.ToString("0.##", CultureInfo.InvariantCulture),
                    avgMfe.ToString("0.##", CultureInfo.InvariantCulture)));
            }
        }

        #endregion
    }
}
