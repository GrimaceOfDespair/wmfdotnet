using System;
using System.Collections.Generic;
using NGraphics;

namespace WmfDotNet
{
    /// <summary>
    /// Processes EMF records and renders them using NGraphics.
    /// </summary>
    public class EmfWriter
    {
        private readonly Emf _emf;
        private static readonly Color Transparent = new(0, 0, 0, 0);

        public EmfWriter(Emf emf)
        {
            _emf = emf ?? throw new ArgumentNullException(nameof(emf));
        }

        public (int width, int height) GetRenderSize(int maxDimension = 500)
        {
            int extX = Math.Abs(_emf.Header.Bounds.Right - _emf.Header.Bounds.Left);
            int extY = Math.Abs(_emf.Header.Bounds.Bottom - _emf.Header.Bounds.Top);

            if (extX <= 0 || extY <= 0)
                return (maxDimension, maxDimension);

            if (extX >= extY)
                return (maxDimension, Math.Max(1, (int)Math.Round((double)extY / extX * maxDimension)));
            else
                return (Math.Max(1, (int)Math.Round((double)extX / extY * maxDimension)), maxDimension);
        }

        public void Render(ICanvas canvas, int width, int height)
        {
            if (canvas == null) throw new ArgumentNullException(nameof(canvas));

            int windowOrgX = _emf.Header.Bounds.Left;
            int windowOrgY = _emf.Header.Bounds.Top;
            int windowExtX = Math.Max(1, Math.Abs(_emf.Header.Bounds.Right - _emf.Header.Bounds.Left));
            int windowExtY = Math.Max(1, Math.Abs(_emf.Header.Bounds.Bottom - _emf.Header.Bounds.Top));

            int viewportOrgX = 0, viewportOrgY = 0;
            int viewportExtX = windowExtX;
            int viewportExtY = windowExtY;

            Color backgroundColor = Colors.White;
            var world = Affine.Identity;

            var gdiObjectTable = new Dictionary<uint, EmfGdiObject>();

            Pen currentPen = new Pen(Colors.Black, 1);
            Brush currentBrush = new SolidBrush(Colors.Black);
            Color currentTextColor = Colors.Black;
            uint currentTextAlign = 0;
            EmfParamsExtCreateFontIndirectW? currentFont = null;
            double curX = 0, curY = 0;

            canvas.FillRectangle(new Rect(0, 0, width, height), backgroundColor);

            foreach (var record in _emf.Records)
            {
                switch (record.Function)
                {
                    case EmfFunc.SetWindowOrgEx:
                        if (record.Params is EmfParamsSetPoint wOrg)
                        {
                            windowOrgX = wOrg.X;
                            windowOrgY = wOrg.Y;
                        }
                        break;

                    case EmfFunc.SetWindowExtEx:
                        if (record.Params is EmfParamsSetSize wExt)
                        {
                            windowExtX = wExt.X != 0 ? Math.Abs(wExt.X) : windowExtX;
                            windowExtY = wExt.Y != 0 ? Math.Abs(wExt.Y) : windowExtY;
                        }
                        break;

                    case EmfFunc.SetViewportOrgEx:
                        if (record.Params is EmfParamsSetPoint vOrg)
                        {
                            viewportOrgX = vOrg.X;
                            viewportOrgY = vOrg.Y;
                        }
                        break;

                    case EmfFunc.SetViewportExtEx:
                        if (record.Params is EmfParamsSetSize vExt)
                        {
                            viewportExtX = vExt.X != 0 ? Math.Abs(vExt.X) : viewportExtX;
                            viewportExtY = vExt.Y != 0 ? Math.Abs(vExt.Y) : viewportExtY;
                        }
                        break;

                    case EmfFunc.SetWorldTransform:
                        if (record.Params is EmfParamsSetWorldTransform swt)
                            world = Affine.FromXForm(swt.XForm);
                        break;

                    case EmfFunc.ModifyWorldTransform:
                        if (record.Params is EmfParamsModifyWorldTransform mwt)
                        {
                            var m = Affine.FromXForm(mwt.XForm);
                            world = mwt.Mode switch
                            {
                                EmfModifyWorldTransformMode.Identity => Affine.Identity,
                                EmfModifyWorldTransformMode.LeftMultiply => Affine.Multiply(m, world),
                                EmfModifyWorldTransformMode.RightMultiply => Affine.Multiply(world, m),
                                _ => world
                            };
                        }
                        break;

                    case EmfFunc.SetBkColor:
                        if (record.Params is EmfColorRef bgColor)
                            backgroundColor = ToNGraphicsColor(bgColor);
                        break;

                    case EmfFunc.SetTextColor:
                        if (record.Params is EmfColorRef textColor)
                            currentTextColor = ToNGraphicsColor(textColor);
                        break;

                    case EmfFunc.SetTextAlign:
                        if (record.Params is EmfParamsSetTextAlign textAlign)
                            currentTextAlign = textAlign.TextAlignmentMode;
                        break;

                    case EmfFunc.CreateBrushIndirect:
                        if (record.Params is EmfParamsCreateBrushIndirect brush)
                        {
                            gdiObjectTable[brush.HandleIndex] = new EmfGdiObject(EmfGdiObjectKind.Brush, brush);
                        }
                        break;

                    case EmfFunc.CreatePen:
                        if (record.Params is EmfParamsCreatePen pen)
                        {
                            gdiObjectTable[pen.HandleIndex] = new EmfGdiObject(EmfGdiObjectKind.Pen, pen);
                        }
                        break;

                    case EmfFunc.ExtCreateFontIndirectW:
                        if (record.Params is EmfParamsExtCreateFontIndirectW font)
                        {
                            gdiObjectTable[font.HandleIndex] = new EmfGdiObject(EmfGdiObjectKind.Font, font);
                        }
                        break;

                    case EmfFunc.SelectObject:
                        if (record.Params is EmfParamsSelectObject sel)
                        {
                            if (TrySelectStockObject(sel.ObjectIndex, ref currentPen, ref currentBrush))
                                break;

                            if (gdiObjectTable.TryGetValue(sel.ObjectIndex, out var obj))
                            {
                                if (obj.Kind == EmfGdiObjectKind.Pen)
                                    currentPen = MakePen((EmfParamsCreatePen)obj.Params);
                                else if (obj.Kind == EmfGdiObjectKind.Brush)
                                    currentBrush = MakeBrush((EmfParamsCreateBrushIndirect)obj.Params);
                                else
                                    currentFont = (EmfParamsExtCreateFontIndirectW)obj.Params;
                            }
                        }
                        break;

                    case EmfFunc.DeleteObject:
                        if (record.Params is EmfParamsDeleteObject del)
                            gdiObjectTable.Remove(del.ObjectIndex);
                        break;

                    case EmfFunc.Polygon:
                        if (record.Params is EmfParamsPolygon polygon && polygon.Points.Count >= 2)
                        {
                            DrawPolygon(canvas, polygon.Points, world,
                                windowOrgX, windowOrgY, windowExtX, windowExtY,
                                viewportOrgX, viewportOrgY, viewportExtX, viewportExtY,
                                width, height, currentPen, currentBrush);
                        }
                        break;

                    case EmfFunc.Polyline:
                        if (record.Params is EmfParamsPolyline polyline && polyline.Points.Count >= 2)
                        {
                            var ops = BuildPathOps(polyline.Points, world,
                                windowOrgX, windowOrgY, windowExtX, windowExtY,
                                viewportOrgX, viewportOrgY, viewportExtX, viewportExtY,
                                width, height, close: false);
                            canvas.DrawPath(ops, currentPen);
                        }
                        break;

                    case EmfFunc.PolyPolygon:
                        if (record.Params is EmfParamsPolyPolygon polyPoly)
                        {
                            foreach (var pts in polyPoly.Polygons)
                            {
                                if (pts.Count >= 2)
                                {
                                    DrawPolygon(canvas, pts, world,
                                        windowOrgX, windowOrgY, windowExtX, windowExtY,
                                        viewportOrgX, viewportOrgY, viewportExtX, viewportExtY,
                                        width, height, currentPen, currentBrush);
                                }
                            }
                        }
                        break;

                    case EmfFunc.Polygon16:
                        if (record.Params is EmfParamsPolygon16 polygon16 && polygon16.Points.Count >= 2)
                        {
                            DrawPolygon(canvas, ToPointL(polygon16.Points), world,
                                windowOrgX, windowOrgY, windowExtX, windowExtY,
                                viewportOrgX, viewportOrgY, viewportExtX, viewportExtY,
                                width, height, currentPen, currentBrush);
                        }
                        break;

                    case EmfFunc.Polyline16:
                        if (record.Params is EmfParamsPolyline16 polyline16 && polyline16.Points.Count >= 2)
                        {
                            var ops = BuildPathOps(ToPointL(polyline16.Points), world,
                                windowOrgX, windowOrgY, windowExtX, windowExtY,
                                viewportOrgX, viewportOrgY, viewportExtX, viewportExtY,
                                width, height, close: false);
                            canvas.DrawPath(ops, currentPen);
                        }
                        break;

                    case EmfFunc.Rectangle:
                    case EmfFunc.Ellipse:
                        if (record.Params is EmfParamsRect rect)
                        {
                            var (x1, y1) = MapPoint(rect.Rect.Left, rect.Rect.Top, world,
                                windowOrgX, windowOrgY, windowExtX, windowExtY,
                                viewportOrgX, viewportOrgY, viewportExtX, viewportExtY,
                                width, height);
                            var (x2, y2) = MapPoint(rect.Rect.Right, rect.Rect.Bottom, world,
                                windowOrgX, windowOrgY, windowExtX, windowExtY,
                                viewportOrgX, viewportOrgY, viewportExtX, viewportExtY,
                                width, height);

                            var rx = Math.Min(x1, x2);
                            var ry = Math.Min(y1, y2);
                            var rw = Math.Abs(x2 - x1);
                            var rh = Math.Abs(y2 - y1);
                            var r = new Rect(rx, ry, rw, rh);
                            if (record.Function == EmfFunc.Rectangle)
                                canvas.DrawRectangle(r, Size.Zero, currentPen, currentBrush);
                            else
                                canvas.DrawEllipse(r, currentPen, currentBrush);
                        }
                        break;

                    case EmfFunc.RoundRect:
                        if (record.Params is EmfParamsRoundRect roundRect)
                        {
                            var (x1, y1) = MapPoint(roundRect.Rect.Left, roundRect.Rect.Top, world,
                                windowOrgX, windowOrgY, windowExtX, windowExtY,
                                viewportOrgX, viewportOrgY, viewportExtX, viewportExtY,
                                width, height);
                            var (x2, y2) = MapPoint(roundRect.Rect.Right, roundRect.Rect.Bottom, world,
                                windowOrgX, windowOrgY, windowExtX, windowExtY,
                                viewportOrgX, viewportOrgY, viewportExtX, viewportExtY,
                                width, height);

                            var rx = Math.Min(x1, x2);
                            var ry = Math.Min(y1, y2);
                            var rw = Math.Abs(x2 - x1);
                            var rh = Math.Abs(y2 - y1);
                            var cornerRadius = new Size(Math.Abs(roundRect.Corner.X), Math.Abs(roundRect.Corner.Y));
                            canvas.DrawRectangle(new Rect(rx, ry, rw, rh), cornerRadius, currentPen, currentBrush);
                        }
                        break;

                    case EmfFunc.MoveToEx:
                        if (record.Params is EmfParamsSetPoint moveTo)
                        {
                            (curX, curY) = MapPoint(moveTo.X, moveTo.Y, world,
                                windowOrgX, windowOrgY, windowExtX, windowExtY,
                                viewportOrgX, viewportOrgY, viewportExtX, viewportExtY,
                                width, height);
                        }
                        break;

                    case EmfFunc.LineTo:
                        if (record.Params is EmfParamsSetPoint lineTo)
                        {
                            var (nx, ny) = MapPoint(lineTo.X, lineTo.Y, world,
                                windowOrgX, windowOrgY, windowExtX, windowExtY,
                                viewportOrgX, viewportOrgY, viewportExtX, viewportExtY,
                                width, height);
                            canvas.DrawPath([new MoveTo(curX, curY), new LineTo(nx, ny)], currentPen);
                            curX = nx;
                            curY = ny;
                        }
                        break;

                    case EmfFunc.ExtTextOutW:
                        if (record.Params is EmfParamsExtTextOutW textOut && !string.IsNullOrWhiteSpace(textOut.Text))
                        {
                            DrawText(canvas, textOut, currentFont, currentTextColor, currentTextAlign, world,
                                windowOrgX, windowOrgY, windowExtX, windowExtY,
                                viewportOrgX, viewportOrgY, viewportExtX, viewportExtY,
                                width, height);
                        }
                        break;

                    case EmfFunc.SetPolyFillMode:
                        break;

                    case EmfFunc.Eof:
                        return;
                }
            }
        }

        private static void DrawPolygon(
            ICanvas canvas,
            IList<EmfPointL> points,
            Affine world,
            int wOrgX, int wOrgY, int wExtX, int wExtY,
            int vOrgX, int vOrgY, int vExtX, int vExtY,
            int canvasW, int canvasH,
            Pen pen, Brush brush)
        {
            var ops = BuildPathOps(points, world, wOrgX, wOrgY, wExtX, wExtY, vOrgX, vOrgY, vExtX, vExtY, canvasW, canvasH, close: true);
            canvas.FillPath(ops, brush);
            canvas.DrawPath(ops, pen);
        }

        private static PathOp[] BuildPathOps(
            IList<EmfPointL> points,
            Affine world,
            int wOrgX, int wOrgY, int wExtX, int wExtY,
            int vOrgX, int vOrgY, int vExtX, int vExtY,
            int canvasW, int canvasH,
            bool close)
        {
            var ops = new List<PathOp>(points.Count + (close ? 1 : 0));
            for (int i = 0; i < points.Count; i++)
            {
                var (x, y) = MapPoint(points[i].X, points[i].Y, world, wOrgX, wOrgY, wExtX, wExtY, vOrgX, vOrgY, vExtX, vExtY, canvasW, canvasH);
                ops.Add(i == 0 ? new MoveTo(x, y) : (PathOp)new LineTo(x, y));
            }
            if (close)
                ops.Add(new ClosePath());
            return [.. ops];
        }

        private static (double x, double y) MapPoint(
            double logicalX, double logicalY,
            Affine world,
            int wOrgX, int wOrgY, int wExtX, int wExtY,
            int vOrgX, int vOrgY, int vExtX, int vExtY,
            int canvasW, int canvasH)
        {
            var (wx, wy) = world.Transform(logicalX, logicalY);

            // Window -> viewport (page transform)
            double dx = vOrgX + (wx - wOrgX) * (vExtX != 0 ? (double)vExtX / wExtX : 1.0);
            double dy = vOrgY + (wy - wOrgY) * (vExtY != 0 ? (double)vExtY / wExtY : 1.0);

            // Device -> canvas
            double cx = dx * canvasW / (vExtX != 0 ? Math.Abs(vExtX) : canvasW);
            double cy = dy * canvasH / (vExtY != 0 ? Math.Abs(vExtY) : canvasH);
            return (cx, cy);
        }

        private static List<EmfPointL> ToPointL(IList<EmfPointS> points)
        {
            var list = new List<EmfPointL>(points.Count);
            for (int i = 0; i < points.Count; i++)
                list.Add(new EmfPointL { X = points[i].X, Y = points[i].Y });
            return list;
        }

        private static bool TrySelectStockObject(uint objectIndex, ref Pen pen, ref Brush brush)
        {
            // Stock object constants from wingdi.h.
            switch (objectIndex)
            {
                case 0x80000000: // WHITE_BRUSH
                    brush = new SolidBrush(Colors.White);
                    return true;
                case 0x80000004: // BLACK_BRUSH
                    brush = new SolidBrush(Colors.Black);
                    return true;
                case 0x80000005: // NULL_BRUSH
                    brush = new SolidBrush(Transparent);
                    return true;
                case 0x80000006: // WHITE_PEN
                    pen = new Pen(Colors.White, 1);
                    return true;
                case 0x80000007: // BLACK_PEN
                    pen = new Pen(Colors.Black, 1);
                    return true;
                case 0x80000008: // NULL_PEN
                    pen = new Pen(Transparent, 0);
                    return true;
                default:
                    return false;
            }

            private static void DrawText(
                ICanvas canvas,
                EmfParamsExtTextOutW textOut,
                EmfParamsExtCreateFontIndirectW? fontParams,
                Color textColor,
                uint textAlign,
                Affine world,
                int wOrgX, int wOrgY, int wExtX, int wExtY,
                int vOrgX, int vOrgY, int vExtX, int vExtY,
                int canvasW, int canvasH)
            {
                var (x, y) = MapPoint(textOut.Reference.X, textOut.Reference.Y, world,
                    wOrgX, wOrgY, wExtX, wExtY, vOrgX, vOrgY, vExtX, vExtY, canvasW, canvasH);

                var font = MakeFont(fontParams, world, wOrgX, wOrgY, wExtX, wExtY, vOrgX, vOrgY, vExtX, vExtY, canvasW, canvasH);
                var fontSize = Math.Max(1.0, font.Size);

                var alignment = ToTextAlignment(textAlign);
                var (frameX, frameWidth) = alignment switch
                {
                    TextAlignment.Right => (0.0, Math.Max(1.0, x)),
                    TextAlignment.Center => (x - canvasW / 2.0, Math.Max(1.0, canvasW)),
                    _ => (x, Math.Max(1.0, canvasW - x))
                };

                var frameY = UsesTopAlignment(textAlign) ? y : y - fontSize;
                canvas.DrawText(textOut.Text, new Rect(frameX, frameY, frameWidth, fontSize), font, alignment, null, new SolidBrush(textColor));
            }

            private static Font MakeFont(
                EmfParamsExtCreateFontIndirectW? fontParams,
                Affine world,
                int wOrgX, int wOrgY, int wExtX, int wExtY,
                int vOrgX, int vOrgY, int vExtX, int vExtY,
                int canvasW, int canvasH)
            {
                if (fontParams == null)
                    return new Font("Arial", 12.0);

                var logicalHeight = Math.Abs(fontParams.Height);
                if (logicalHeight <= 0)
                    logicalHeight = 12;

                var (x0, y0) = MapPoint(0, 0, world, wOrgX, wOrgY, wExtX, wExtY, vOrgX, vOrgY, vExtX, vExtY, canvasW, canvasH);
                var (x1, y1) = MapPoint(0, logicalHeight, world, wOrgX, wOrgY, wExtX, wExtY, vOrgX, vOrgY, vExtX, vExtY, canvasW, canvasH);
                var size = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                size = Math.Max(1.0, size);

                var style = FontStyle.Normal;
                if (fontParams.Weight >= 600)
                    style |= FontStyle.Bold;
                if (fontParams.Italic)
                    style |= FontStyle.Italic;

                var family = string.IsNullOrWhiteSpace(fontParams.FaceName) ? "Arial" : fontParams.FaceName;
                return new Font(family, size, style);
            }

            private static TextAlignment ToTextAlignment(uint textAlign)
            {
                // TA_CENTER=0x0006, TA_RIGHT=0x0002, TA_LEFT=0x0000.
                if ((textAlign & 0x0006) == 0x0006)
                    return TextAlignment.Center;
                if ((textAlign & 0x0002) != 0)
                    return TextAlignment.Right;
                return TextAlignment.Left;
            }

            private static bool UsesTopAlignment(uint textAlign)
            {
                // TA_BASELINE=0x0018 and TA_BOTTOM=0x0008 use a baseline/bottom reference point.
                if ((textAlign & 0x0018) == 0x0018)
                    return false;
                if ((textAlign & 0x0008) != 0)
                    return false;
                return true;
            }
        }

        private static Brush MakeBrush(EmfParamsCreateBrushIndirect brush)
        {
            if (brush.IsNull)
                return new SolidBrush(Transparent);
            return new SolidBrush(ToNGraphicsColor(brush.Color));
        }

        private static Pen MakePen(EmfParamsCreatePen pen)
        {
            if (pen.IsNull)
                return new Pen(Transparent, 0);
            double width = pen.Width > 0 ? pen.Width : 1.0;
            return new Pen(ToNGraphicsColor(pen.Color), width);
        }

        private static Color ToNGraphicsColor(EmfColorRef c) =>
            new(c.Red / 255.0, c.Green / 255.0, c.Blue / 255.0);
    }

    internal enum EmfGdiObjectKind { Pen, Brush, Font }

    internal sealed class EmfGdiObject(EmfGdiObjectKind kind, IEmfParams @params)
    {
        public EmfGdiObjectKind Kind { get; } = kind;
        public IEmfParams Params { get; } = @params;
    }

    internal readonly struct Affine(double m11, double m12, double m21, double m22, double dx, double dy)
    {
        public static Affine Identity => new(1, 0, 0, 1, 0, 0);

        public static Affine FromXForm(EmfXForm xf) => new(xf.M11, xf.M12, xf.M21, xf.M22, xf.Dx, xf.Dy);

        public static Affine Multiply(Affine a, Affine b)
        {
            return new Affine(
                a.M11 * b.M11 + a.M12 * b.M21,
                a.M11 * b.M12 + a.M12 * b.M22,
                a.M21 * b.M11 + a.M22 * b.M21,
                a.M21 * b.M12 + a.M22 * b.M22,
                a.M11 * b.Dx + a.M12 * b.Dy + a.Dx,
                a.M21 * b.Dx + a.M22 * b.Dy + a.Dy);
        }

        private double M11 { get; } = m11;
        private double M12 { get; } = m12;
        private double M21 { get; } = m21;
        private double M22 { get; } = m22;
        private double Dx { get; } = dx;
        private double Dy { get; } = dy;

        public (double x, double y) Transform(double x, double y) =>
            (M11 * x + M12 * y + Dx, M21 * x + M22 * y + Dy);
    }
}
