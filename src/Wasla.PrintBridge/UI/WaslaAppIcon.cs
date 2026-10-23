using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Wasla.PrintBridge.UI;

/// <summary>
/// The app window's title bar and taskbar icon: the first letter of the canonical Wasla wordmark (the leftmost
/// path of the embedded <c>wasla-logo.svg</c>, never redrawn by hand) in white on a rounded tile in the logo's
/// orange. The icon holds one image per common size and display scale, so Windows never has to stretch one.
/// </summary>
internal static class WaslaAppIcon
{
    internal static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];

    private static readonly Lazy<Icon?> Shared = new(CreateOrNull);

    /// <summary>The shared icon, or null if the logo could not be read (the window then keeps the default icon).</summary>
    public static Icon? Instance => Shared.Value;

    /// <summary>Writes the icon file: a standard .ico with one PNG image per size in <see cref="Sizes"/>.</summary>
    internal static byte[] CreateIconFile(WaslaLogoShape shape)
    {
        var images = Sizes.Select(size => Render(shape, size)).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Length);

        var offset = 6 + 16 * images.Length;
        for (var i = 0; i < images.Length; i++)
        {
            var size = Sizes[i];
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }

        foreach (var image in images)
            writer.Write(image);

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>One PNG image of the icon at <paramref name="size"/> × <paramref name="size"/> pixels.</summary>
    internal static byte[] Render(WaslaLogoShape shape, int size)
    {
        var letter = shape.Parts.MinBy(part => part.Path.GetBounds().Left)!;
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            var radius = size * 0.22F;
            using (var tile = RoundedSquare(size, radius))
            using (var fill = new SolidBrush(letter.Fill))
                g.FillPath(fill, tile);

            // The letter fills 64 % of the tile's width and sits on its optical centre.
            var bounds = letter.Path.GetBounds();
            var scale = Math.Min(size * 0.64F / bounds.Width, size * 0.64F / bounds.Height);
            g.TranslateTransform((size - bounds.Width * scale) / 2, (size - bounds.Height * scale) / 2);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-bounds.X, -bounds.Y);
            g.FillPath(Brushes.White, letter.Path);
        }

        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    private static GraphicsPath RoundedSquare(int size, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(size - d, 0, d, d, 270, 90);
        path.AddArc(size - d, size - d, d, d, 0, 90);
        path.AddArc(0, size - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Icon? CreateOrNull()
    {
        if (WaslaLogoShape.Instance is not { } shape)
            return null;

        try
        {
            using var stream = new MemoryStream(CreateIconFile(shape));
            return new Icon(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            return null;
        }
    }
}
