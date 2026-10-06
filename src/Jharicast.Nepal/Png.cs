using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Jharicast.Nepal;

/// <summary>
/// Decodes the PNG files DHM publishes its warning maps as: 8-bit truecolour, truecolour with
/// alpha or palette, not interlaced (ADR-0016). Anything else is rejected, not guessed at.
/// </summary>
/// <remarks>
/// The input comes from the network, so the decoder trusts nothing in it: the size is capped before
/// anything is allocated, and inflation stops at the exact byte count the header implies, so a
/// small file cannot expand into a large allocation.
/// </remarks>
internal static class Png
{
    /// <summary>Largest width or height accepted. DHM's maps are 1300 by 800.</summary>
    public const int MaxSide = 2048;

    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Decodes to RGB, three bytes per pixel, rows top to bottom. Alpha is dropped.</summary>
    /// <param name="png">The file.</param>
    /// <returns>The image.</returns>
    /// <exception cref="InvalidDataException">Not a PNG this decoder reads, or damaged.</exception>
    public static RgbImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        int width = 0, height = 0, colourType = -1;
        byte[]? palette = null;
        using var idat = new MemoryStream();
        var at = Signature.Length;
        while (true)
        {
            if (png.Length - at < 12)
            {
                throw new InvalidDataException("PNG ends inside a chunk header.");
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(png[at..]);
            var type = png.Slice(at + 4, 4);
            if (length > (uint)(png.Length - at - 12))
            {
                throw new InvalidDataException("PNG chunk runs past the end of the file.");
            }

            var data = png.Slice(at + 8, (int)length);
            at += 12 + (int)length;
            if (type.SequenceEqual("IHDR"u8))
            {
                (width, height, colourType) = ReadHeader(data);
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        if (width == 0)
        {
            throw new InvalidDataException("PNG has no IHDR chunk.");
        }

        if (colourType == 3 && palette is null)
        {
            throw new InvalidDataException("Palette PNG has no PLTE chunk.");
        }

        var channels = colourType switch { 2 => 3, 6 => 4, _ => 1 };
        var stride = width * channels;
        var raw = new byte[height * (stride + 1)];
        idat.Position = 0;
        using (var inflate = new ZLibStream(idat, CompressionMode.Decompress))
        {
            try
            {
                inflate.ReadExactly(raw);
            }
            catch (EndOfStreamException e)
            {
                throw new InvalidDataException("PNG image data is shorter than its header says.", e);
            }
        }

        return new RgbImage(width, height, ToRgb(Unfilter(raw, height, stride, channels), width, height, colourType, palette));
    }

    private static (int Width, int Height, int ColourType) ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length != 13)
        {
            throw new InvalidDataException("PNG IHDR has the wrong length.");
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(data);
        var height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (width is 0 or > MaxSide || height is 0 or > MaxSide)
        {
            throw new InvalidDataException($"PNG is {width} by {height}; at most {MaxSide} a side is read.");
        }

        var (depth, colourType, interlace) = (data[8], data[9], data[12]);
        if (depth != 8 || colourType is not (2 or 3 or 6) || interlace != 0)
        {
            throw new InvalidDataException($"PNG bit depth {depth}, colour type {colourType}, interlace {interlace} is not read; 8-bit truecolour or palette, not interlaced, is.");
        }

        return ((int)width, (int)height, colourType);
    }

    // PNG filters, RFC 2083 section 6. Each row starts with its filter type byte; the decoded rows
    // are written over the raw buffer without those bytes.
    private static byte[] Unfilter(byte[] raw, int height, int stride, int bpp)
    {
        var pixels = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var source = raw.AsSpan(y * (stride + 1) + 1, stride);
            var row = pixels.AsSpan(y * stride, stride);
            var above = y == 0 ? Span<byte>.Empty : pixels.AsSpan((y - 1) * stride, stride);
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? row[x - bpp] : 0;
                int b = above.IsEmpty ? 0 : above[x];
                int c = x >= bpp && !above.IsEmpty ? above[x - bpp] : 0;
                row[x] = (byte)(source[x] + filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"PNG row {y} has unknown filter {filter}."),
                });
            }
        }

        return pixels;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var (pa, pb, pc) = (Math.Abs(p - a), Math.Abs(p - b), Math.Abs(p - c));
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] ToRgb(byte[] pixels, int width, int height, int colourType, byte[]? palette)
    {
        if (colourType == 2)
        {
            return pixels;
        }

        var rgb = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            if (colourType == 6)
            {
                pixels.AsSpan(i * 4, 3).CopyTo(rgb.AsSpan(i * 3));
            }
            else
            {
                var entry = pixels[i] * 3;
                if (entry + 3 > palette!.Length)
                {
                    throw new InvalidDataException($"PNG pixel uses palette entry {pixels[i]}, past the palette's end.");
                }

                palette.AsSpan(entry, 3).CopyTo(rgb.AsSpan(i * 3));
            }
        }

        return rgb;
    }
}

/// <summary>A decoded image, three bytes per pixel.</summary>
/// <param name="Width">Width, pixels.</param>
/// <param name="Height">Height, pixels.</param>
/// <param name="Rgb">Pixels, rows top to bottom.</param>
internal sealed record RgbImage(int Width, int Height, byte[] Rgb)
{
    /// <summary>The colour at a pixel.</summary>
    public (byte R, byte G, byte B) this[int x, int y]
    {
        get
        {
            var i = (y * Width + x) * 3;
            return (Rgb[i], Rgb[i + 1], Rgb[i + 2]);
        }
    }
}
