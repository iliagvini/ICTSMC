using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace IctSmc
{
    /// <summary>
    /// Live-trading rendering. Philosophy: the chart shows only what is tradeable
    /// RIGHT NOW — mitigated objects vanish, zones stop at the live candle instead
    /// of smearing to the right edge, and Clean mode culls anything far from price.
    /// The full detection state lives on underneath (alerts, entry model and the
    /// journal all see everything); rendering is just the visible slice of it.
    /// </summary>
    public partial class IctSmcZones
    {
        private static readonly RenderFont ZoneFont = new("Segoe UI", 8f);
        private static readonly RenderFont StructureFont = new("Segoe UI", 8f, FontStyle.Bold);

        private const int ActiveZoneAlpha = 46;
        private const int TouchedZoneAlpha = 30;
        private const int MitigatedZoneAlpha = 14;
        private const int PdShadeAlpha = 10;

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo == null || InstrumentInfo == null || CurrentBar < 2)
                return;

            var region = Container.Region;
            var lastBar = CurrentBar - 1;

            if (ShowPremiumDiscount)
                RenderPremiumDiscount(context, region, lastBar);

            RenderZones(context, region, lastBar);

            if (ShowLiquidity)
                RenderLiquidity(context, region, lastBar);

            if (ShowStructure)
                RenderStructure(context, region);

            if (HtfEnabled && ShowHtfInfoBadge)
                RenderHtfBadge(context, region);
        }

        /// <summary>
        /// Small badge in the chart corner showing the measured chart timeframe and
        /// the HTF layer(s) in use — so the auto selection is always verifiable at a glance.
        /// </summary>
        private void RenderHtfBadge(RenderContext context, Rectangle region)
        {
            var text = string.IsNullOrEmpty(_htfInfo) ? "HTF: measuring chart timeframe…" : _htfInfo;
            context.DrawString(text, ZoneFont, Color.FromArgb(190, 200, 200, 200), region.Left + 10, region.Top + 8);
        }

        #region Zones

        /// <summary>
        /// Clean mode shows the nearest unmitigated zones per side within an ATR
        /// budget (HTF zones get double range — a fresh Daily OB stays visible
        /// longer). Detailed mode shows every unmitigated zone.
        /// </summary>
        private List<Zone> SelectVisibleZones(int lastBar)
        {
            IEnumerable<Zone> pool = _zones;

            if (!ShowMitigated)
                pool = pool.Where(z => z.State != ZoneState.Mitigated);

            pool = pool.Where(z =>
                (!z.IsOrderBlock || ShowOb) &&
                (z.IsOrderBlock || ShowFvg));

            if (DisplayMode == DisplayMode.Detailed)
                return pool.ToList();

            var price = GetCandle(lastBar).Close;
            var budget = _atr > 0 && ZoneVisibilityAtrRange > 0
                ? _atr * ZoneVisibilityAtrRange
                : decimal.MaxValue;

            decimal Distance(Zone z) => z.Contains(price)
                ? 0m
                : Math.Min(Math.Abs(price - z.Top), Math.Abs(price - z.Bottom));

            var candidates = pool
                .Where(z => z.State != ZoneState.Mitigated)
                .Select(z => (Zone: z, Dist: Distance(z)))
                .Where(t => t.Dist <= (t.Zone.IsHtf ? budget * 2 : budget))
                .ToList();

            var visible = new List<Zone>();

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

        private void RenderZones(RenderContext context, Rectangle region, int lastBar)
        {
            var visible = SelectVisibleZones(lastBar);
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
                var x2 = zone.EndBar.HasValue
                    ? Math.Min(ChartInfo.GetXByBar(zone.EndBar.Value, false), xLive)
                    : xLive;

                var y1 = ChartInfo.GetYByPrice(zone.Top, false);
                var y2 = ChartInfo.GetYByPrice(zone.Bottom, false);

                x1 = Math.Max(x1, region.Left);
                x2 = Math.Min(x2, region.Right);
                if (x2 <= x1 || Math.Max(y1, y2) < region.Top || Math.Min(y1, y2) > region.Bottom)
                    continue;

                var baseColor = ZoneColor(zone);
                var rect = new Rectangle(x1, Math.Min(y1, y2), x2 - x1, Math.Max(2, Math.Abs(y2 - y1)));

                if (zone.IsHtf)
                {
                    // HTF zones are frames, not fills — they outline confluence
                    // without stacking paint over the LTF zones inside them.
                    // Same 1px weight as LTF borders; the gold hue alone marks HTF.
                    context.DrawRectangle(new RenderPen(Color.FromArgb(220, HtfBorderColor), 1), rect);
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
                    context.DrawRectangle(new RenderPen(Color.FromArgb(160, baseColor), 1), rect);

                    // Midline (consequent encroachment) when there is room.
                    if (zone.State != ZoneState.Mitigated && rect.Width >= 14 && rect.Height >= 8)
                    {
                        var midY = ChartInfo.GetYByPrice(zone.Mid, false);
                        if (midY > region.Top && midY < region.Bottom)
                            context.DrawLine(new RenderPen(Color.FromArgb(70, baseColor), 1, DashStyle.Dot),
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
        private void DrawCenteredZoneLabel(RenderContext context, Zone zone, Rectangle rect, Color baseColor)
        {
            if (zone.State == ZoneState.Mitigated)
                return;

            var size = context.MeasureString(zone.Tag, ZoneFont);
            if (rect.Width < size.Width + 10 || rect.Height < size.Height + 4)
                return;

            var textX = rect.Left + (rect.Width - size.Width) / 2;
            var textY = rect.Top + (rect.Height - size.Height) / 2;

            var labelColor = zone.IsHtf ? HtfBorderColor : baseColor;
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

        private Color ZoneColor(Zone zone) => zone.Type switch
        {
            ZoneType.BullOrderBlock => BullObColor,
            ZoneType.BearOrderBlock => BearObColor,
            ZoneType.BullFvg => BullFvgColor,
            ZoneType.BearFvg => BearFvgColor,
            ZoneType.BullIfvg => BullIfvgColor,
            _ => BearIfvgColor
        };

        #endregion

        #region Liquidity

        private void RenderLiquidity(RenderContext context, Rectangle region, int lastBar)
        {
            // Liquidity lines track price like zones do: unswept levels stop one
            // bar past the live candle instead of running to the screen edge.
            var xLast = ChartInfo.GetXByBar(lastBar, false);
            var barWidth = lastBar > 0 ? Math.Max(2, xLast - ChartInfo.GetXByBar(lastBar - 1, false)) : 4;
            var xLive = Math.Min(region.Right, xLast + barWidth * 2);

            foreach (var level in _liquidity)
            {
                // Swept liquidity is history — it fades out after a short window.
                if (level.Swept && level.SweptBar.HasValue &&
                    DisplayMode == DisplayMode.Clean &&
                    lastBar - level.SweptBar.Value > SweptRetentionBars)
                    continue;

                var y = ChartInfo.GetYByPrice(level.Price, false);
                if (y < region.Top || y > region.Bottom)
                    continue;

                var x1 = Math.Max(ChartInfo.GetXByBar(level.StartBar, false), region.Left);
                var x2 = level.Swept && level.SweptBar.HasValue
                    ? Math.Min(ChartInfo.GetXByBar(level.SweptBar.Value, false), region.Right)
                    : xLive;

                if (x2 <= x1)
                    continue;

                var color = level.BuySide ? BslColor : SslColor;
                var alpha = level.Swept ? 70 : 170;
                var width = level.IsEqual ? 2 : 1;
                var pen = new RenderPen(Color.FromArgb(alpha, color), width, DashStyle.Dash);

                if (!level.Swept)
                {
                    var label = level.BuySide
                        ? (level.IsEqual ? "EQH · BSL" : "BSL")
                        : (level.IsEqual ? "EQL · SSL" : "SSL");
                    DrawLabeledLine(context, pen, label, Color.FromArgb(220, color), x1, x2, y, region);
                }
                else
                {
                    // Only genuine sweeps (trap: closed back inside) are labeled —
                    // runs stay unlabeled, per standard ICT chart presentation.
                    var label = level.WasTrap == true ? "Sweep" : null;
                    DrawLabeledLine(context, pen, label, Color.FromArgb(200, color), x1, x2, y, region);
                }
            }
        }

        #endregion

        #region Structure

        private void RenderStructure(RenderContext context, Rectangle region)
        {
            // Only the most recent events matter live; old structure is implied
            // by the zones it produced.
            var recent = _structure.Count > MaxStructureLabels
                ? _structure.Skip(_structure.Count - MaxStructureLabels)
                : _structure;

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

                // `---- BoS ----` / `---- MSS ----`: dashed line split around the
                // bare centered label. MSS keeps a heavier, brighter stroke.
                var color = evt.Bullish ? BullStructureColor : BearStructureColor;
                var pen = new RenderPen(Color.FromArgb(evt.IsMss ? 230 : 150, color),
                    evt.IsMss ? 2 : 1, DashStyle.Dash);

                DrawLabeledLine(context, pen, evt.IsMss ? "MSS" : "BoS",
                    Color.FromArgb(240, color), x1, x2, y, region);
            }
        }

        #endregion

        #region Premium / Discount

        private void RenderPremiumDiscount(RenderContext context, Rectangle region, int lastBar)
        {
            // Same ordered dealing range the entry filter uses — the drawing on the
            // chart and the PD gate in the signal engine can never disagree.
            var range = GetDealingRange();
            if (!range.HasValue)
                return;

            var high = range.Value.High.Price;
            var low = range.Value.Low.Price;
            var eq = (high + low) / 2m;

            var anchorBar = Math.Min(range.Value.High.Bar, range.Value.Low.Bar);
            var x1 = Math.Max(ChartInfo.GetXByBar(anchorBar, false), region.Left);
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

            context.DrawLine(new RenderPen(Color.FromArgb(150, EquilibriumColor), 1, DashStyle.DashDot),
                x1, yEq, x2, yEq);
            context.DrawString("EQ 50%", ZoneFont, Color.FromArgb(180, EquilibriumColor), x1 + 3, yEq + 2);
        }

        #endregion
    }
}
