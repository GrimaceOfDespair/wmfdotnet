using System;
using System.Collections.Generic;
using System.IO;

namespace WmfDotNet
{
    /// <summary>
    /// Parser for EMF (Enhanced Metafile Format) vector images.
    /// </summary>
    public class Emf
    {
        private const uint EmrHeaderType = 0x00000001;
        private const uint EmfSignature = 0x464D4520; // " EMF"

        public EmfHeader Header { get; private set; } = null!;
        public List<EmfRecord> Records { get; private set; } = [];
        public bool ContainsEmfPlusRecords { get; private set; }

        public static Emf FromFile(string path)
        {
            using var stream = File.OpenRead(path);
            return new Emf(stream);
        }

        public Emf(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.Default, leaveOpen: true);
            Read(reader);
        }

        private void Read(BinaryReader reader)
        {
            var firstType = reader.ReadUInt32();
            if (firstType != EmrHeaderType)
                throw new InvalidDataException("Not an EMF file: first record is not EMR_HEADER.");

            var firstSize = reader.ReadUInt32();
            if (firstSize < 88)
                throw new InvalidDataException("Invalid EMF header record size.");

            var headerData = reader.ReadBytes((int)firstSize - 8);
            using (var hs = new MemoryStream(headerData))
            using (var hr = new BinaryReader(hs))
            {
                Header = EmfHeader.Read(hr, firstSize);
            }

            if (Header.Signature != EmfSignature)
                throw new InvalidDataException("Invalid EMF signature.");

            Records = [new EmfRecord { Function = EmfFunc.Header, Size = firstSize, Params = Header }];

            while (true)
            {
                var record = EmfRecord.Read(reader);
                Records.Add(record);

                if (record.Function == EmfFunc.GdiComment
                    && record.Params is EmfRawParams raw
                    && raw.Data.Length >= 8)
                {
                    // EMR_GDICOMMENT payload: cbData (u4) + data[cbData]
                    // EMF+ comments usually begin with 0x2B464D45 ("EMF+").
                    uint marker = BitConverter.ToUInt32(raw.Data, 4);
                    if (marker == 0x2B464D45)
                        ContainsEmfPlusRecords = true;
                }

                if (record.Function == EmfFunc.Eof)
                    break;
            }
        }
    }

    public class EmfHeader : IEmfParams
    {
        public EmfRectL Bounds { get; private set; } = null!;
        public EmfRectL FrameIn01Mm { get; private set; } = null!;
        public uint Signature { get; private set; }
        public uint Version { get; private set; }
        public uint Bytes { get; private set; }
        public uint RecordCount { get; private set; }
        public ushort HandleCount { get; private set; }
        public uint DescriptionLength { get; private set; }
        public uint DescriptionOffset { get; private set; }
        public uint PaletteEntries { get; private set; }
        public EmfSizeL DeviceSizePixels { get; private set; } = null!;
        public EmfSizeL DeviceSizeMillimeters { get; private set; } = null!;
        public uint PixelFormatSize { get; private set; }
        public uint PixelFormatOffset { get; private set; }
        public uint OpenGl { get; private set; }
        public EmfSizeL DeviceSizeMicrometers { get; private set; } = new EmfSizeL { X = 0, Y = 0 };

        internal static EmfHeader Read(BinaryReader reader, uint headerRecordSize)
        {
            var h = new EmfHeader
            {
                Bounds = EmfRectL.Read(reader),
                FrameIn01Mm = EmfRectL.Read(reader),
                Signature = reader.ReadUInt32(),
                Version = reader.ReadUInt32(),
                Bytes = reader.ReadUInt32(),
                RecordCount = reader.ReadUInt32(),
                HandleCount = reader.ReadUInt16(),
                _ = reader.ReadUInt16(), // reserved
                DescriptionLength = reader.ReadUInt32(),
                DescriptionOffset = reader.ReadUInt32(),
                PaletteEntries = reader.ReadUInt32(),
                DeviceSizePixels = EmfSizeL.Read(reader),
                DeviceSizeMillimeters = EmfSizeL.Read(reader)
            };

            // Present for 108-byte+ EMR_HEADER variants.
            if (headerRecordSize >= 108)
            {
                h.PixelFormatSize = reader.ReadUInt32();
                h.PixelFormatOffset = reader.ReadUInt32();
                h.OpenGl = reader.ReadUInt32();
                h.DeviceSizeMicrometers = EmfSizeL.Read(reader);
            }

            return h;
        }
    }

    public class EmfRecord
    {
        public uint Size { get; private set; }
        public EmfFunc Function { get; private set; }
        public IEmfParams? Params { get; private set; }

        internal static EmfRecord Read(BinaryReader reader)
        {
            var typeRaw = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            if (size < 8)
                throw new InvalidDataException("Invalid EMF record size.");

            var func = Enum.IsDefined(typeof(EmfFunc), (int)typeRaw)
                ? (EmfFunc)typeRaw
                : EmfFunc.Unknown;

            int paramBytes = (int)size - 8;
            byte[] raw = paramBytes > 0 ? reader.ReadBytes(paramBytes) : [];

            IEmfParams? @params = null;
            if (raw.Length > 0)
            {
                using var ms = new MemoryStream(raw);
                using var pr = new BinaryReader(ms);
                @params = func switch
                {
                    EmfFunc.SetWindowExtEx => EmfParamsSetSize.Read(pr),
                    EmfFunc.SetWindowOrgEx => EmfParamsSetPoint.Read(pr),
                    EmfFunc.SetViewportExtEx => EmfParamsSetSize.Read(pr),
                    EmfFunc.SetViewportOrgEx => EmfParamsSetPoint.Read(pr),
                    EmfFunc.SetBkColor => EmfColorRef.Read(pr),
                    EmfFunc.SetPolyFillMode => EmfParamsSetPolyFillMode.Read(pr),
                    EmfFunc.MoveToEx => EmfParamsSetPoint.Read(pr),
                    EmfFunc.LineTo => EmfParamsSetPoint.Read(pr),
                    EmfFunc.Polygon => EmfParamsPolygon.Read(pr),
                    EmfFunc.Polyline => EmfParamsPolyline.Read(pr),
                    EmfFunc.PolyPolygon => EmfParamsPolyPolygon.Read(pr),
                    EmfFunc.Polygon16 => EmfParamsPolygon16.Read(pr),
                    EmfFunc.Polyline16 => EmfParamsPolyline16.Read(pr),
                    EmfFunc.Rectangle => EmfParamsRect.Read(pr),
                    EmfFunc.Ellipse => EmfParamsRect.Read(pr),
                    EmfFunc.RoundRect => EmfParamsRoundRect.Read(pr),
                    EmfFunc.CreatePen => EmfParamsCreatePen.Read(pr),
                    EmfFunc.CreateBrushIndirect => EmfParamsCreateBrushIndirect.Read(pr),
                    EmfFunc.SelectObject => EmfParamsSelectObject.Read(pr),
                    EmfFunc.DeleteObject => EmfParamsDeleteObject.Read(pr),
                    EmfFunc.SetWorldTransform => EmfParamsSetWorldTransform.Read(pr),
                    EmfFunc.ModifyWorldTransform => EmfParamsModifyWorldTransform.Read(pr),
                    _ => new EmfRawParams(raw)
                };
            }

            return new EmfRecord { Size = size, Function = func, Params = @params };
        }
    }

    public interface IEmfParams { }

    public class EmfRawParams(byte[] data) : IEmfParams
    {
        public byte[] Data { get; } = data;
    }

    public class EmfPointL
    {
        public int X { get; set; }
        public int Y { get; set; }

        internal static EmfPointL Read(BinaryReader reader) => new() { X = reader.ReadInt32(), Y = reader.ReadInt32() };
    }

    public class EmfPointS
    {
        public short X { get; set; }
        public short Y { get; set; }

        internal static EmfPointS Read(BinaryReader reader) => new() { X = reader.ReadInt16(), Y = reader.ReadInt16() };
    }

    public class EmfRectL
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }

        internal static EmfRectL Read(BinaryReader reader) =>
            new()
            {
                Left = reader.ReadInt32(),
                Top = reader.ReadInt32(),
                Right = reader.ReadInt32(),
                Bottom = reader.ReadInt32()
            };
    }

    public class EmfSizeL : IEmfParams
    {
        public int X { get; set; }
        public int Y { get; set; }

        internal static EmfSizeL Read(BinaryReader reader) => new() { X = reader.ReadInt32(), Y = reader.ReadInt32() };
    }

    public class EmfColorRef : IEmfParams
    {
        public byte Red { get; private set; }
        public byte Green { get; private set; }
        public byte Blue { get; private set; }
        public byte Reserved { get; private set; }

        internal static EmfColorRef Read(BinaryReader reader)
        {
            return new EmfColorRef
            {
                Red = reader.ReadByte(),
                Green = reader.ReadByte(),
                Blue = reader.ReadByte(),
                Reserved = reader.ReadByte()
            };
        }
    }

    public class EmfParamsSetSize : IEmfParams
    {
        public int X { get; private set; }
        public int Y { get; private set; }

        internal static EmfParamsSetSize Read(BinaryReader reader) =>
            new() { X = reader.ReadInt32(), Y = reader.ReadInt32() };
    }

    public class EmfParamsSetPoint : IEmfParams
    {
        public int X { get; private set; }
        public int Y { get; private set; }

        internal static EmfParamsSetPoint Read(BinaryReader reader) =>
            new() { X = reader.ReadInt32(), Y = reader.ReadInt32() };
    }

    public class EmfParamsSetPolyFillMode : IEmfParams
    {
        public EmfPolyFillMode PolyFillMode { get; private set; }

        internal static EmfParamsSetPolyFillMode Read(BinaryReader reader) =>
            new() { PolyFillMode = (EmfPolyFillMode)reader.ReadUInt32() };
    }

    public class EmfParamsPolygon : IEmfParams
    {
        public EmfRectL Bounds { get; private set; } = null!;
        public uint Count { get; private set; }
        public List<EmfPointL> Points { get; private set; } = [];

        internal static EmfParamsPolygon Read(BinaryReader reader)
        {
            var p = new EmfParamsPolygon { Bounds = EmfRectL.Read(reader), Count = reader.ReadUInt32() };
            for (int i = 0; i < p.Count; i++)
                p.Points.Add(EmfPointL.Read(reader));
            return p;
        }
    }

    public class EmfParamsPolyline : IEmfParams
    {
        public EmfRectL Bounds { get; private set; } = null!;
        public uint Count { get; private set; }
        public List<EmfPointL> Points { get; private set; } = [];

        internal static EmfParamsPolyline Read(BinaryReader reader)
        {
            var p = new EmfParamsPolyline { Bounds = EmfRectL.Read(reader), Count = reader.ReadUInt32() };
            for (int i = 0; i < p.Count; i++)
                p.Points.Add(EmfPointL.Read(reader));
            return p;
        }
    }

    public class EmfParamsPolyPolygon : IEmfParams
    {
        public EmfRectL Bounds { get; private set; } = null!;
        public uint NumberOfPolygons { get; private set; }
        public uint TotalPoints { get; private set; }
        public List<List<EmfPointL>> Polygons { get; private set; } = [];

        internal static EmfParamsPolyPolygon Read(BinaryReader reader)
        {
            var p = new EmfParamsPolyPolygon
            {
                Bounds = EmfRectL.Read(reader),
                NumberOfPolygons = reader.ReadUInt32(),
                TotalPoints = reader.ReadUInt32()
            };

            var counts = new uint[p.NumberOfPolygons];
            for (int i = 0; i < p.NumberOfPolygons; i++)
                counts[i] = reader.ReadUInt32();

            for (int i = 0; i < p.NumberOfPolygons; i++)
            {
                var polygon = new List<EmfPointL>((int)counts[i]);
                for (int j = 0; j < counts[i]; j++)
                    polygon.Add(EmfPointL.Read(reader));
                p.Polygons.Add(polygon);
            }
            return p;
        }
    }

    public class EmfParamsPolygon16 : IEmfParams
    {
        public EmfRectL Bounds { get; private set; } = null!;
        public uint Count { get; private set; }
        public List<EmfPointS> Points { get; private set; } = [];

        internal static EmfParamsPolygon16 Read(BinaryReader reader)
        {
            var p = new EmfParamsPolygon16 { Bounds = EmfRectL.Read(reader), Count = reader.ReadUInt32() };
            for (int i = 0; i < p.Count; i++)
                p.Points.Add(EmfPointS.Read(reader));
            return p;
        }
    }

    public class EmfParamsPolyline16 : IEmfParams
    {
        public EmfRectL Bounds { get; private set; } = null!;
        public uint Count { get; private set; }
        public List<EmfPointS> Points { get; private set; } = [];

        internal static EmfParamsPolyline16 Read(BinaryReader reader)
        {
            var p = new EmfParamsPolyline16 { Bounds = EmfRectL.Read(reader), Count = reader.ReadUInt32() };
            for (int i = 0; i < p.Count; i++)
                p.Points.Add(EmfPointS.Read(reader));
            return p;
        }
    }

    public class EmfParamsRect : IEmfParams
    {
        public EmfRectL Rect { get; private set; } = null!;

        internal static EmfParamsRect Read(BinaryReader reader) => new() { Rect = EmfRectL.Read(reader) };
    }

    public class EmfParamsRoundRect : IEmfParams
    {
        public EmfRectL Rect { get; private set; } = null!;
        public EmfSizeL Corner { get; private set; } = null!;

        internal static EmfParamsRoundRect Read(BinaryReader reader) =>
            new() { Rect = EmfRectL.Read(reader), Corner = EmfSizeL.Read(reader) };
    }

    public class EmfParamsCreatePen : IEmfParams
    {
        public uint HandleIndex { get; private set; }
        public uint PenStyle { get; private set; }
        public int Width { get; private set; }
        public EmfColorRef Color { get; private set; } = null!;

        public bool IsNull => (PenStyle & 0x0000000F) == 5; // PS_NULL

        internal static EmfParamsCreatePen Read(BinaryReader reader)
        {
            var handle = reader.ReadUInt32();
            var style = reader.ReadUInt32();
            var width = reader.ReadInt32();
            _ = reader.ReadInt32(); // y component of width point
            var color = EmfColorRef.Read(reader);

            return new EmfParamsCreatePen
            {
                HandleIndex = handle,
                PenStyle = style,
                Width = width,
                Color = color
            };
        }
    }

    public class EmfParamsCreateBrushIndirect : IEmfParams
    {
        public uint HandleIndex { get; private set; }
        public uint BrushStyle { get; private set; }
        public EmfColorRef Color { get; private set; } = null!;
        public uint Hatch { get; private set; }

        public bool IsNull => BrushStyle == 1; // BS_NULL

        internal static EmfParamsCreateBrushIndirect Read(BinaryReader reader)
        {
            return new EmfParamsCreateBrushIndirect
            {
                HandleIndex = reader.ReadUInt32(),
                BrushStyle = reader.ReadUInt32(),
                Color = EmfColorRef.Read(reader),
                Hatch = reader.ReadUInt32()
            };
        }
    }

    public class EmfParamsSelectObject : IEmfParams
    {
        public uint ObjectIndex { get; private set; }

        internal static EmfParamsSelectObject Read(BinaryReader reader) =>
            new() { ObjectIndex = reader.ReadUInt32() };
    }

    public class EmfParamsDeleteObject : IEmfParams
    {
        public uint ObjectIndex { get; private set; }

        internal static EmfParamsDeleteObject Read(BinaryReader reader) =>
            new() { ObjectIndex = reader.ReadUInt32() };
    }

    public class EmfXForm
    {
        public float M11 { get; private set; }
        public float M12 { get; private set; }
        public float M21 { get; private set; }
        public float M22 { get; private set; }
        public float Dx { get; private set; }
        public float Dy { get; private set; }

        internal static EmfXForm Read(BinaryReader reader) =>
            new()
            {
                M11 = reader.ReadSingle(),
                M12 = reader.ReadSingle(),
                M21 = reader.ReadSingle(),
                M22 = reader.ReadSingle(),
                Dx = reader.ReadSingle(),
                Dy = reader.ReadSingle()
            };
    }

    public class EmfParamsSetWorldTransform : IEmfParams
    {
        public EmfXForm XForm { get; private set; } = null!;

        internal static EmfParamsSetWorldTransform Read(BinaryReader reader) =>
            new() { XForm = EmfXForm.Read(reader) };
    }

    public class EmfParamsModifyWorldTransform : IEmfParams
    {
        public EmfXForm XForm { get; private set; } = null!;
        public EmfModifyWorldTransformMode Mode { get; private set; }

        internal static EmfParamsModifyWorldTransform Read(BinaryReader reader) =>
            new() { XForm = EmfXForm.Read(reader), Mode = (EmfModifyWorldTransformMode)reader.ReadUInt32() };
    }

    public enum EmfFunc : int
    {
        Unknown = -1,
        Header = 1,
        PolyBezier = 2,
        Polygon = 3,
        Polyline = 4,
        PolyBezierTo = 5,
        PolylineTo = 6,
        PolyPolyline = 7,
        PolyPolygon = 8,
        SetWindowExtEx = 9,
        SetWindowOrgEx = 10,
        SetViewportExtEx = 11,
        SetViewportOrgEx = 12,
        SetBrushOrgEx = 13,
        Eof = 14,
        SetPixelV = 15,
        SetMapperFlags = 16,
        SetMapMode = 17,
        SetBkMode = 18,
        SetPolyFillMode = 19,
        SetRop2 = 20,
        SetStretchBltMode = 21,
        SetTextAlign = 22,
        SetColorAdjustment = 23,
        SetTextColor = 24,
        SetBkColor = 25,
        OffsetClipRgn = 26,
        MoveToEx = 27,
        SetMetaRgn = 28,
        ExcludeClipRect = 29,
        IntersectClipRect = 30,
        ScaleViewportExtEx = 31,
        ScaleWindowExtEx = 32,
        SaveDc = 33,
        RestoreDc = 34,
        SetWorldTransform = 35,
        ModifyWorldTransform = 36,
        SelectObject = 37,
        CreatePen = 38,
        CreateBrushIndirect = 39,
        DeleteObject = 40,
        AngleArc = 41,
        Ellipse = 42,
        Rectangle = 43,
        RoundRect = 44,
        Arc = 45,
        Chord = 46,
        Pie = 47,
        SelectPalette = 48,
        CreatePalette = 49,
        SetPaletteEntries = 50,
        ResizePalette = 51,
        RealizePalette = 52,
        ExtFloodFill = 53,
        LineTo = 54,
        ArcTo = 55,
        PolyDraw = 56,
        SetArcDirection = 57,
        SetMiterLimit = 58,
        BeginPath = 59,
        EndPath = 60,
        CloseFigure = 61,
        FillPath = 62,
        StrokeAndFillPath = 63,
        StrokePath = 64,
        FlattenPath = 65,
        WidenPath = 66,
        SelectClipPath = 67,
        AbortPath = 68,
        GdiComment = 70,
        FillRgn = 71,
        FrameRgn = 72,
        InvertRgn = 73,
        PaintRgn = 74,
        ExtSelectClipRgn = 75,
        BitBlt = 76,
        StretchBlt = 77,
        MaskBlt = 78,
        PlgBlt = 79,
        SetDibitsToDevice = 80,
        StretchDibits = 81,
        ExtCreateFontIndirectW = 82,
        ExtTextOutA = 83,
        ExtTextOutW = 84,
        PolyBezier16 = 85,
        Polygon16 = 86,
        Polyline16 = 87,
        PolyBezierTo16 = 88,
        PolylineTo16 = 89,
        PolyPolyline16 = 90,
        PolyPolygon16 = 91,
        PolyDraw16 = 92,
        CreateMonoBrush = 93,
        CreateDibPatternBrushPt = 94,
        ExtCreatePen = 95,
        PolyTextOutA = 96,
        PolyTextOutW = 97
    }

    public enum EmfPolyFillMode : uint
    {
        Alternate = 1,
        Winding = 2
    }

    public enum EmfModifyWorldTransformMode : uint
    {
        Identity = 1,
        LeftMultiply = 2,
        RightMultiply = 3
    }
}
