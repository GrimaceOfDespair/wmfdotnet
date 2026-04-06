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
        /// Renders the WMF content to an NGraphics canvas.
        /// </summary>
        /// <param name="canvas">The target canvas to draw on.</param>
        /// <param name="width">Canvas width in pixels.</param>
        /// <param name="height">Canvas height in pixels.</param>
        public void Render(ICanvas canvas, int width, int height)
        {
            if (canvas == null) throw new ArgumentNullException(nameof(canvas));

            // Track drawing state from WMF records
            int windowOrgX = 0, windowOrgY = 0;
            int windowExtX = width, windowExtY = height;
            NGraphics.Color backgroundColor = Colors.White;
            var polyFillMode = WmfPolyFillMode.Alternate;

            // Fill background
            canvas.FillRectangle(new Rect(0, 0, width, height), backgroundColor);

            // Default pen and brush
            Pen currentPen = new Pen(Colors.Black, 1);
            Brush currentBrush = new SolidBrush(Colors.Black);

            foreach (var record in _wmf.Records)
            {
                switch (record.Function)
                {
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

                    case WmfFunc.SetBkColor:
                        if (record.Params is WmfColorRef bgColor)
                        {
                            backgroundColor = new NGraphics.Color(
                                bgColor.Red / 255.0,
                                bgColor.Green / 255.0,
                                bgColor.Blue / 255.0);
                            canvas.FillRectangle(new Rect(0, 0, width, height), backgroundColor);
                        }
                        break;

                    case WmfFunc.Polygon:
                        if (record.Params is WmfParamsPolygon polygon && polygon.Points.Count >= 2)
                        {
                            var pathOps = BuildPathOps(
                                polygon.Points,
                                windowOrgX, windowOrgY,
                                windowExtX, windowExtY,
                                width, height,
                                close: true);
                            canvas.FillPath(pathOps, currentBrush);
                            canvas.DrawPath(pathOps, currentPen);
                        }
                        break;

                    case WmfFunc.Polyline:
                        if (record.Params is WmfParamsPolyline polyline && polyline.Points.Count >= 2)
                        {
                            var pathOps = BuildPathOps(
                                polyline.Points,
                                windowOrgX, windowOrgY,
                                windowExtX, windowExtY,
                                width, height,
                                close: false);
                            canvas.DrawPath(pathOps, currentPen);
                        }
                        break;

                    case WmfFunc.SetPolyFillMode:
                        if (record.Params is WmfParamsSetPolyFillMode pfm)
                            polyFillMode = pfm.PolyFillMode;
                        break;

                    case WmfFunc.Eof:
                        return;
                }
            }
        }

        private static PathOp[] BuildPathOps(
            IList<WmfPointS> points,
            int orgX, int orgY,
            int extX, int extY,
            int canvasWidth, int canvasHeight,
            bool close)
        {
            var ops = new List<PathOp>(points.Count + (close ? 2 : 1));
            double scaleX = extX > 0 ? (double)canvasWidth / extX : 1.0;
            double scaleY = extY > 0 ? (double)canvasHeight / extY : 1.0;

            for (int i = 0; i < points.Count; i++)
            {
                double x = (points[i].X - orgX) * scaleX;
                double y = (points[i].Y - orgY) * scaleY;
                ops.Add(i == 0 ? new MoveTo(x, y) : (PathOp)new LineTo(x, y));
            }

            if (close)
                ops.Add(new ClosePath());

            return [.. ops];
        }
    }
}
