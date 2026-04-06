using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using VerifyXunit;
using Xunit;

namespace WmfDotNet.Tests
{
    public class WmfTests
    {
        private static string TestDataPath =>
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "TestData");

        [Fact]
        public void ParseWmf_SampleFile_ReadsCorrectly()
        {
            var wmf = Wmf.FromFile(System.IO.Path.Combine(TestDataPath, "sample.wmf"));

            Assert.NotNull(wmf);
            Assert.Null(wmf.SpecialHeader);  // standard WMF, no placeable header
            Assert.NotNull(wmf.Header);
            Assert.Equal(WmfMetafileType.MemoryMetafile, wmf.Header.MetafileType);
            Assert.True(wmf.Records.Count > 0);
            Assert.Equal(WmfFunc.Eof, wmf.Records[^1].Function);
        }

        [Fact]
        public void ParseWmf_SampleFile_ContainsExpectedRecords()
        {
            var wmf = Wmf.FromFile(System.IO.Path.Combine(TestDataPath, "sample.wmf"));

            var funcs = wmf.Records.ConvertAll(r => r.Function);
            Assert.Contains(WmfFunc.SetWindowOrg, funcs);
            Assert.Contains(WmfFunc.SetWindowExt, funcs);
            Assert.Contains(WmfFunc.Polygon, funcs);
            Assert.Contains(WmfFunc.Polyline, funcs);
            Assert.Contains(WmfFunc.Eof, funcs);
        }

        [Fact]
        public void ParseWmf_SampleFile_PolygonHasCorrectPoints()
        {
            var wmf = Wmf.FromFile(System.IO.Path.Combine(TestDataPath, "sample.wmf"));

            var polyRecord = wmf.Records.Find(r => r.Function == WmfFunc.Polygon);
            Assert.NotNull(polyRecord);
            var poly = Assert.IsType<WmfParamsPolygon>(polyRecord.Params);
            Assert.Equal(3, poly.NumPoints);
            Assert.Equal(3, poly.Points.Count);
        }

        [Fact]
        public Task RenderWmf_SampleFile_ProducesVerifiedImage()
        {
            var wmf = Wmf.FromFile(System.IO.Path.Combine(TestDataPath, "sample.wmf"));
            var writer = new WmfWriter(wmf);

            using var canvas = new SkiaSharpImageCanvas(100, 100);
            writer.Render(canvas, 100, 100);

            var image = canvas.GetImage();
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            var pngBytes = ms.ToArray();

            return Verifier.Verify(pngBytes, "png");
        }

        [Fact]
        public void ParseWmf_FromStream_Works()
        {
            var filePath = System.IO.Path.Combine(TestDataPath, "sample.wmf");
            using var stream = File.OpenRead(filePath);
            var wmf = new Wmf(stream);

            Assert.NotNull(wmf);
            Assert.True(wmf.Records.Count > 0);
        }

        [Fact]
        public void WmfWriter_Render_DoesNotThrow()
        {
            var wmf = Wmf.FromFile(System.IO.Path.Combine(TestDataPath, "sample.wmf"));
            var writer = new WmfWriter(wmf);

            using var canvas = new SkiaSharpImageCanvas(200, 200);
            var ex = Record.Exception(() => writer.Render(canvas, 200, 200));
            Assert.Null(ex);
        }
    }
}
