using Wasla.PrintBridge.UI;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// The app window's icon is made from the canonical logo's first letter, with one image per common size so the
/// title bar and the taskbar stay sharp at every display scale.
/// </summary>
[Collection(SharedLogoShapeCollection.Name)]
public sealed class WaslaAppIconTests
{
    private static readonly Color Orange = Color.FromArgb(0xE8, 0x73, 0x42);

    [Fact]
    public void IconFile_HoldsOnePngImagePerSize()
    {
        var file = WaslaAppIcon.CreateIconFile(WaslaLogoShape.LoadEmbedded());

        Assert.Equal(0, BitConverter.ToUInt16(file, 0));
        Assert.Equal(1, BitConverter.ToUInt16(file, 2));
        Assert.Equal(WaslaAppIcon.Sizes.Length, BitConverter.ToUInt16(file, 4));
        for (var i = 0; i < WaslaAppIcon.Sizes.Length; i++)
        {
            var entry = 6 + 16 * i;
            var length = BitConverter.ToInt32(file, entry + 8);
            var offset = BitConverter.ToInt32(file, entry + 12);
            using var image = new Bitmap(new MemoryStream(file, offset, length));

            Assert.Equal(new Size(WaslaAppIcon.Sizes[i], WaslaAppIcon.Sizes[i]), image.Size);
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    public void Windows_PicksTheExactImageForEachSize(int size)
    {
        Assert.NotNull(WaslaAppIcon.Instance);

        using var icon = new Icon(WaslaAppIcon.Instance, size, size);

        Assert.Equal(new Size(size, size), icon.Size);
    }

    [Fact]
    public void Image_IsAWhiteLetterOnAnOrangeTile_WithTransparentCorners()
    {
        using var image = new Bitmap(new MemoryStream(WaslaAppIcon.Render(WaslaLogoShape.LoadEmbedded(), 64)));

        Assert.Equal(0, image.GetPixel(0, 0).A);
        Assert.Equal(0, image.GetPixel(63, 63).A);
        Assert.Equal(Orange.ToArgb(), image.GetPixel(32, 3).ToArgb());

        var pixels = Enumerable.Range(12, 40).SelectMany(x => Enumerable.Range(12, 40).Select(y => image.GetPixel(x, y))).ToArray();
        Assert.Contains(pixels, p => p.ToArgb() == Color.White.ToArgb());
        Assert.Contains(pixels, p => p.ToArgb() == Orange.ToArgb());
    }
}
