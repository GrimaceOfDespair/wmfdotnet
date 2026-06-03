using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using VerifyXunit;
using Xunit;

namespace WmfDotNet.Tests
{
    public class EmfTests
    {
        private static string TestDataPath =>
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "TestData");

        [Fact]
        public void ParseEmf_SampleFile_ReadsCorrectly()
        {
            var emf = Emf.FromFile(Path.Combine(TestDataPath, "sample.emf"));

            Assert.NotNull(emf);
            Assert.NotNull(emf.Header);
            Assert.Equal(0x464D4520u, emf.Header.Signature);
            Assert.True(emf.Records.Count > 0);
            Assert.Equal(EmfFunc.Header, emf.Records[0].Function);
            Assert.Equal(EmfFunc.Eof, emf.Records[^1].Function);
        }

        [Fact]
        public void ParseEmf_SampleFile_ContainsExpectedRecords()
        {
            var emf = Emf.FromFile(Path.Combine(TestDataPath, "sample.emf"));

            var funcs = emf.Records.ConvertAll(r => r.Function);
            Assert.Contains(EmfFunc.SetWindowOrgEx, funcs);
            Assert.Contains(EmfFunc.SetWindowExtEx, funcs);
            Assert.Contains(EmfFunc.Polygon, funcs);
            Assert.Contains(EmfFunc.Polyline, funcs);
            Assert.Contains(EmfFunc.PolyPolygon, funcs);
            Assert.Contains(EmfFunc.Eof, funcs);
        }

        [Fact]
        public void ParseEmf_Sample2File_DetectsEmfPlusComment()
        {
            var emf = Emf.FromFile(Path.Combine(TestDataPath, "sample2.emf"));
            Assert.True(emf.ContainsEmfPlusRecords);
        }

        [Theory]
        [InlineData("sample")]
        [InlineData("sample2")]
        public Task RenderEmf_SampleFile_ProducesVerifiedImage(string imageName)
        {
            var emf = Emf.FromFile(Path.Combine(TestDataPath, $"{imageName}.emf"));
            var writer = new EmfWriter(emf);

            var (canvasW, canvasH) = writer.GetRenderSize(maxDimension: 500);

            using var canvas = new SkiaSharpImageCanvas(canvasW, canvasH);
            writer.Render(canvas, canvasW, canvasH);

            var image = canvas.GetImage();
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            var pngBytes = ms.ToArray();

            return Verifier.Verify(pngBytes, "png");
        }

        [Theory]
        [InlineData("sample")]
        [InlineData("sample2")]
        public void EmfWriter_Render_DoesNotThrow(string imageName)
        {
            var emf = Emf.FromFile(Path.Combine(TestDataPath, $"{imageName}.emf"));
            var writer = new EmfWriter(emf);
            var (canvasW, canvasH) = writer.GetRenderSize(maxDimension: 200);

            using var canvas = new SkiaSharpImageCanvas(canvasW, canvasH);

            var ex = Record.Exception(() =>
                writer.Render(canvas, canvasW, canvasH));

            Assert.Null(ex);
        }
    }
}
