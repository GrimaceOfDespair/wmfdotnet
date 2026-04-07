using System;
using System.Collections.Generic;
using System.IO;

namespace WmfDotNet
{
    /// <summary>
    /// Parser for Windows Metafile (WMF) vector images.
    /// Based on the KaitaiStruct WMF format specification.
    /// </summary>
    public class Wmf
    {
        private static readonly byte[] PlaceableMagic = [0xD7, 0xCD, 0xC6, 0x9A];

        public WmfSpecialHeader? SpecialHeader { get; private set; }
        public WmfHeader Header { get; private set; } = null!;
        public List<WmfRecord> Records { get; private set; } = [];

        public static Wmf FromFile(string path)
        {
            using var stream = File.OpenRead(path);
            return new Wmf(stream);
        }

        public Wmf(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.Default, leaveOpen: true);
            Read(reader);
        }

        private void Read(BinaryReader reader)
        {
            // Check for placeable WMF magic header
            var magic = reader.ReadBytes(4);
            if (magic.Length == 4
                && magic[0] == PlaceableMagic[0]
                && magic[1] == PlaceableMagic[1]
                && magic[2] == PlaceableMagic[2]
                && magic[3] == PlaceableMagic[3])
            {
                SpecialHeader = WmfSpecialHeader.Read(reader);
            }
            else
            {
                // No placeable header — magic bytes are the first 4 bytes of the standard header:
                //   magic[0..1] = MetafileType (u2)
                //   magic[2..3] = HeaderSize   (u2)
                var metafileType = BitConverter.ToUInt16(magic, 0);
                var headerSize = BitConverter.ToUInt16(magic, 2);
                Header = WmfHeader.Read(reader, metafileType, headerSize);
            }

            if (SpecialHeader != null)
            {
                Header = WmfHeader.ReadFull(reader);
            }

            Records = [];
            while (true)
            {
                var record = WmfRecord.Read(reader);
                Records.Add(record);
                if (record.Function == WmfFunc.Eof)
                    break;
            }
        }
    }

    /// <summary>
    /// Placeable metafile special header (section 2.2.2.3 of WMF spec).
    /// </summary>
    public class WmfSpecialHeader
    {
        // Magic bytes already consumed by the parent parser
        public ushort Handle { get; private set; }
        public short Left { get; private set; }
        public short Top { get; private set; }
        public short Right { get; private set; }
        public short Bottom { get; private set; }
        public ushort Inch { get; private set; }
        public uint Reserved { get; private set; }
        public ushort Checksum { get; private set; }

        internal static WmfSpecialHeader Read(BinaryReader reader)
        {
            var h = new WmfSpecialHeader
            {
                Handle = reader.ReadUInt16(),
                Left = reader.ReadInt16(),
                Top = reader.ReadInt16(),
                Right = reader.ReadInt16(),
                Bottom = reader.ReadInt16(),
                Inch = reader.ReadUInt16(),
                Reserved = reader.ReadUInt32(),
                Checksum = reader.ReadUInt16()
            };
            return h;
        }
    }

    /// <summary>
    /// Standard WMF file header.
    /// </summary>
    public class WmfHeader
    {
        public WmfMetafileType MetafileType { get; private set; }
        public ushort HeaderSize { get; private set; }
        public ushort Version { get; private set; }
        public uint Size { get; private set; }
        public ushort NumberOfObjects { get; private set; }
        public uint MaxRecord { get; private set; }
        public ushort NumberOfMembers { get; private set; }

        internal static WmfHeader ReadFull(BinaryReader reader)
        {
            return new WmfHeader
            {
                MetafileType = (WmfMetafileType)reader.ReadUInt16(),
                HeaderSize = reader.ReadUInt16(),
                Version = reader.ReadUInt16(),
                Size = reader.ReadUInt32(),
                NumberOfObjects = reader.ReadUInt16(),
                MaxRecord = reader.ReadUInt32(),
                NumberOfMembers = reader.ReadUInt16()
            };
        }

        internal static WmfHeader Read(BinaryReader reader, ushort metafileType, ushort headerSize)
        {
            // MetafileType and HeaderSize have already been consumed by the caller
            return new WmfHeader
            {
                MetafileType = (WmfMetafileType)metafileType,
                HeaderSize = headerSize,
                Version = reader.ReadUInt16(),
                Size = reader.ReadUInt32(),
                NumberOfObjects = reader.ReadUInt16(),
                MaxRecord = reader.ReadUInt32(),
                NumberOfMembers = reader.ReadUInt16()
            };
        }
    }

    /// <summary>
    /// A single WMF record consisting of size, function code, and parameters.
    /// </summary>
    public class WmfRecord
    {
        /// <summary>Total record size in 16-bit words.</summary>
        public uint Size { get; private set; }
        public WmfFunc Function { get; private set; }
        public IWmfParams? Params { get; private set; }

        internal static WmfRecord Read(BinaryReader reader)
        {
            var size = reader.ReadUInt32();
            var funcRaw = reader.ReadUInt16();
            var func = Enum.IsDefined(typeof(WmfFunc), (int)funcRaw)
                ? (WmfFunc)funcRaw
                : WmfFunc.Unknown;

            int paramBytes = (int)((size - 3) * 2);
            byte[] raw = paramBytes > 0 ? reader.ReadBytes(paramBytes) : [];

            IWmfParams? @params = null;
            if (raw.Length > 0)
            {
                using var ms = new MemoryStream(raw);
                using var pr = new BinaryReader(ms);
                @params = func switch
                {
                    WmfFunc.Polyline => WmfParamsPolyline.Read(pr),
                    WmfFunc.Polygon => WmfParamsPolygon.Read(pr),
                    WmfFunc.PolyPolygon => WmfParamsPolyPolygon.Read(pr),
                    WmfFunc.SetBkColor => WmfColorRef.Read(pr),
                    WmfFunc.SetBkMode => WmfParamsSetBkMode.Read(pr),
                    WmfFunc.SetPolyFillMode => WmfParamsSetPolyFillMode.Read(pr),
                    WmfFunc.SetRop2 => WmfParamsSetRop2.Read(pr),
                    WmfFunc.SetWindowExt => WmfParamsSetWindowExt.Read(pr),
                    WmfFunc.SetWindowOrg => WmfParamsSetWindowOrg.Read(pr),
                    WmfFunc.CreateBrushIndirect => WmfParamsCreateBrushIndirect.Read(pr),
                    WmfFunc.CreatePenIndirect => WmfParamsCreatePenIndirect.Read(pr),
                    WmfFunc.SelectObject => WmfParamsSelectObject.Read(pr),
                    WmfFunc.DeleteObject => WmfParamsDeleteObject.Read(pr),
                    WmfFunc.Rectangle => WmfParamsRectangle.Read(pr),
                    WmfFunc.Ellipse => WmfParamsEllipse.Read(pr),
                    WmfFunc.LineTo => WmfParamsLineTo.Read(pr),
                    WmfFunc.MoveTo => WmfParamsMoveTo.Read(pr),
                    _ => new WmfRawParams(raw)
                };
            }

            return new WmfRecord { Size = size, Function = func, Params = @params };
        }
    }

    public interface IWmfParams { }

    public class WmfRawParams(byte[] data) : IWmfParams
    {
        public byte[] Data { get; } = data;
    }

    public class WmfParamsPolyline : IWmfParams
    {
        public short NumPoints { get; private set; }
        public List<WmfPointS> Points { get; private set; } = [];

        internal static WmfParamsPolyline Read(BinaryReader reader)
        {
            var p = new WmfParamsPolyline { NumPoints = reader.ReadInt16() };
            for (int i = 0; i < p.NumPoints; i++)
                p.Points.Add(WmfPointS.Read(reader));
            return p;
        }
    }

    public class WmfParamsPolygon : IWmfParams
    {
        public short NumPoints { get; private set; }
        public List<WmfPointS> Points { get; private set; } = [];

        internal static WmfParamsPolygon Read(BinaryReader reader)
        {
            var p = new WmfParamsPolygon { NumPoints = reader.ReadInt16() };
            for (int i = 0; i < p.NumPoints; i++)
                p.Points.Add(WmfPointS.Read(reader));
            return p;
        }
    }

    public class WmfColorRef : IWmfParams
    {
        public byte Red { get; private set; }
        public byte Green { get; private set; }
        public byte Blue { get; private set; }
        public byte Reserved { get; private set; }

        internal static WmfColorRef Read(BinaryReader reader)
        {
            return new WmfColorRef
            {
                Red = reader.ReadByte(),
                Green = reader.ReadByte(),
                Blue = reader.ReadByte(),
                Reserved = reader.ReadByte()
            };
        }
    }

    public class WmfParamsSetBkMode : IWmfParams
    {
        public WmfMixMode BkMode { get; private set; }

        internal static WmfParamsSetBkMode Read(BinaryReader reader) =>
            new() { BkMode = (WmfMixMode)reader.ReadUInt16() };
    }

    public class WmfParamsSetPolyFillMode : IWmfParams
    {
        public WmfPolyFillMode PolyFillMode { get; private set; }

        internal static WmfParamsSetPolyFillMode Read(BinaryReader reader) =>
            new() { PolyFillMode = (WmfPolyFillMode)reader.ReadUInt16() };
    }

    public class WmfParamsSetRop2 : IWmfParams
    {
        public WmfBinRasterOp DrawMode { get; private set; }

        internal static WmfParamsSetRop2 Read(BinaryReader reader) =>
            new() { DrawMode = (WmfBinRasterOp)reader.ReadUInt16() };
    }

    public class WmfParamsSetWindowExt : IWmfParams
    {
        /// <summary>Vertical extent of the window in logical units.</summary>
        public short Y { get; private set; }
        /// <summary>Horizontal extent of the window in logical units.</summary>
        public short X { get; private set; }

        internal static WmfParamsSetWindowExt Read(BinaryReader reader) =>
            new() { Y = reader.ReadInt16(), X = reader.ReadInt16() };
    }

    public class WmfParamsSetWindowOrg : IWmfParams
    {
        /// <summary>Y coordinate of the window origin in logical units.</summary>
        public short Y { get; private set; }
        /// <summary>X coordinate of the window origin in logical units.</summary>
        public short X { get; private set; }

        internal static WmfParamsSetWindowOrg Read(BinaryReader reader) =>
            new() { Y = reader.ReadInt16(), X = reader.ReadInt16() };
    }

    public class WmfPointS
    {
        public short X { get; private set; }
        public short Y { get; private set; }

        internal static WmfPointS Read(BinaryReader reader) =>
            new() { X = reader.ReadInt16(), Y = reader.ReadInt16() };
    }

    /// <summary>
    /// Multiple polygons in a single record (section 2.3.3.15 of WMF spec).
    /// </summary>
    public class WmfParamsPolyPolygon : IWmfParams
    {
        public ushort NumPolygons { get; private set; }
        public List<List<WmfPointS>> Polygons { get; private set; } = [];

        internal static WmfParamsPolyPolygon Read(BinaryReader reader)
        {
            var p = new WmfParamsPolyPolygon { NumPolygons = reader.ReadUInt16() };
            var counts = new ushort[p.NumPolygons];
            for (int i = 0; i < p.NumPolygons; i++)
                counts[i] = reader.ReadUInt16();
            for (int i = 0; i < p.NumPolygons; i++)
            {
                var polygon = new List<WmfPointS>(counts[i]);
                for (int j = 0; j < counts[i]; j++)
                    polygon.Add(WmfPointS.Read(reader));
                p.Polygons.Add(polygon);
            }
            return p;
        }
    }

    /// <summary>
    /// Defines a brush by style and color. The brush fills closed shapes.
    /// Common styles: 0=solid, 1=null/hollow, 2=hatched. See WMF spec section 2.1.1.4.
    /// </summary>
    public class WmfParamsCreateBrushIndirect : IWmfParams
    {
        /// <summary>0=solid, 1=null/hollow, 2=hatched</summary>
        public ushort BrushStyle { get; private set; }
        public WmfColorRef Color { get; private set; } = null!;
        public ushort BrushHatch { get; private set; }

        public bool IsNull => BrushStyle == 1;

        internal static WmfParamsCreateBrushIndirect Read(BinaryReader reader)
        {
            return new WmfParamsCreateBrushIndirect
            {
                BrushStyle = reader.ReadUInt16(),
                Color = WmfColorRef.Read(reader),
                BrushHatch = reader.ReadUInt16()
            };
        }
    }

    /// <summary>
    /// Defines a pen by style, width, and color. The pen strokes open and closed shapes.
    /// Common styles: 0=solid, 1=dash, 2=dot, 3=dashdot, 4=dashdotdot, 5=null (invisible), 6=insideframe.
    /// See WMF spec section 2.1.1.23.
    /// </summary>
    public class WmfParamsCreatePenIndirect : IWmfParams
    {
        /// <summary>0=solid, 1=dash, 2=dot, 3=dashdot, 4=dashdotdot, 5=null (invisible), 6=insideframe</summary>
        public ushort PenStyle { get; private set; }
        public short Width { get; private set; }
        public WmfColorRef Color { get; private set; } = null!;

        public bool IsNull => PenStyle == 5;

        internal static WmfParamsCreatePenIndirect Read(BinaryReader reader)
        {
            var style = reader.ReadUInt16();
            var wx = reader.ReadInt16();
            reader.ReadInt16(); // POINT.y — ignored per spec
            var color = WmfColorRef.Read(reader);
            return new WmfParamsCreatePenIndirect
            {
                PenStyle = style,
                Width = wx,
                Color = color
            };
        }
    }

    public class WmfParamsSelectObject : IWmfParams
    {
        public ushort ObjectIndex { get; private set; }

        internal static WmfParamsSelectObject Read(BinaryReader reader) =>
            new() { ObjectIndex = reader.ReadUInt16() };
    }

    public class WmfParamsDeleteObject : IWmfParams
    {
        public ushort ObjectIndex { get; private set; }

        internal static WmfParamsDeleteObject Read(BinaryReader reader) =>
            new() { ObjectIndex = reader.ReadUInt16() };
    }

    /// <summary>
    /// Draws a rectangle. Coordinates are specified bottom-right-top-left (per WMF spec).
    /// </summary>
    public class WmfParamsRectangle : IWmfParams
    {
        public short Bottom { get; private set; }
        public short Right { get; private set; }
        public short Top { get; private set; }
        public short Left { get; private set; }

        internal static WmfParamsRectangle Read(BinaryReader reader) =>
            new()
            {
                Bottom = reader.ReadInt16(),
                Right = reader.ReadInt16(),
                Top = reader.ReadInt16(),
                Left = reader.ReadInt16()
            };
    }

    /// <summary>
    /// Draws an ellipse within the bounding rectangle. Coordinates: bottom-right-top-left.
    /// </summary>
    public class WmfParamsEllipse : IWmfParams
    {
        public short Bottom { get; private set; }
        public short Right { get; private set; }
        public short Top { get; private set; }
        public short Left { get; private set; }

        internal static WmfParamsEllipse Read(BinaryReader reader) =>
            new()
            {
                Bottom = reader.ReadInt16(),
                Right = reader.ReadInt16(),
                Top = reader.ReadInt16(),
                Left = reader.ReadInt16()
            };
    }

    public class WmfParamsLineTo : IWmfParams
    {
        public short Y { get; private set; }
        public short X { get; private set; }

        internal static WmfParamsLineTo Read(BinaryReader reader) =>
            new() { Y = reader.ReadInt16(), X = reader.ReadInt16() };
    }

    public class WmfParamsMoveTo : IWmfParams
    {
        public short Y { get; private set; }
        public short X { get; private set; }

        internal static WmfParamsMoveTo Read(BinaryReader reader) =>
            new() { Y = reader.ReadInt16(), X = reader.ReadInt16() };
    }

    public enum WmfMetafileType : ushort
    {
        MemoryMetafile = 1,
        DiskMetafile = 2
    }

    public enum WmfFunc : int
    {
        Unknown = -1,
        Eof = 0x0000,
        RealizePalette = 0x0035,
        SetPalEntries = 0x0037,
        SetBkMode = 0x0102,
        SetMapMode = 0x0103,
        SetRop2 = 0x0104,
        SetRelAbs = 0x0105,
        SetPolyFillMode = 0x0106,
        SetStretchBltMode = 0x0107,
        SetTextCharExtra = 0x0108,
        RestoreDc = 0x0127,
        ResizePalette = 0x0139,
        DibCreatePatternBrush = 0x0142,
        SetLayout = 0x0149,
        SetBkColor = 0x0201,
        SetTextColor = 0x0209,
        OffsetViewportOrg = 0x0211,
        LineTo = 0x0213,
        MoveTo = 0x0214,
        OffsetClipRgn = 0x0220,
        FillRegion = 0x0228,
        SetMapperFlags = 0x0231,
        SelectPalette = 0x0234,
        Polygon = 0x0324,
        Polyline = 0x0325,
        SetTextJustification = 0x020A,
        SetWindowOrg = 0x020B,
        SetWindowExt = 0x020C,
        SetViewportOrg = 0x020D,
        SetViewportExt = 0x020E,
        OffsetWindowOrg = 0x020F,
        ScaleWindowExt = 0x0410,
        ScaleViewportExt = 0x0412,
        ExcludeClipRect = 0x0415,
        IntersectClipRect = 0x0416,
        Ellipse = 0x0418,
        FloodFill = 0x0419,
        FrameRegion = 0x0429,
        AnimatePalette = 0x0436,
        TextOut = 0x0521,
        PolyPolygon = 0x0538,
        ExtFloodFill = 0x0548,
        Rectangle = 0x041B,
        SetPixel = 0x041F,
        RoundRect = 0x061C,
        PatBlt = 0x061D,
        SaveDc = 0x001E,
        Pie = 0x081A,
        StretchBlt = 0x0B23,
        Escape = 0x0626,
        InvertRegion = 0x012A,
        PaintRegion = 0x012B,
        SelectClipRegion = 0x012C,
        SelectObject = 0x012D,
        SetTextAlign = 0x012E,
        Arc = 0x0817,
        Chord = 0x0830,
        BitBlt = 0x0922,
        ExtTextOut = 0x0A32,
        SetDibToDev = 0x0D33,
        DibBitBlt = 0x0940,
        DibStretchBlt = 0x0B41,
        StretchDib = 0x0F43,
        DeleteObject = 0x01F0,
        CreatePalette = 0x00F7,
        CreatePatternBrush = 0x01F9,
        CreatePenIndirect = 0x02FA,
        CreateFontIndirect = 0x02FB,
        CreateBrushIndirect = 0x02FC,
        CreateRegion = 0x06FF
    }

    public enum WmfBinRasterOp : ushort
    {
        Black = 0x0001,
        NotMergePen = 0x0002,
        MaskNotPen = 0x0003,
        NotCopyPen = 0x0004,
        MaskPenNot = 0x0005,
        Not = 0x0006,
        XorPen = 0x0007,
        NotMaskPen = 0x0008,
        MaskPen = 0x0009,
        NotXorPen = 0x000A,
        Nop = 0x000B,
        MergeNotPen = 0x000C,
        CopyPen = 0x000D,
        MergePenNot = 0x000E,
        MergePen = 0x000F,
        White = 0x0010
    }

    public enum WmfMixMode : ushort
    {
        Transparent = 0x0001,
        Opaque = 0x0002
    }

    public enum WmfPolyFillMode : ushort
    {
        Alternate = 0x0001,
        Winding = 0x0002
    }
}
