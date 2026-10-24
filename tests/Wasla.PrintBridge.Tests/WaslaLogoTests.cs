using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Wasla.PrintBridge.UI;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// Print Bridge ships the canonical Wasla logo owned by Wasla.Web (linked, not copied): the same bytes go to
/// shell-ui for the WebView2 page and into the assembly for the native windows, the geometry is read exactly as
/// the SVG defines it, and the wordmark is never drawn back to front in a right-to-left window.
/// </summary>
public sealed class WaslaLogoTests
{
    private const uint LayoutRtl = 0x00000001;

    [Fact]
    public void ShippedLogo_IsTheCanonicalWebAsset()
    {
        var canonical = File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(), "src", "Wasla.Web", "wwwroot", "images", "brand", "wasla-logo.svg"));

        using var resource = typeof(WaslaLogo).Assembly.GetManifestResourceStream(WaslaLogoShape.ResourceName);
        Assert.NotNull(resource);
        using var embedded = new MemoryStream();
        resource.CopyTo(embedded);

        Assert.Equal(canonical, embedded.ToArray());
        Assert.Equal(canonical, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "shell-ui", "wasla-logo.svg")));
    }

    [Fact]
    public void EmbeddedLogo_KeepsTheSvgGeometryAndColour()
    {
        var shape = WaslaLogoShape.LoadEmbedded();

        Assert.Equal(new RectangleF(213.9F, 255.4F, 184.3F, 68.1F), shape.ViewBox);
        Assert.Equal(5, shape.Parts.Count); // w, a, s, l, a
        Assert.All(shape.Parts, part => Assert.Equal(Color.FromArgb(0xE8, 0x73, 0x42).ToArgb(), part.Fill.ToArgb()));
        Assert.All(shape.Parts, part =>
        {
            // Flatten first: the bounds of an unflattened path include Bézier control points, not just the ink.
            using var ink = (GraphicsPath)part.Path.Clone();
            ink.Flatten(null, 0.01F);
            Assert.True(shape.ViewBox.Contains(ink.GetBounds()), "A glyph is clipped by the viewBox.");
        });
        Assert.Equal(184.3F / 68.1F, WaslaLogoShape.AspectRatio, 3);
    }

    [Theory]
    [InlineData("M 0 0 H 10 Z")]
    [InlineData("m 0 0 l 1 1 z")]
    [InlineData("M 0 0 C 1 1 2 2")]
    public void UnsupportedPathData_IsRejected(string data)
    {
        var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><path d=\"{data}\"/></svg>";

        Assert.Throws<InvalidDataException>(() => WaslaLogoShape.Load(new MemoryStream(Encoding.UTF8.GetBytes(svg))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Logo_IsNeverDrawnBackToFront(bool mirroredSurface)
    {
        // RightToLeftLayout windows paint on a mirrored device context; SetLayout reproduces that on a bitmap.
        using var bitmap = new Bitmap(370, 137, PixelFormat.Format24bppRgb);
        using (var target = Graphics.FromImage(bitmap))
        {
            target.Clear(Color.White);
            var hdc = target.GetHdc();
            try
            {
                if (mirroredSurface)
                    Assert.NotEqual(uint.MaxValue, SetLayout(hdc, LayoutRtl));
                using var g = Graphics.FromHdc(hdc);
                WaslaLogo.Draw(g, bitmap.Size);
            }
            finally
            {
                target.ReleaseHdc(hdc);
            }
        }

        // Only the tall stroke of the "l" reaches the top fifth of the wordmark, about three quarters across it.
        var inkColumns = Enumerable.Range(0, bitmap.Width)
            .Where(x => Enumerable.Range(0, bitmap.Height / 5).Any(y => IsLogoInk(bitmap.GetPixel(x, y))))
            .ToArray();
        Assert.NotEmpty(inkColumns);
        Assert.InRange(inkColumns.Average() / bitmap.Width, 0.65, 0.82);
    }

    private static bool IsLogoInk(Color pixel) => pixel.R > 180 && pixel.G < 180 && pixel.B < 140;

    [DllImport("gdi32.dll")]
    private static extern uint SetLayout(IntPtr hdc, uint layout);
}
