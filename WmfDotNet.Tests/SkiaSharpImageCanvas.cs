using System;
using System.Collections.Generic;
using NGraphics;
using SkiaSharp;

namespace WmfDotNet.Tests
{
    /// <summary>
    /// Cross-platform NGraphics IImageCanvas implementation using SkiaSharp.
    /// Enables rendering WMF content to PNG images on any platform.
    /// </summary>
    internal sealed class SkiaSharpImageCanvas : IImageCanvas, IDisposable
    {
        private readonly SKBitmap _bitmap;
        private readonly SKCanvas _canvas;
        private readonly int _width;
        private readonly int _height;

        public Size Size => new(_width, _height);
        public double Scale => 1.0;

        public SkiaSharpImageCanvas(int width, int height)
        {
            _width = width;
            _height = height;
            _bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            _canvas = new SKCanvas(_bitmap);
            _canvas.Clear(SKColors.White);
        }

        public IImage GetImage()
        {
            _canvas.Flush();
            using var image = SKImage.FromBitmap(_bitmap);
            var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return new SkiaSharpImage(data.ToArray());
        }

        public void SaveState() => _canvas.Save();

        public void RestoreState() => _canvas.Restore();

        public void Transform(NGraphics.Transform transform)
        {
            var m = new SKMatrix
            {
                ScaleX = (float)transform.A,
                SkewY = (float)transform.B,
                SkewX = (float)transform.C,
                ScaleY = (float)transform.D,
                TransX = (float)transform.E,
                TransY = (float)transform.F,
                Persp2 = 1
            };
            _canvas.Concat(m);
        }

        public TextMetrics MeasureText(string text, Font font) => new();

        public void DrawText(string text, Rect frame, Font font, TextAlignment alignment, Pen? pen, Brush? brush)
        {
            if (brush == null) return;
            float fontSize = (float)(font?.Size ?? 12.0);
            var familyName = font?.Family ?? font?.Name;
            using var typeface = string.IsNullOrWhiteSpace(familyName)
                ? SKTypeface.Default
                : SKTypeface.FromFamilyName(familyName) ?? SKTypeface.Default;
            using var skFont = new SKFont(typeface, fontSize);
            using var paint = new SKPaint();
            ApplyBrushToFill(brush, paint);
            paint.Style = SKPaintStyle.Fill;
            paint.IsAntialias = true;

            skFont.GetFontMetrics(out var metrics);

            float x = (float)frame.X;
            if (alignment != TextAlignment.Left)
            {
                float textWidth = skFont.MeasureText(text, paint);
                float widthDiff = Math.Max(0, (float)frame.Width - textWidth);
                if (alignment == TextAlignment.Center)
                    x += widthDiff / 2f;
                else if (alignment == TextAlignment.Right)
                    x += widthDiff;
            }

            // Convert the frame's top edge into Skia's baseline-based text position.
            float y = (float)frame.Y - metrics.Ascent;
            using var textPath = skFont.GetTextPath(text, new SKPoint(x, y));
            if (textPath.IsEmpty)
            {
                _canvas.DrawText(text, x, y, skFont, paint);
                return;
            }

            var totalMatrix = _canvas.TotalMatrix;
            textPath.Transform(totalMatrix);

            _canvas.Save();
            _canvas.ResetMatrix();
            _canvas.DrawPath(textPath, paint);
            _canvas.Restore();
        }

        public void DrawPath(IEnumerable<PathOp> ops, Pen? pen, Brush? brush)
        {
            using var path = BuildSkPath(ops);

            if (brush != null)
            {
                using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
                ApplyBrushToFill(brush, fillPaint);
                _canvas.DrawPath(path, fillPaint);
            }

            if (pen != null)
            {
                using var strokePaint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = Math.Max((float)pen.Width, 1),
                    Color = ToSKColor(pen.Color)
                };
                _canvas.DrawPath(path, strokePaint);
            }
        }

        public void DrawRectangle(Rect frame, Size corner, Pen? pen, Brush? brush)
        {
            var rect = ToSKRect(frame);

            if (brush != null)
            {
                using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
                ApplyBrushToFill(brush, fillPaint);
                if (corner.Width > 0 || corner.Height > 0)
                    _canvas.DrawRoundRect(rect, (float)corner.Width, (float)corner.Height, fillPaint);
                else
                    _canvas.DrawRect(rect, fillPaint);
            }

            if (pen != null)
            {
                using var strokePaint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = Math.Max((float)pen.Width, 1),
                    Color = ToSKColor(pen.Color)
                };
                if (corner.Width > 0 || corner.Height > 0)
                    _canvas.DrawRoundRect(rect, (float)corner.Width, (float)corner.Height, strokePaint);
                else
                    _canvas.DrawRect(rect, strokePaint);
            }
        }

        public void DrawEllipse(Rect frame, Pen? pen, Brush? brush)
        {
            var oval = ToSKRect(frame);

            if (brush != null)
            {
                using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
                ApplyBrushToFill(brush, fillPaint);
                _canvas.DrawOval(oval, fillPaint);
            }

            if (pen != null)
            {
                using var strokePaint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = Math.Max((float)pen.Width, 1),
                    Color = ToSKColor(pen.Color)
                };
                _canvas.DrawOval(oval, strokePaint);
            }
        }

        public void DrawImage(IImage image, Rect frame, double alpha)
        {
            if (image is not SkiaSharpImage skImg) return;
            using var bmp = SKBitmap.Decode(skImg.PngData);
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(alpha * 255)) };
            _canvas.DrawBitmap(bmp, ToSKRect(frame), paint);
        }

        private static SKPath BuildSkPath(IEnumerable<PathOp> ops)
        {
            var path = new SKPath();
            foreach (var op in ops)
            {
                switch (op)
                {
                    case MoveTo m: path.MoveTo((float)m.Point.X, (float)m.Point.Y); break;
                    case LineTo l: path.LineTo((float)l.Point.X, (float)l.Point.Y); break;
                    case CurveTo c:
                        path.CubicTo(
                            (float)c.Control1.X, (float)c.Control1.Y,
                            (float)c.Control2.X, (float)c.Control2.Y,
                            (float)c.Point.X, (float)c.Point.Y);
                        break;
                    case ArcTo a:
                        // Approximate arc with a line for simplicity
                        path.LineTo((float)a.Point.X, (float)a.Point.Y);
                        break;
                    case ClosePath: path.Close(); break;
                }
            }
            return path;
        }

        private static void ApplyBrushToFill(Brush brush, SKPaint paint)
        {
            switch (brush)
            {
                case SolidBrush solid:
                    paint.Color = ToSKColor(solid.Color);
                    break;
                case LinearGradientBrush lgb when lgb.Stops.Count > 0:
                    paint.Color = ToSKColor(lgb.Stops[0].Color);
                    break;
                default:
                    paint.Color = SKColors.Black;
                    break;
            }
        }

        private static SKColor ToSKColor(NGraphics.Color c) =>
            new((byte)(c.Red * 255), (byte)(c.Green * 255), (byte)(c.Blue * 255), (byte)(c.Alpha * 255));

        private static SKRect ToSKRect(Rect r) =>
            new((float)r.X, (float)r.Y, (float)(r.X + r.Width), (float)(r.Y + r.Height));

        public void Dispose()
        {
            _canvas.Dispose();
            _bitmap.Dispose();
        }
    }

    internal sealed class SkiaSharpImage(byte[] pngData) : IImage
    {
        public byte[] PngData { get; } = pngData;

        public void SaveAsPng(string path) => File.WriteAllBytes(path, PngData);

        public void SaveAsPng(System.IO.Stream stream) => stream.Write(PngData, 0, PngData.Length);

        public double Scale => 1.0;

        public Size Size
        {
            get
            {
                using var bmp = SKBitmap.Decode(PngData);
                return bmp != null ? new Size(bmp.Width, bmp.Height) : new Size(0, 0);
            }
        }
    }
}
