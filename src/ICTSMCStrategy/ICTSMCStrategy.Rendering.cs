using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ICTSMC
{
    /// <summary>
    /// Live-trading rendering. Philosophy: the chart shows only what is tradeable
    /// RIGHT NOW — mitigated objects vanish, zones stop at the live candle instead
    /// of smearing to the right edge, and Clean mode culls anything far from price.
    /// The full detection state lives on underneath (alerts, entry model and the
    /// journal all see everything); rendering is just the visible slice of it.
    ///
    /// THREADING: OnRender runs on the chart's drawing thread while OnCalculate runs
    /// on the data thread. Nothing here touches the engine's live collections — it
    /// works exclusively from the immutable <see cref="RenderModel"/> snapshot the
    /// calculation thread publishes, so there is no shared mutable state to race on.
    /// </summary>
    public partial class ICTSMCStrategy
    {
        private static readonly RenderFont ZoneFont = new("Segoe UI", 8f);
        private static readonly RenderFont StructureFont = new("Segoe UI", 8f, FontStyle.Bold);

        private const int ActiveZoneAlpha = 46;
        private const int TouchedZoneAlpha = 30;
        private const int MitigatedZoneAlpha = 14;
        private const int PdShadeAlpha = 10;
        private const int OteShadeAlpha = 16;
        private const int LabelBackdropAlpha = 120;

        // Pens are immutable for a given (colour, width, dash) triple and were
        // previously reallocated for every zone on every frame. The cache is only
        // ever touched from the drawing thread.
        private readonly Dictionary<(int Argb, int Width, DashStyle Dash), RenderPen> _penCache = new();

        /// <summary>One-shot latch so a permanent render fault is recorded once, not per frame.</summary>
        private bool _renderErrorLogged;

        private RenderPen GetPen(Color color, int width = 1, DashStyle dash = DashStyle.Solid)
        {
            var key = (color.ToArgb(), width, dash);
            if (_penCache.TryGetValue(key, out var pen))
                return pen;

            pen = new RenderPen(color, width, dash);
            _penCache[key] = pen;
            return pen;
        }

        /// <summary>
        /// Drops the cached pens on teardown. RenderPen holds no unmanaged handle of its own
        /// (it is not IDisposable), so this only releases the references — but the cache is
        /// keyed per colour/width/dash and lives as long as the indicator, and until now the
        /// indicator had no teardown path at all.
        /// </summary>
        private void DisposePenCache() => _penCache.Clear();

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo == null || InstrumentInfo == null || CurrentBar < 2)
                return;

            // Single volatile read: everything below works from an immutable snapshot.
            var model = Volatile.Read(ref _renderModel);
            if (model == null)
                return;

            // Defence in depth. ATAS gives OnRender no exception boundary of its own,
            // so anything thrown here degrades or kills the chart's drawing for the
            // rest of the session. Losing one frame is always preferable.
            try
            {
                var region = Container.Region;
                var lastBar = CurrentBar - 1;

                if (ShowPremiumDiscount)
                    RenderPremiumDiscount(context, region, model);

                RenderZones(context, region, lastBar, model);

                if (ShowLiquidity)
                    RenderLiquidity(context, region, lastBar, model);

                if (ShowStructure)
                    RenderStructure(context, region, model);

                if (HtfEnabled && ShowHtfInfoBadge)
                    RenderHtfBadge(context, region, model);
            }
            catch (Exception ex)
            {
                // Drop the frame; the next one redraws from a fresh snapshot.
                //
                // But a throw here is usually PERMANENT, not transient — a bad setting
                // combination reaches the same line on every frame — and the catch made that
                // indistinguishable from "there is nothing to draw". Worse, layers render in
                // sequence, so a fault part-way through leaves the earlier ones on screen and
                // silently removes the rest. The first fault is therefore recorded.
                if (!_renderErrorLogged)
                {
                    _renderErrorLogged = true;

                    try
                    {
                        JournalEventAt(model.HasLiveCandle ? model.LiveCandle.Time : DateTime.Now,
                            "RenderFailed", "", null, 0m,
                            $"{ex.GetType().Name}: {ex.Message} — every chart layer after the " +
                            "failure point was not drawn this frame");
                    }
                    catch
                    {
                        // Diagnostics must never themselves break the drawing thread.
                    }
                }
            }
        }

        /// <summary>
        /// Small badge in the chart corner showing the measured chart timeframe and
        /// the HTF layer(s) in use — so the auto selection is always verifiable at a glance.
        /// </summary>
        /// <summary>
        /// Vertical offset of the HTF badge from the top of the chart region. Platforms paint
        /// their own attribution watermark ("Trading Platform by …") along the very top of the
        /// same area, and at +8 the badge landed straight in it — legible only in fragments,
        /// which matters because the badge is the fastest check that detection is healthy.
        /// </summary>
        private const int HtfBadgeTopOffset = 28;

        private void RenderHtfBadge(RenderContext context, Rectangle region, RenderModel model)
        {
            var text = string.IsNullOrEmpty(model.HtfInfo) ? "HTF: measuring chart timeframe…" : model.HtfInfo;

            var x = region.Left + 10;
            var y = region.Top + HtfBadgeTopOffset;
            var size = context.MeasureString(text, ZoneFont);

            // Same translucent backdrop the zone labels use, so the badge stays readable
            // over candles and over anything the platform draws underneath it.
            context.FillRectangle(Color.FromArgb(LabelBackdropAlpha, 14, 17, 22),
                new Rectangle(x - 4, y - 2, size.Width + 8, size.Height + 4));

            context.DrawString(text, ZoneFont, Color.FromArgb(205, 200, 200, 200), x, y);
        }

        #region Zones

        /// <summary>
        /// Clean mode shows the nearest unmitigated zones per side within an ATR
        /// budget (HTF zones get double range — a fresh Daily OB stays visible
        /// longer). Detailed mode shows every unmitigated zone.
        /// </summary>
        private List<ZoneView> SelectVisibleZones(RenderModel model)
        {
            IEnumerable<ZoneView> pool = model.Zones;

            if (!ShowMitigated)
                pool = pool.Where(z => z.State != ZoneState.Mitigated);

            pool = pool.Where(z =>
                (!z.IsOrderBlock || ShowOb) &&
                (z.IsOrderBlock || ShowFvg));

            if (DisplayMode == DisplayMode.Detailed)
                return pool.ToList();

            var price = model.LastClose;

            // "No distance limit" is carried as a FLAG, never as a sentinel value.
            // Using decimal.MaxValue as the budget meant the HTF branch below computed
            // decimal.MaxValue * 2, which overflows — and because OnRender's catch-all
            // swallows the throw, every layer after this one (zones, liquidity, structure,
            // the HTF badge) silently stopped drawing while premium/discount, rendered
            // earlier in the frame, kept working. A chart showing only EQ and OTE was the
            // visible symptom.
            var unlimited = ZoneVisibilityAtrRange <= 0 || model.Atr <= 0;
            var budget = unlimited ? 0m : model.Atr * ZoneVisibilityAtrRange;

            decimal Distance(ZoneView z) => z.Contains(price)
                ? 0m
                : Math.Min(Math.Abs(price - z.Top), Math.Abs(price - z.Bottom));

            var candidates = pool
                .Where(z => z.State != ZoneState.Mitigated)
                .Select(z => (Zone: z, Dist: Distance(z)))
                .Where(t => unlimited || t.Dist <= (t.Zone.IsHtf ? budget * 2 : budget))
                .ToList();

            var visible = new List<ZoneView>();

            foreach (var bullish in new[] { true, false })
            {
                var side = candidates.Where(t => t.Zone.IsBullish == bullish)
                                     .OrderBy(t => t.Dist)
                                     .ToList();

                // Separate budgets so LTF triggers never crowd out the HTF map.
                visible.AddRange(side.Where(t => !t.Zone.IsHtf).Take(MaxVisibleZonesPerSide).Select(t => t.Zone));
                visible.AddRange(side.Where(t => t.Zone.IsHtf).Take(MaxVisibleZonesPerSide).Select(t => t.Zone));
            }

            return visible;
        }

        private void RenderZones(RenderContext context, Rectangle region, int lastBar, RenderModel model)
        {
            var visible = SelectVisibleZones(model);
            if (visible.Count == 0)
                return;

            // Zones stop one bar past the live candle — they track price, never
            // smear across the screen.
            var xLast = ChartInfo.GetXByBar(lastBar, false);
            var barWidth = lastBar > 0 ? Math.Max(2, xLast - ChartInfo.GetXByBar(lastBar - 1, false)) : 4;
            var xLive = Math.Min(region.Right, xLast + barWidth * 2);

            // HTF frames first, LTF fills on top — confluence reads as layers.
            foreach (var zone in visible.OrderByDescending(z => z.IsHtf).ThenBy(z => z.StartBar))
            {
                var x1 = ChartInfo.GetXByBar(zone.StartBar, false);
                var x2 = zone.HasEndBar
                    ? Math.Min(ChartInfo.GetXByBar(zone.EndBar, false), xLive)
                    : xLive;

                var y1 = ChartInfo.GetYByPrice(zone.Top, false);
                var y2 = ChartInfo.GetYByPrice(zone.Bottom, false);

                x1 = Math.Max(x1, region.Left);
                x2 = Math.Min(x2, region.Right);
                if (x2 <= x1 || Math.Max(y1, y2) < region.Top || Math.Min(y1, y2) > region.Bottom)
                    continue;

                var baseColor = ZoneColor(zone.Type);
                var rect = new Rectangle(x1, Math.Min(y1, y2), x2 - x1, Math.Max(2, Math.Abs(y2 - y1)));

                if (zone.IsHtf)
                {
                    // HTF zones are frames, not fills — they outline confluence
                    // without stacking paint over the LTF zones inside them. The
                    // heavier 2px stroke marks them as HTF; the hue marks the side.
                    context.DrawRectangle(GetPen(Color.FromArgb(220, HtfColor(zone.IsBullish)), 2), rect);
                }
                else
                {
                    var alpha = zone.State switch
                    {
                        ZoneState.Mitigated => MitigatedZoneAlpha,
                        ZoneState.Touched => TouchedZoneAlpha,
                        _ => ActiveZoneAlpha
                    };

                    context.FillRectangle(Color.FromArgb(alpha, baseColor), rect);
                    context.DrawRectangle(GetPen(Color.FromArgb(160, baseColor), 1), rect);

                    // Midline (consequent encroachment) when there is room.
                    if (zone.State != ZoneState.Mitigated && rect.Width >= 14 && rect.Height >= 8)
                    {
                        var midY = ChartInfo.GetYByPrice(zone.Mid, false);
                        if (midY > region.Top && midY < region.Bottom)
                            context.DrawLine(GetPen(Color.FromArgb(70, baseColor), 1, DashStyle.Dot),
                                x1, midY, x2, midY);
                    }
                }

                DrawCenteredZoneLabel(context, zone, rect, baseColor);
            }
        }

        /// <summary>
        /// Zone tag precisely centered (both axes) on a translucent backdrop pill,
        /// auto-hidden whenever the zone is too small to host it cleanly.
        /// </summary>
        private void DrawCenteredZoneLabel(RenderContext context, ZoneView zone, Rectangle rect, Color baseColor)
        {
            if (zone.State == ZoneState.Mitigated)
                return;

            var size = context.MeasureString(zone.Tag, ZoneFont);
            if (rect.Width < size.Width + 10 || rect.Height < size.Height + 4)
                return;

            var textX = rect.Left + (rect.Width - size.Width) / 2;
            var textY = rect.Top + (rect.Height - size.Height) / 2;

            // Backdrop pill: keeps the tag readable where zones overlap candles.
            context.FillRectangle(Color.FromArgb(LabelBackdropAlpha, 14, 17, 22),
                new Rectangle(textX - 3, textY - 1, size.Width + 6, size.Height + 2));

            var labelColor = zone.IsHtf ? HtfColor(zone.IsBullish) : baseColor;
            context.DrawString(zone.Tag, ZoneFont, Color.FromArgb(240, labelColor), textX, textY);
        }

        /// <summary>
        /// Standard ICT line style: the line splits around its centered label —
        /// `---- BoS ----` — with the text sitting DIRECTLY on the chart (no
        /// backdrop box). If the segment is too short to host the label, the
        /// plain line is drawn without text.
        /// </summary>
        private void DrawLabeledLine(RenderContext context, RenderPen pen, string text, Color textColor,
            int x1, int x2, int y, Rectangle region)
        {
            if (string.IsNullOrEmpty(text))
            {
                context.DrawLine(pen, x1, y, x2, y);
                return;
            }

            var size = context.MeasureString(text, StructureFont);
            if (x2 - x1 < size.Width + 18)
            {
                context.DrawLine(pen, x1, y, x2, y);
                return;
            }

            const int gap = 5;
            var textX = x1 + (x2 - x1 - size.Width) / 2;

            context.DrawLine(pen, x1, y, textX - gap, y);
            context.DrawLine(pen, textX + size.Width + gap, y, x2, y);

            var textY = Math.Max(region.Top + 1,
                Math.Min(y - size.Height / 2, region.Bottom - size.Height - 1));
            context.DrawString(text, StructureFont, textColor, textX, textY);
        }

        /// <summary>Frame colour for an HTF zone — metallic either way, directional by hue.</summary>
        private Color HtfColor(bool bullish) => bullish ? HtfBorderColor : HtfBearBorderColor;

        private Color ZoneColor(ZoneType type) => type switch
        {
            ZoneType.BullOrderBlock => BullObColor,
            ZoneType.BearOrderBlock => BearObColor,
            ZoneType.BullFvg => BullFvgColor,
            ZoneType.BearFvg => BearFvgColor,
            ZoneType.BullIfvg => BullIfvgColor,
            ZoneType.BullBreaker => BullBreakerColor,
            ZoneType.BearBreaker => BearBreakerColor,
            _ => BearIfvgColor
        };

        #endregion

        #region Liquidity

        private void RenderLiquidity(RenderContext context, Rectangle region, int lastBar, RenderModel model)
        {
            // Liquidity lines track price like zones do: unswept levels stop one
            // bar past the live candle instead of running to the screen edge.
            var xLast = ChartInfo.GetXByBar(lastBar, false);
            var barWidth = lastBar > 0 ? Math.Max(2, xLast - ChartInfo.GetXByBar(lastBar - 1, false)) : 4;
            var xLive = Math.Min(region.Right, xLast + barWidth * 2);

            foreach (var level in model.Liquidity)
            {
                // Swept liquidity is history — it fades out after a short window.
                if (level.Swept && level.HasSweptBar &&
                    DisplayMode == DisplayMode.Clean &&
                    lastBar - level.SweptBar > SweptRetentionBars)
                    continue;

                var y = ChartInfo.GetYByPrice(level.Price, false);
                if (y < region.Top || y > region.Bottom)
                    continue;

                var x1 = Math.Max(ChartInfo.GetXByBar(level.StartBar, false), region.Left);
                var x2 = level.Swept && level.HasSweptBar
                    ? Math.Min(ChartInfo.GetXByBar(level.SweptBar, false), region.Right)
                    : xLive;

                if (x2 <= x1)
                    continue;

                // Clustered stops are a stronger magnet, and so are previous session
                // extremes — EQH/EQL pools and PDH/PDL/PWH/PWL draw with a heavier stroke.
                var color = level.BuySide ? BslColor : SslColor;
                var alpha = level.Swept ? 70 : 170;
                var pen = GetPen(Color.FromArgb(alpha, color), level.Emphasis ? 2 : 1, DashStyle.Dash);

                if (!level.Swept)
                {
                    DrawLabeledLine(context, pen, level.Label, Color.FromArgb(220, color), x1, x2, y, region);
                }
                else
                {
                    // Only genuine sweeps (trap: closed back inside) are labeled —
                    // runs stay unlabeled, per standard ICT chart presentation.
                    var label = level.WasTrap ? "Sweep" : null;
                    DrawLabeledLine(context, pen, label, Color.FromArgb(200, color), x1, x2, y, region);
                }
            }
        }

        #endregion

        #region Structure

        private void RenderStructure(RenderContext context, Rectangle region, RenderModel model)
        {
            // Only the most recent events matter live; old structure is implied
            // by the zones it produced.
            var recent = model.Structure.Count > MaxStructureLabels
                ? model.Structure.Skip(model.Structure.Count - MaxStructureLabels)
                : model.Structure;

            foreach (var evt in recent)
            {
                var y = ChartInfo.GetYByPrice(evt.Level, false);
                if (y < region.Top || y > region.Bottom)
                    continue;

                var x1 = ChartInfo.GetXByBar(evt.FromBar, false);
                var x2 = ChartInfo.GetXByBar(evt.Bar, false);

                if (x2 < region.Left || x1 > region.Right)
                    continue;

                x1 = Math.Max(x1, region.Left);
                x2 = Math.Min(x2, region.Right);

                // `---- MSS ----` solid and heavier, `---- BoS ----` dashed and light:
                // an MSS is the stronger, rarer event and should read that way at a glance.
                var color = evt.Bullish ? BullStructureColor : BearStructureColor;
                var pen = evt.IsMss
                    ? GetPen(Color.FromArgb(200, color), 2)
                    : GetPen(Color.FromArgb(150, color), 1, DashStyle.Dash);

                DrawLabeledLine(context, pen, evt.IsMss ? "MSS" : "BoS",
                    Color.FromArgb(240, color), x1, x2, y, region);
            }
        }

        #endregion

        #region Premium / Discount

        private void RenderPremiumDiscount(RenderContext context, Rectangle region, RenderModel model)
        {
            // Same ordered dealing range the entry filter uses — the drawing on the
            // chart and the PD gate in the signal engine can never disagree.
            if (!model.HasRange)
                return;

            var high = model.RangeHigh;
            var low = model.RangeLow;
            var eq = model.RangeEq;

            var x1 = Math.Max(ChartInfo.GetXByBar(model.RangeAnchorBar, false), region.Left);
            var x2 = region.Right;
            if (x2 <= x1)
                return;

            var yHigh = Math.Max(ChartInfo.GetYByPrice(high, false), region.Top);
            var yEq = ChartInfo.GetYByPrice(eq, false);
            var yLow = Math.Min(ChartInfo.GetYByPrice(low, false), region.Bottom);

            if (yEq <= region.Top || yEq >= region.Bottom)
                return;

            if (PdShadingEnabled)
            {
                if (yEq > yHigh)
                    context.FillRectangle(Color.FromArgb(PdShadeAlpha, PremiumColor),
                        new Rectangle(x1, yHigh, x2 - x1, yEq - yHigh));

                if (yLow > yEq)
                    context.FillRectangle(Color.FromArgb(PdShadeAlpha, DiscountColor),
                        new Rectangle(x1, yEq, x2 - x1, yLow - yEq));
            }

            // OTE pocket (0.618–0.79 retracement of the current impulse leg).
            if (ShowOte && model.HasOte)
            {
                var yOteTop = ChartInfo.GetYByPrice(model.OteTop, false);
                var yOteBottom = ChartInfo.GetYByPrice(model.OteBottom, false);
                var top = Math.Max(region.Top, Math.Min(yOteTop, yOteBottom));
                var bottom = Math.Min(region.Bottom, Math.Max(yOteTop, yOteBottom));

                if (bottom > top)
                {
                    context.FillRectangle(Color.FromArgb(OteShadeAlpha, OteColor),
                        new Rectangle(x1, top, x2 - x1, bottom - top));
                    context.DrawString("OTE", ZoneFont, Color.FromArgb(170, OteColor), x1 + 3, top + 2);
                }
            }

            context.DrawLine(GetPen(Color.FromArgb(150, EquilibriumColor), 1, DashStyle.DashDot),
                x1, yEq, x2, yEq);
            context.DrawString("EQ 50%", ZoneFont, Color.FromArgb(180, EquilibriumColor), x1 + 3, yEq + 2);
        }

        #endregion
    }
}
