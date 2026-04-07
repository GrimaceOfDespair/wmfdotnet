using System;
using System.Collections.Generic;
using NGraphics;

namespace WmfDotNet
{
    /// <summary>
    /// Processes WMF records and renders them using NGraphics.
    /// </summary>
    public class WmfWriter
    {
        private readonly Wmf _wmf;

        public WmfWriter(Wmf wmf)
        {
            _wmf = wmf ?? throw new ArgumentNullException(nameof(wmf));
        }

        /// <summary>
        /// Returns a render size (width × height in pixels) appropriate for this WMF,
        /// scaled so the longer dimension does not exceed <paramref name="maxDimension"/>.
        /// </summary>
        public (int width, int height) GetRenderSize(int maxDimension = 500)
        {
            int extX = 0, extY = 0;
            foreach (var record in _wmf.Records)
            {
                if (record.Function == WmfFunc.SetWindowExt
                    && record.Params is WmfParamsSetWindowExt ext)
                {
                    extX = Math.Abs(ext.X);
                    extY = Math.Abs(ext.Y);
                }
            }

            if (extX <= 0 || extY <= 0)
                return (maxDimension, maxDimension);

            if (extX >= extY)
                return (maxDimension, Math.Max(1, (int)Math.Round((double)extY / extX * maxDimension)));
            else
                return (Math.Max(1, (int)Math.Round((double)extX / extY * maxDimension)), maxDimension);
        }

        /// <summary>
        /// Renders the WMF content to an NGraphics canvas.
        /// </summary>
        /// <param name="canvas">The target canvas to draw on.</param>
        /// <param name="width">Canvas width in pixels.</param>
        /// <param name="height">Canvas height in pixels.</param>
        public void Render(ICanvas canvas, int width, int height)
        {
            if (canvas == null) throw new ArgumentNullException(nameof(canvas));

            // ------- Drawing state -------
            int windowOrgX = 0, windowOrgY = 0;
            int windowExtX = width, windowExtY = height;
            NGraphics.Color backgroundColor = Colors.White;

            // GDI object table: holds pens and brushes created by CreateXxxIndirect
            int tableSize = Math.Max(8, (int)(_wmf.Header?.NumberOfObjects ?? 8));
            var objectTable = new GdiObject?[tableSize];

            // Default pen and brush (black solid)
            Pen currentPen = new Pen(Colors.Black, 1);
            Brush currentBrush = new SolidBrush(Colors.Black);

            // Current drawing position (for LineTo/MoveTo)
            double curX = 0, curY = 0;

            // Fill background
            canvas.FillRectangle(new Rect(0, 0, width, height), backgroundColor);

            foreach (var record in _wmf.Records)
            {
                switch (record.Function)
                {
                    // ---- Viewport / window ----
                    case WmfFunc.SetWindowOrg:
                        if (record.Params is WmfParamsSetWindowOrg org)
                        {
                            windowOrgX = org.X;
                            windowOrgY = org.Y;
                        }
                        break;

                    case WmfFunc.SetWindowExt:
                        if (record.Params is WmfParamsSetWindowExt ext)
                        {
                            windowExtX = ext.X != 0 ? Math.Abs(ext.X) : width;
                            windowExtY = ext.Y != 0 ? Math.Abs(ext.Y) : height;
                        }
                        break;

                    // ---- Background color ----
                    case WmfFunc.SetBkColor:
                        if (record.Params is WmfColorRef bgColor)
                        {
                            backgroundColor = ToNGraphicsColor(bgColor);
                            canvas.FillRectangle(new Rect(0, 0, width, height), backgroundColor);
                        }
                        break;

                    // ---- Object table management ----
                    case WmfFunc.CreateBrushIndirect:
                        if (record.Params is WmfParamsCreateBrushIndirect brush)
                        {
                            int slot = FindFreeSlot(objectTable);
                            if (slot >= 0)
                                objectTable[slot] = new GdiObject(GdiObjectKind.Brush, brush);
                        }
                        break;

                    case WmfFunc.CreatePenIndirect:
                        if (record.Params is WmfParamsCreatePenIndirect pen)
                        {
                            int slot = FindFreeSlot(objectTable);
                            if (slot >= 0)
                                objectTable[slot] = new GdiObject(GdiObjectKind.Pen, pen);
                        }
                        break;

                    case WmfFunc.SelectObject:
                        if (record.Params is WmfParamsSelectObject sel
                            && sel.ObjectIndex < objectTable.Length
                            && objectTable[sel.ObjectIndex] is GdiObject gdiObj)
                        {
                            if (gdiObj.Kind == GdiObjectKind.Brush)
                                currentBrush = MakeBrush((WmfParamsCreateBrushIndirect)gdiObj.Params);
                            else
                                currentPen = MakePen((WmfParamsCreatePenIndirect)gdiObj.Params);
                        }
                        break;

                    case WmfFunc.DeleteObject:
                        if (record.Params is WmfParamsDeleteObject del
                            && del.ObjectIndex < objectTable.Length)
                        {
                            objectTable[del.ObjectIndex] = null;
                        }
                        break;

                    // ---- Shapes ----
                    case WmfFunc.Polygon:
                        if (record.Params is WmfParamsPolygon polygon && polygon.Points.Count >= 2)
                        {
                            DrawPolygon(canvas, polygon.Points,
                                windowOrgX, windowOrgY, windowExtX, windowExtY, width, height,
                                currentPen, currentBrush);
                        }
                        break;

                    case WmfFunc.Polyline:
                        if (record.Params is WmfParamsPolyline polyline && polyline.Points.Count >= 2)
                        {
                            var ops = BuildPathOps(polyline.Points,
                                windowOrgX, windowOrgY, windowExtX, windowExtY, width, height,
                                close: false);
                            canvas.DrawPath(ops, currentPen);
                        }
                        break;

                    case WmfFunc.PolyPolygon:
                        if (record.Params is WmfParamsPolyPolygon polyPoly)
                        {
                            foreach (var pts in polyPoly.Polygons)
                            {
                                if (pts.Count >= 2)
                                {
                                    DrawPolygon(canvas, pts,
                                        windowOrgX, windowOrgY, windowExtX, windowExtY, width, height,
                                        currentPen, currentBrush);
                                }
                            }
                        }
                        break;

                    case WmfFunc.Rectangle:
                        if (record.Params is WmfParamsRectangle rect)
                        {
                            double rx = ToCanvasX(rect.Left, windowOrgX, windowExtX, width);
                            double ry = ToCanvasY(rect.Top, windowOrgY, windowExtY, height);
                            double rw = ToCanvasX(rect.Right, windowOrgX, windowExtX, width) - rx;
                            double rh = ToCanvasY(rect.Bottom, windowOrgY, windowExtY, height) - ry;
                            canvas.DrawRectangle(new Rect(rx, ry, rw, rh), Size.Zero, currentPen, currentBrush);
                        }
                        break;

                    case WmfFunc.Ellipse:
                        if (record.Params is WmfParamsEllipse ell)
                        {
                            double ex = ToCanvasX(ell.Left, windowOrgX, windowExtX, width);
                            double ey = ToCanvasY(ell.Top, windowOrgY, windowExtY, height);
                            double ew = ToCanvasX(ell.Right, windowOrgX, windowExtX, width) - ex;
                            double eh = ToCanvasY(ell.Bottom, windowOrgY, windowExtY, height) - ey;
                            canvas.DrawEllipse(new Rect(ex, ey, ew, eh), currentPen, currentBrush);
                        }
                        break;

                    case WmfFunc.MoveTo:
                        if (record.Params is WmfParamsMoveTo moveTo)
                        {
                            curX = ToCanvasX(moveTo.X, windowOrgX, windowExtX, width);
                            curY = ToCanvasY(moveTo.Y, windowOrgY, windowExtY, height);
                        }
                        break;

                    case WmfFunc.LineTo:
                        if (record.Params is WmfParamsLineTo lineTo)
                        {
                            double nx = ToCanvasX(lineTo.X, windowOrgX, windowExtX, width);
                            double ny = ToCanvasY(lineTo.Y, windowOrgY, windowExtY, height);
                            canvas.DrawPath(
                                [new MoveTo(curX, curY), new LineTo(nx, ny)],
                                currentPen);
                            curX = nx;
                            curY = ny;
                        }
                        break;

                    case WmfFunc.SetPolyFillMode:
                        // PolyFillMode affects even-odd vs winding fill; NGraphics ICanvas
                        // does not expose fill-rule control, so this record is acknowledged but
                        // the default (alternate/even-odd) fill is always used.
                        break;

                    case WmfFunc.Eof:
                        return;
                }
            }
        }

        // ---- Helpers ----

        private static void DrawPolygon(
            ICanvas canvas,
            IList<WmfPointS> points,
            int orgX, int orgY,
            int extX, int extY,
            int canvasW, int canvasH,
            Pen pen, Brush brush)
        {
            var ops = BuildPathOps(points, orgX, orgY, extX, extY, canvasW, canvasH, close: true);
            canvas.FillPath(ops, brush);
            canvas.DrawPath(ops, pen);
        }

        private static PathOp[] BuildPathOps(
            IList<WmfPointS> points,
            int orgX, int orgY,
            int extX, int extY,
            int canvasWidth, int canvasHeight,
            bool close)
        {
            var ops = new List<PathOp>(points.Count + (close ? 2 : 1));
            for (int i = 0; i < points.Count; i++)
            {
                double x = ToCanvasX(points[i].X, orgX, extX, canvasWidth);
                double y = ToCanvasY(points[i].Y, orgY, extY, canvasHeight);
                ops.Add(i == 0 ? new MoveTo(x, y) : (PathOp)new LineTo(x, y));
            }
            if (close)
                ops.Add(new ClosePath());
            return [.. ops];
        }

        private static double ToCanvasX(double logX, int orgX, int extX, int canvasWidth) =>
            (logX - orgX) * canvasWidth / (extX > 0 ? extX : canvasWidth);

        private static double ToCanvasY(double logY, int orgY, int extY, int canvasHeight) =>
            (logY - orgY) * canvasHeight / (extY > 0 ? extY : canvasHeight);

        private static int FindFreeSlot(GdiObject?[] table)
        {
            for (int i = 0; i < table.Length; i++)
                if (table[i] == null) return i;
            return -1;
        }

        private static Brush MakeBrush(WmfParamsCreateBrushIndirect b)
        {
            if (b.IsNull)
                return new SolidBrush(new NGraphics.Color(0, 0, 0, 0)); // transparent
            return new SolidBrush(ToNGraphicsColor(b.Color));
        }

        private static Pen MakePen(WmfParamsCreatePenIndirect p)
        {
            if (p.IsNull)
                return new Pen(new NGraphics.Color(0, 0, 0, 0), 0); // invisible
            double width = p.Width > 0 ? p.Width : 1.0;
            return new Pen(ToNGraphicsColor(p.Color), width);
        }

        private static NGraphics.Color ToNGraphicsColor(WmfColorRef c) =>
            new(c.Red / 255.0, c.Green / 255.0, c.Blue / 255.0);
    }

    // ---- Internal GDI object table types ----

    internal enum GdiObjectKind { Pen, Brush }

    internal sealed class GdiObject(GdiObjectKind kind, IWmfParams @params)
    {
        public GdiObjectKind Kind { get; } = kind;
        public IWmfParams Params { get; } = @params;
    }
}
