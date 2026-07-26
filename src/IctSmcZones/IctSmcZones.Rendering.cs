using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace IctSmc
{
    public partial class IctSmcZones
    {
        private static readonly RenderFont ZoneFont = new("Segoe UI", 8f);
        private static readonly RenderFont StructureFont = new("Segoe UI", 8f, FontStyle.Bold);

        private const int ActiveZoneAlpha = 46;
        private const int TouchedZoneAlpha = 30;
        private const int MitigatedZoneAlpha = 14;
        private const int HtfExtraAlpha = 22;
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
            var size = context.MeasureString(text, ZoneFont);
            var rect = new Rectangle(region.Left + 8, region.Top + 8, size.Width + 12, size.Height + 6);

            context.FillRectangle(Color.FromArgb(150, 20, 20, 20), rect);
            context.DrawRectangle(new RenderPen(Color.FromArgb(90, Color.White)), rect);
            context.DrawString(text, ZoneFont, Color.FromArgb(235, Color.White), rect.X + 6, rect.Y + 3);
        }

        #region Zones

        private void RenderZones(RenderContext context, Rectangle region, int lastBar)
        {
            foreach (var zone in _zones)
            {
                if (zone.IsOrderBlock && !ShowOb)
                    continue;
                if (!zone.IsOrderBlock && !ShowFvg)
                    continue;
                if (zone.State == ZoneState.Mitigated && !ShowMitigated)
                    continue;

                var endBar = zone.EndBar ?? lastBar;

                var x1 = ChartInfo.GetXByBar(zone.StartBar, false);
                var x2 = zone.EndBar.HasValue
                    ? ChartInfo.GetXByBar(endBar, false)
                    : region.Right; // live zones stretch to the right edge — responsive as bars shift

                var y1 = ChartInfo.GetYByPrice(zone.Top, false);
                var y2 = ChartInfo.GetYByPrice(zone.Bottom, false);

                // Clip to the visible chart region.
                x1 = Math.Max(x1, region.Left);
                x2 = Math.Min(x2, region.Right);
                if (x2 <= x1 || y2 < region.Top || y1 > region.Bottom)
                    continue;

                var baseColor = ZoneColor(zone);
                var alpha = zone.State switch
                {
                    ZoneState.Mitigated => MitigatedZoneAlpha,
                    ZoneState.Touched => TouchedZoneAlpha,
                    _ => ActiveZoneAlpha
                };

                if (zone.IsHtf && zone.State != ZoneState.Mitigated)
                    alpha += HtfExtraAlpha;

                var rect = new Rectangle(x1, Math.Min(y1, y2), x2 - x1, Math.Max(1, Math.Abs(y2 - y1)));

                context.FillRectangle(Color.FromArgb(alpha, baseColor), rect);

                var borderWidth = zone.IsHtf ? 2 : 1;
                var borderPen = new RenderPen(Color.FromArgb(zone.State == ZoneState.Mitigated ? 60 : 160, baseColor), borderWidth);
                context.DrawRectangle(borderPen, rect);

                // Midline (consequent encroachment) for active zones.
                if (zone.State != ZoneState.Mitigated)
                {
                    var midY = ChartInfo.GetYByPrice(zone.Mid, false);
                    if (midY > region.Top && midY < region.Bottom)
                    {
                        var midPen = new RenderPen(Color.FromArgb(70, baseColor), 1, DashStyle.Dot);
                        context.DrawLine(midPen, x1, midY, x2, midY);
                    }
                }

                // Label inside the left edge of the zone.
                if (rect.Height >= 10 && zone.State != ZoneState.Mitigated)
                {
                    var labelColor = Color.FromArgb(210, baseColor);
                    context.DrawString(zone.Tag, ZoneFont, labelColor, x1 + 3, rect.Top + 1);
                }
            }
        }

        private Color ZoneColor(Zone zone) => zone.Type switch
        {
            ZoneType.BullOrderBlock => BullObColor,
            ZoneType.BearOrderBlock => BearObColor,
            ZoneType.BullFvg => BullFvgColor,
            _ => BearFvgColor
        };

        #endregion

        #region Liquidity

        private void RenderLiquidity(RenderContext context, Rectangle region, int lastBar)
        {
            foreach (var level in _liquidity)
            {
                var y = ChartInfo.GetYByPrice(level.Price, false);
                if (y < region.Top || y > region.Bottom)
                    continue;

                var x1 = Math.Max(ChartInfo.GetXByBar(level.StartBar, false), region.Left);
                var x2 = level.Swept && level.SweptBar.HasValue
                    ? Math.Min(ChartInfo.GetXByBar(level.SweptBar.Value, false), region.Right)
                    : region.Right;

                if (x2 <= x1)
                    continue;

                var color = level.BuySide ? BslColor : SslColor;
                var alpha = level.Swept ? 70 : 170;
                var width = level.IsEqual ? 2 : 1;
                var pen = new RenderPen(Color.FromArgb(alpha, color), width, DashStyle.Dash);

                context.DrawLine(pen, x1, y, x2, y);

                var label = level.BuySide
                    ? (level.IsEqual ? "EQH · BSL" : "BSL")
                    : (level.IsEqual ? "EQL · SSL" : "SSL");

                if (!level.Swept)
                {
                    context.DrawString(label, ZoneFont, Color.FromArgb(200, color), x1 + 3,
                        level.BuySide ? y - 13 : y + 2);
                }
                else if (level.SweptBar.HasValue)
                {
                    // Mark the exact sweep with an ✕ and note whether it trapped traders.
                    var sweepTag = level.WasTrap == true ? "✕ sweep" : "✕ run";
                    context.DrawString(sweepTag, StructureFont, Color.FromArgb(200, color),
                        x2 - 2, level.BuySide ? y - 14 : y + 2);
                }
            }
        }

        #endregion

        #region Structure

        private void RenderStructure(RenderContext context, Rectangle region)
        {
            foreach (var evt in _structure)
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

                var color = evt.Bullish ? BullStructureColor : BearStructureColor;
                var pen = new RenderPen(Color.FromArgb(evt.IsMss ? 220 : 140, color), evt.IsMss ? 2 : 1,
                    evt.IsMss ? DashStyle.Solid : DashStyle.Dash);

                context.DrawLine(pen, x1, y, x2, y);

                var label = evt.IsMss ? "MSS" : "BoS";
                var size = context.MeasureString(label, StructureFont);
                var textY = evt.Bullish ? y - size.Height - 1 : y + 2;
                context.DrawString(label, StructureFont, Color.FromArgb(230, color), x2 - size.Width, textY);
            }
        }

        #endregion

        #region Premium / Discount

        private void RenderPremiumDiscount(RenderContext context, Rectangle region, int lastBar)
        {
            if (_lastSwingHigh == null || _lastSwingLow == null)
                return;

            var high = _lastSwingHigh.Price;
            var low = _lastSwingLow.Price;
            if (high <= low)
                return;

            var eq = (high + low) / 2m;

            var anchorBar = Math.Min(_lastSwingHigh.Bar, _lastSwingLow.Bar);
            var x1 = Math.Max(ChartInfo.GetXByBar(anchorBar, false), region.Left);
            var x2 = region.Right;
            if (x2 <= x1)
                return;

            var yHigh = Math.Max(ChartInfo.GetYByPrice(high, false), region.Top);
            var yEq = ChartInfo.GetYByPrice(eq, false);
            var yLow = Math.Min(ChartInfo.GetYByPrice(low, false), region.Bottom);

            if (yEq > region.Top && yEq < region.Bottom)
            {
                // Premium shading (upper half) and discount shading (lower half).
                if (yEq > yHigh)
                    context.FillRectangle(Color.FromArgb(PdShadeAlpha, PremiumColor),
                        new Rectangle(x1, yHigh, x2 - x1, yEq - yHigh));

                if (yLow > yEq)
                    context.FillRectangle(Color.FromArgb(PdShadeAlpha, DiscountColor),
                        new Rectangle(x1, yEq, x2 - x1, yLow - yEq));

                var eqPen = new RenderPen(Color.FromArgb(150, EquilibriumColor), 1, DashStyle.DashDot);
                context.DrawLine(eqPen, x1, yEq, x2, yEq);
                context.DrawString("EQ 50%", ZoneFont, Color.FromArgb(180, EquilibriumColor), x1 + 3, yEq + 2);
            }
        }

        #endregion
    }
}
