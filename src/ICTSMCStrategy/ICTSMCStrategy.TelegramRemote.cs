using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ICTSMC
{
    /// <summary>
    /// Telegram remote commands + self-rendered chart snapshots.
    ///
    /// Every chart running this indicator registers with a process-wide hub. The
    /// hub runs ONE long-poll loop per distinct bot token (Telegram allows a
    /// single getUpdates consumer per bot), listens for /shot, answers with an
    /// inline-keyboard list of the charts wired to the requesting chat, and on a
    /// button tap renders a fresh PNG of that chart's current state — candles
    /// plus every zone, liquidity line, EQ and structure marker, in the
    /// indicator's own visual style — and sends it via sendPhoto.
    ///
    /// The image is drawn from live data, so it works even when ATAS is
    /// minimized or the chart sits in a background tab. Commands from chat ids
    /// that no registered chart is configured for are ignored silently.
    /// </summary>
    public partial class ICTSMCStrategy
    {
        [Display(GroupName = GrpTelegram, Name = "Remote commands (/shot snapshots)", Order = 1030)]
        public bool TelegramRemoteEnabled
        {
            get => _telegramRemoteEnabled;
            set
            {
                _telegramRemoteEnabled = value;
                TelegramHub.Register(this);
            }
        }

        private bool _telegramRemoteEnabled = true;

        /// <summary>Bot token this chart is reachable under, or null when remote is off.</summary>
        internal string HubToken =>
            TelegramEnabled && TelegramRemoteEnabled && !string.IsNullOrWhiteSpace(TelegramBotToken)
                ? TelegramBotToken.Trim()
                : null;

        internal string HubChatId => TelegramChatId?.Trim() ?? "";

        /// <summary>Stable per-instance key used in callback buttons.</summary>
        internal string HubKey => _hubKey ??= Guid.NewGuid().ToString("N")[..12];
        private string _hubKey;

        internal string HubName
        {
            get
            {
                var instrument = InstrumentInfo?.Instrument ?? "chart";
                return string.IsNullOrEmpty(_chartTfLabel) ? instrument : $"{instrument} {_chartTfLabel}";
            }
        }

        #region Snapshot renderer

        /// <summary>
        /// Called from the hub's poller thread. The chart thread may mutate the
        /// zone/liquidity/structure lists mid-render, so the render is retried on
        /// the (rare, benign) collection-changed race instead of locking the hot
        /// trading path.
        /// </summary>
        internal byte[] TryRenderSnapshot()
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    return RenderSnapshot();
                }
                catch
                {
                    Thread.Sleep(120);
                }
            }

            return null;
        }

        private byte[] RenderSnapshot()
        {
            const int width = 1280, height = 720;
            const int barsBack = 120;

            var last = CurrentBar - 1;
            if (last < 5)
                throw new InvalidOperationException("not enough bars");

            var first = Math.Max(0, last - barsBack + 1);
            var count = last - first + 1;

            var hi = decimal.MinValue;
            var lo = decimal.MaxValue;
            for (var b = first; b <= last; b++)
            {
                var c = GetCandle(b);
                if (c.High > hi) hi = c.High;
                if (c.Low < lo) lo = c.Low;
            }

            if (hi <= lo)
                throw new InvalidOperationException("flat range");

            var pad = (hi - lo) * 0.04m;
            hi += pad;
            lo -= pad;

            var zones = _zones.Where(z => z.State != ZoneState.Mitigated).ToList();
            var liquidity = _liquidity.ToList();
            var structure = _structure.Where(e => e.Bar >= first).ToList();
            if (structure.Count > MaxStructureLabels)
                structure = structure.Skip(structure.Count - MaxStructureLabels).ToList();

            var plot = new Rectangle(8, 34, width - 8 - 76, height - 34 - 26);
            var bw = (float)plot.Width / (count + 6);
            float X(int bar) => plot.Left + (bar - first) * bw;
            float Y(decimal price) => plot.Top + (float)((double)((hi - price) / (hi - lo))) * plot.Height;
            var xLive = X(last) + bw * 3f;

            using var bmp = new Bitmap(width, height);
            using var g = Graphics.FromImage(bmp);
            using var fontSmall = new Font("Segoe UI", 8.5f);
            using var fontHeader = new Font("Segoe UI", 10f, FontStyle.Bold);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.FromArgb(14, 17, 22));

            var gray = Color.FromArgb(150, 156, 165);
            using var grayBrush = new SolidBrush(gray);
            using var gridPen = new Pen(Color.FromArgb(16, 255, 255, 255), 1);

            // Price grid + axis (nice steps: 1/2/5 × 10^k).
            var rawStep = (double)(hi - lo) / 7.0;
            var mag = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
            var norm = rawStep / mag;
            var step = (decimal)(mag * (norm < 1.5 ? 1 : norm < 3.5 ? 2 : norm < 7.5 ? 5 : 10));
            for (var p = Math.Ceiling(lo / step) * step; p <= hi; p += step)
            {
                var y = Y(p);
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                g.DrawString(FormatPrice(p), fontSmall, grayBrush, plot.Right + 4, y - 7);
            }

            // Time axis: ~6 labels.
            var tstep = Math.Max(1, count / 6);
            var multiDay = GetCandle(last).Time.Date != GetCandle(first).Time.Date;
            for (var b = first; b <= last; b += tstep)
            {
                var t = GetCandle(b).Time;
                var label = t.ToString(multiDay ? "dd MMM HH:mm" : "HH:mm", CultureInfo.InvariantCulture);
                g.DrawString(label, fontSmall, grayBrush, X(b) - 20, plot.Bottom + 6);
            }

            // Zones (under candles): HTF = thin frame, LTF = translucent fill.
            foreach (var z in zones)
            {
                if (z.Bottom > hi || z.Top < lo || z.StartBar > last)
                    continue;

                var x1 = Math.Max(plot.Left, X(Math.Max(z.StartBar, first)));
                var y1 = Math.Max(plot.Top, Y(Math.Min(z.Top, hi)));
                var y2 = Math.Min(plot.Bottom, Y(Math.Max(z.Bottom, lo)));
                var rect = new RectangleF(x1, y1, Math.Max(1f, xLive - x1), Math.Max(1f, y2 - y1));
                var baseColor = ZoneColor(z);

                if (z.IsHtf)
                {
                    using var framePen = new Pen(Color.FromArgb(220, HtfBorderColor), 1);
                    g.DrawRectangle(framePen, rect.X, rect.Y, rect.Width, rect.Height);
                }
                else
                {
                    using var fill = new SolidBrush(Color.FromArgb(z.State == ZoneState.Touched ? 30 : 46, baseColor));
                    using var border = new Pen(Color.FromArgb(160, baseColor), 1);
                    g.FillRectangle(fill, rect);
                    g.DrawRectangle(border, rect.X, rect.Y, rect.Width, rect.Height);
                }

                var tag = z.Tag;
                var size = g.MeasureString(tag, fontSmall);
                if (size.Width < rect.Width - 4 && size.Height < rect.Height + 4)
                {
                    using var tagBrush = new SolidBrush(Color.FromArgb(210, z.IsHtf ? HtfBorderColor : baseColor));
                    g.DrawString(tag, fontSmall, tagBrush,
                        rect.X + (rect.Width - size.Width) / 2f,
                        rect.Y + (rect.Height - size.Height) / 2f);
                }
            }

            // Liquidity: unswept pools + recently swept traps, dashed to the live edge.
            foreach (var lq in liquidity)
            {
                if (lq.Price > hi || lq.Price < lo || lq.StartBar > last)
                    continue;
                if (lq.Swept && (!lq.WasTrap.HasValue || !lq.WasTrap.Value || last - (lq.SweptBar ?? last) > SweptRetentionBars))
                    continue;

                var color = lq.BuySide ? BslColor : SslColor;
                var alpha = lq.Swept ? 70 : 170;
                var y = Y(lq.Price);
                var x1 = Math.Max(plot.Left, X(Math.Max(lq.StartBar, first)));
                using var pen = new Pen(Color.FromArgb(alpha, color), 1) { DashStyle = DashStyle.Dash };
                g.DrawLine(pen, x1, y, xLive, y);

                var label = lq.Swept ? "Sweep" : lq.IsEqual ? (lq.BuySide ? "EQH·BSL" : "EQL·SSL") : lq.BuySide ? "BSL" : "SSL";
                using var lb = new SolidBrush(Color.FromArgb(alpha + 40, color));
                g.DrawString(label, fontSmall, lb, Math.Max(x1, xLive - 60), lq.BuySide ? y - 15 : y + 2);
            }

            // Structure: last few BoS/MSS as dashed level lines with labels.
            foreach (var evt in structure)
            {
                if (evt.Level > hi || evt.Level < lo)
                    continue;
                var color = evt.Bullish ? BullStructureColor : BearStructureColor;
                var y = Y(evt.Level);
                var x1 = Math.Max(plot.Left, X(Math.Max(evt.FromBar, first)));
                var x2 = X(evt.Bar);
                using var pen = new Pen(Color.FromArgb(150, color), 1) { DashStyle = DashStyle.Dash };
                var label = evt.IsMss ? "MSS" : "BoS";
                var size = g.MeasureString(label, fontSmall);
                var mid = (x1 + x2) / 2f;
                g.DrawLine(pen, x1, y, mid - size.Width / 2f - 3, y);
                g.DrawLine(pen, mid + size.Width / 2f + 3, y, x2, y);
                using var sb2 = new SolidBrush(Color.FromArgb(200, color));
                g.DrawString(label, fontSmall, sb2, mid - size.Width / 2f, y - size.Height / 2f);
            }

            // Equilibrium of the current dealing range.
            var rangeOpt = GetDealingRange();
            if (rangeOpt.HasValue)
            {
                var eq = (rangeOpt.Value.High.Price + rangeOpt.Value.Low.Price) / 2m;
                if (eq < hi && eq > lo)
                {
                    var y = Y(eq);
                    using var pen = new Pen(Color.FromArgb(150, EquilibriumColor), 1) { DashStyle = DashStyle.DashDot };
                    g.DrawLine(pen, plot.Left, y, xLive, y);
                    g.DrawString("EQ 50%", fontSmall, grayBrush, plot.Left + 4, y - 15);
                }
            }

            // Candles on top.
            var bodyW = Math.Max(1f, bw * 0.62f);
            for (var b = first; b <= last; b++)
            {
                var c = GetCandle(b);
                var bull = c.Close >= c.Open;
                var color = bull ? BullStructureColor : BearStructureColor;
                var x = X(b) + bw / 2f;
                using var wick = new Pen(Color.FromArgb(200, color), 1);
                g.DrawLine(wick, x, Y(c.High), x, Y(c.Low));
                var yo = Y(c.Open);
                var yc = Y(c.Close);
                var top = Math.Min(yo, yc);
                var h = Math.Max(1f, Math.Abs(yo - yc));
                using var body = new SolidBrush(color);
                g.FillRectangle(body, x - bodyW / 2f, top, bodyW, h);
            }

            // Live price tag.
            var lastClose = GetCandle(last).Close;
            var yLast = Y(lastClose);
            using (var tagBrush = new SolidBrush(Color.FromArgb(41, 98, 255)))
            using (var white = new SolidBrush(Color.White))
            {
                var text = FormatPrice(lastClose);
                var size = g.MeasureString(text, fontSmall);
                g.FillRectangle(tagBrush, plot.Right + 2, yLast - size.Height / 2f, size.Width + 8, size.Height + 2);
                g.DrawString(text, fontSmall, white, plot.Right + 6, yLast - size.Height / 2f + 1);
            }

            // Header.
            var headerTime = GetCandle(last).Time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            using (var headBrush = new SolidBrush(Color.FromArgb(225, 230, 236)))
                g.DrawString($"{HubName}  ·  {headerTime}", fontHeader, headBrush, 10, 8);
            g.DrawString(_htfInfo, fontSmall, grayBrush, 10 + g.MeasureString($"{HubName}  ·  {headerTime}", fontHeader).Width + 14, 11);

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        #endregion
    }

    /// <summary>Process-wide registry + one Telegram long-poller per bot token.</summary>
    internal static class TelegramHub
    {
        private static readonly object Sync = new();
        private static readonly List<WeakReference<ICTSMCStrategy>> Instances = new();
        private static readonly Dictionary<string, Poller> Pollers = new();

        // Long-poll needs a client that outlives the 25s server-side hold.
        private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(40) };

        public static void Register(ICTSMCStrategy indicator)
        {
            lock (Sync)
            {
                Instances.RemoveAll(w => !w.TryGetTarget(out _));
                if (!Instances.Any(w => w.TryGetTarget(out var t) && ReferenceEquals(t, indicator)))
                    Instances.Add(new WeakReference<ICTSMCStrategy>(indicator));
            }

            EnsurePollers();
        }

        internal static List<ICTSMCStrategy> Live()
        {
            lock (Sync)
            {
                Instances.RemoveAll(w => !w.TryGetTarget(out _));
                var list = new List<ICTSMCStrategy>();
                foreach (var w in Instances)
                    if (w.TryGetTarget(out var t))
                        list.Add(t);
                return list;
            }
        }

        private static void EnsurePollers()
        {
            var tokens = Live().Select(i => i.HubToken)
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToList();

            lock (Sync)
            {
                foreach (var token in tokens)
                {
                    if (Pollers.ContainsKey(token))
                        continue;
                    var poller = new Poller(token);
                    Pollers[token] = poller;
                    poller.Start();
                }

                foreach (var stale in Pollers.Keys.Where(k => !tokens.Contains(k)).ToList())
                {
                    Pollers[stale].Stop();
                    Pollers.Remove(stale);
                }
            }
        }

        /// <summary>One getUpdates loop for one bot token.</summary>
        private sealed class Poller
        {
            private readonly string _token;
            private readonly CancellationTokenSource _cts = new();
            private long _offset;

            public Poller(string token) => _token = token;

            public void Start() => _ = Task.Run(LoopAsync);

            public void Stop() => _cts.Cancel();

            private List<ICTSMCStrategy> Charts() =>
                Live().Where(i => i.HubToken == _token).ToList();

            private async Task LoopAsync()
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        if (Charts().Count == 0)
                        {
                            await Task.Delay(10000, _cts.Token).ConfigureAwait(false);
                            continue;
                        }

                        var url = $"https://api.telegram.org/bot{_token}/getUpdates" +
                                  $"?timeout=25&offset={_offset}&allowed_updates=%5B%22message%22,%22callback_query%22%5D";
                        var json = await Client.GetStringAsync(url, _cts.Token).ConfigureAwait(false);

                        using var doc = JsonDocument.Parse(json);
                        if (!doc.RootElement.TryGetProperty("result", out var result) ||
                            result.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var update in result.EnumerateArray())
                        {
                            _offset = update.GetProperty("update_id").GetInt64() + 1;
                            try
                            {
                                Handle(update);
                            }
                            catch
                            {
                                // One malformed update must not kill the loop.
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Network error / 409 (another poller) / bad token: back off.
                        try { await Task.Delay(15000, _cts.Token).ConfigureAwait(false); }
                        catch { break; }
                    }
                }
            }

            private void Handle(JsonElement update)
            {
                if (update.TryGetProperty("message", out var message))
                {
                    var chatId = message.GetProperty("chat").GetProperty("id").GetInt64()
                        .ToString(CultureInfo.InvariantCulture);
                    var text = message.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";

                    // Only chats some registered chart is explicitly configured for.
                    var charts = Charts().Where(c => c.HubChatId == chatId).ToList();
                    if (charts.Count == 0)
                        return;

                    if (text.StartsWith("/shot", StringComparison.OrdinalIgnoreCase) ||
                        text.StartsWith("/charts", StringComparison.OrdinalIgnoreCase) ||
                        text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
                        SendChartKeyboard(chatId, charts);
                }
                else if (update.TryGetProperty("callback_query", out var callback))
                {
                    var callbackId = callback.GetProperty("id").GetString();
                    var chatId = callback.GetProperty("message").GetProperty("chat").GetProperty("id")
                        .GetInt64().ToString(CultureInfo.InvariantCulture);
                    var data = callback.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";

                    _ = PostAsync("answerCallbackQuery", new Dictionary<string, string>
                    {
                        ["callback_query_id"] = callbackId ?? ""
                    });

                    if (!data.StartsWith("shot:", StringComparison.Ordinal))
                        return;

                    var key = data.Substring(5);
                    var chart = Charts().FirstOrDefault(c => c.HubChatId == chatId && c.HubKey == key);

                    if (chart == null)
                    {
                        SendText(chatId, "⚠️ That chart is no longer active — send /shot for a fresh list.");
                        return;
                    }

                    var png = chart.TryRenderSnapshot();
                    if (png == null)
                    {
                        SendText(chatId, "⚠️ Could not render the chart right now — please try again.");
                        return;
                    }

                    var caption = $"📸 {chart.HubName} · {DateTime.Now:HH:mm:ss}";
                    _ = SendPhotoAsync(chatId, png, caption);
                }
            }

            private void SendChartKeyboard(string chatId, List<ICTSMCStrategy> charts)
            {
                var buttons = charts
                    .OrderBy(c => c.HubName)
                    .Select(c => $"[{{\"text\":\"📸 {c.HubName}\",\"callback_data\":\"shot:{c.HubKey}\"}}]");
                var markup = "{\"inline_keyboard\":[" + string.Join(",", buttons) + "]}";

                _ = PostAsync("sendMessage", new Dictionary<string, string>
                {
                    ["chat_id"] = chatId,
                    ["text"] = "📸 Select a chart:",
                    ["reply_markup"] = markup
                });
            }

            private void SendText(string chatId, string text) =>
                _ = PostAsync("sendMessage", new Dictionary<string, string>
                {
                    ["chat_id"] = chatId,
                    ["text"] = text
                });

            private async Task PostAsync(string method, Dictionary<string, string> fields)
            {
                try
                {
                    var url = $"https://api.telegram.org/bot{_token}/{method}";
                    using var payload = new FormUrlEncodedContent(fields);
                    using var response = await Client.PostAsync(url, payload).ConfigureAwait(false);
                    _ = response.IsSuccessStatusCode;
                }
                catch
                {
                    // Remote-command replies are best-effort.
                }
            }

            private async Task SendPhotoAsync(string chatId, byte[] png, string caption)
            {
                try
                {
                    var url = $"https://api.telegram.org/bot{_token}/sendPhoto";
                    using var form = new MultipartFormDataContent();
                    form.Add(new StringContent(chatId), "chat_id");
                    form.Add(new StringContent(caption), "caption");
                    var photo = new ByteArrayContent(png);
                    photo.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                    form.Add(photo, "photo", "chart.png");
                    using var response = await Client.PostAsync(url, form).ConfigureAwait(false);
                    _ = response.IsSuccessStatusCode;
                }
                catch
                {
                    // Best-effort.
                }
            }
        }
    }
}
