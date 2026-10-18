using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Wasla.UnitTests.Branding;

/// <summary>
/// Small images built byte by byte for the restaurant logo tests: a complete PNG a browser can draw, and JPEG and WebP
/// headers that carry a pixel size but no picture.
/// </summary>
internal static class TestImages
{
    /// <summary>A complete, solid-colour RGB PNG.</summary>
    public static byte[] Png(int width, int height, byte red = 0xE0, byte green = 0x7A, byte blue = 0x45)
    {
        var rowLength = 1 + width * 3;
        var raw = new byte[rowLength * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * rowLength + 1 + x * 3;
                raw[offset] = red;
                raw[offset + 1] = green;
                raw[offset + 2] = blue;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // truecolour

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>A PNG signature and IHDR that claim the given size, with no image data after them.</summary>
    public static byte[] PngHeaderOnly(int width, int height)
    {
        var png = Png(1, 1);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        return png[..33];
    }

    /// <summary>SOI, a JFIF APP0 segment and a baseline frame header of the given size.</summary>
    public static byte[] JpegHeader(int width, int height)
    {
        using var jpeg = new MemoryStream();
        jpeg.Write([0xFF, 0xD8]);
        jpeg.Write([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        jpeg.Write([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03,
            0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        jpeg.Write([0xFF, 0xD9]);
        return jpeg.ToArray();
    }

    /// <summary>A WebP container whose first chunk is a lossy (<c>VP8 </c>), lossless (<c>VP8L</c>) or extended (<c>VP8X</c>) header.</summary>
    public static byte[] WebP(string chunk, int width, int height)
    {
        var payload = new byte[16];
        switch (chunk)
        {
            case "VP8 ":
                payload[3] = 0x9D;
                payload[4] = 0x01;
                payload[5] = 0x2A;
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), (ushort)width);
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), (ushort)height);
                break;
            case "VP8L":
                payload[0] = 0x2F;
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), (uint)(width - 1) | (uint)(height - 1) << 14);
                break;
            case "VP8X":
                payload[4] = (byte)(width - 1);
                payload[5] = (byte)((width - 1) >> 8);
                payload[6] = (byte)((width - 1) >> 16);
                payload[7] = (byte)(height - 1);
                payload[8] = (byte)((height - 1) >> 8);
                payload[9] = (byte)((height - 1) >> 16);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(chunk));
        }

        using var webp = new MemoryStream();
        webp.Write("RIFF"u8);
        webp.Write(LittleEndian(4 + 8 + payload.Length));
        webp.Write("WEBP"u8);
        webp.Write(Encoding.ASCII.GetBytes(chunk));
        webp.Write(LittleEndian(payload.Length));
        webp.Write(payload);
        return webp.ToArray();
    }

    private static byte[] LittleEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typeAndData);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        png.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return ~crc;
    }
}
