using System.Buffers.Binary;

namespace Wasla.Application.Branding;

/// <summary>Why an uploaded restaurant logo was refused; <see cref="None"/> when it was accepted.</summary>
public enum RestaurantLogoError
{
    None = 0,
    Empty,
    TooLarge,
    UnsupportedFormat,
    InvalidImage,
    DimensionsTooLarge
}

/// <summary>The outcome of <see cref="RestaurantLogoImage.Inspect"/>: the detected media type and pixel size, or an error.</summary>
public sealed record RestaurantLogoInspection(RestaurantLogoError Error, string? ContentType = null, int Width = 0, int Height = 0)
{
    public bool IsValid => Error == RestaurantLogoError.None;
}

/// <summary>
/// The restaurant logo upload rules. A logo is a PNG, JPEG or WebP raster image of at most <see cref="MaxBytes"/> bytes
/// and <see cref="MaxDimension"/> pixels per side. The format is recognized from the content (the file name and the
/// client's media type are ignored), and the image's own structure is read far enough to find its pixel size: PNG chunks
/// up to IEND, JPEG segments up to the frame header, the WebP RIFF header and its first bitstream header. SVG, GIF and
/// anything else are refused. The pixels are not decoded, so a file whose compressed pixel data is damaged can pass and
/// then shows as a blank or partial image; a response the browser cannot read as an image at all falls back to the storefront
/// icon.
/// </summary>
public static class RestaurantLogoImage
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxDimension = 4096;

    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string WebP = "image/webp";

    /// <summary>The media types a file input may offer; the server still checks the content.</summary>
    public static readonly IReadOnlyList<string> ContentTypes = [Png, Jpeg, WebP];

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static RestaurantLogoInspection Inspect(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
            return new(RestaurantLogoError.Empty);
        if (content.Length > MaxBytes)
            return new(RestaurantLogoError.TooLarge);

        string contentType;
        (int Width, int Height)? size;
        if (content.StartsWith(PngSignature))
        {
            contentType = Png;
            size = ReadPng(content);
        }
        else if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            contentType = Jpeg;
            size = ReadJpeg(content);
        }
        else if (content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            contentType = WebP;
            size = ReadWebP(content);
        }
        else
        {
            return new(RestaurantLogoError.UnsupportedFormat);
        }

        if (size is not { Width: > 0, Height: > 0 } pixels)
            return new(RestaurantLogoError.InvalidImage);
        if (pixels.Width > MaxDimension || pixels.Height > MaxDimension)
            return new(RestaurantLogoError.DimensionsTooLarge);
        return new(RestaurantLogoError.None, contentType, pixels.Width, pixels.Height);
    }

    // IHDR first, then whole chunks with letter types, at least one IDAT, up to IEND.
    private static (int, int)? ReadPng(ReadOnlySpan<byte> png)
    {
        if (png.Length < 33
            || BinaryPrimitives.ReadUInt32BigEndian(png[8..]) != 13
            || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
            return null;
        var width = BinaryPrimitives.ReadUInt32BigEndian(png[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(png[20..]);

        var position = 8;
        var hasData = false;
        while (position + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png[position..]);
            if (length > (uint)(png.Length - position - 12))
                return null;
            var type = png.Slice(position + 4, 4);
            foreach (var letter in type)
            {
                if (!char.IsAsciiLetter((char)letter))
                    return null;
            }

            if (type.SequenceEqual("IDAT"u8))
                hasData = true;
            if (type.SequenceEqual("IEND"u8))
                return hasData ? Size(width, height) : null;
            position += 12 + (int)length;
        }

        return null;
    }

    // Segments from SOI to the first start-of-frame (SOF0–SOF15 except DHT, JPG and DAC), which holds the size.
    private static (int, int)? ReadJpeg(ReadOnlySpan<byte> jpeg)
    {
        var position = 2;
        while (position + 4 <= jpeg.Length)
        {
            if (jpeg[position] != 0xFF)
                return null;
            var marker = jpeg[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                position += 2;
                continue;
            }

            if (marker is 0xD9 or 0xDA)
                return null;

            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(position + 2)..]);
            if (length < 2 || position + 2 + length > jpeg.Length)
                return null;

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (length < 8)
                    return null;
                var height = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(position + 5)..]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(position + 7)..]);
                return Size(width, height);
            }

            position += 2 + length;
        }

        return null;
    }

    // A complete RIFF container whose first chunk is a lossy (VP8), lossless (VP8L) or extended (VP8X) header.
    private static (int, int)? ReadWebP(ReadOnlySpan<byte> webp)
    {
        if (webp.Length < 30)
            return null;
        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(webp[4..]);
        if (riffSize < 22 || riffSize > (uint)(webp.Length - 8))
            return null;
        var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(webp[16..]);
        if (chunkSize > riffSize - 12)
            return null;

        var chunk = webp.Slice(12, 4);
        if (chunk.SequenceEqual("VP8 "u8))
        {
            if (webp[23] != 0x9D || webp[24] != 0x01 || webp[25] != 0x2A)
                return null;
            return Size(
                BinaryPrimitives.ReadUInt16LittleEndian(webp[26..]) & 0x3FFFu,
                BinaryPrimitives.ReadUInt16LittleEndian(webp[28..]) & 0x3FFFu);
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (webp[20] != 0x2F)
                return null;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(webp[21..]);
            return Size((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            return Size(
                1 + (webp[24] | (uint)webp[25] << 8 | (uint)webp[26] << 16),
                1 + (webp[27] | (uint)webp[28] << 8 | (uint)webp[29] << 16));
        }

        return null;
    }

    private static (int, int) Size(uint width, uint height) =>
        ((int)Math.Min(width, int.MaxValue), (int)Math.Min(height, int.MaxValue));
}
