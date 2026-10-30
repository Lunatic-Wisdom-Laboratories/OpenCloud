using System;
using System.Collections.Generic;
using System.IO;
using OpenCloud.Core;

namespace OpenCloud.Assets.Textures;

/// <summary>One decoded mip level as 8-bit RGBA.</summary>
public sealed record RgbaLevel(int Width, int Height, byte[] Pixels);

/// <summary>One TIM2 picture, decoded to RGBA levels (base level first, at most four as the GS uses).</summary>
public sealed class Tim2Picture
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Bits per pixel of the retail picture: 4, 8, 16, 24 or 32.</summary>
    public required int SourceBpp { get; init; }

    /// <summary>The picture's GS TEX1 register (filtering and mip settings).</summary>
    public required ulong Tex1 { get; init; }

    /// <summary>Decoded levels; empty for a picture that only reserves a canvas (IM3 no_image).</summary>
    public required IReadOnlyList<RgbaLevel> Levels { get; init; }

    /// <summary>Total bytes the picture occupies, header included.</summary>
    public required int ByteSize { get; init; }

    /// <summary>True when any pixel is not fully opaque.</summary>
    public bool HasTransparency
    {
        get
        {
            if (Levels.Count == 0)
                return false;
            var pixels = Levels[0].Pixels;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0xFF)
                    return true;
            }
            return false;
        }
    }
}

/// <summary>
/// Decoder for TIM2 images, the texture format inside IMG archives and <c>.tm2</c> files
/// (<c>TM2_head</c>). Pixel rules follow the PS2 GS:
/// <list type="bullet">
/// <item>alpha is 0..0x80 for 1.0, so it is doubled and saturated;</item>
/// <item>16-bit colours are RGB555 with the top bit as alpha, black with that bit clear is transparent;</item>
/// <item>8-bit palettes stored in CSM1 order have entries 8..15 and 16..23 of every 32 swapped;</item>
/// <item>IM2 entries hold 8-bit pixels swizzled for upload as PSMCT32 at half size.</item>
/// </list>
/// </summary>
public static class Tim2Decoder
{
    private const int PictureHeaderSize = 0x30;

    private enum ImageType : byte
    {
        Rgb555 = 1,
        Rgb888 = 2,
        Rgba8888 = 3,
        Indexed4 = 4,
        Indexed8 = 5,
    }

    public static bool IsTim2(ReadOnlySpan<byte> data) =>
        data.Length >= 16 && data[0] == 'T' && data[1] == 'I' && data[2] == 'M' && data[3] == '2';

    /// <summary>Decodes every picture of a TIM2 file.</summary>
    /// <param name="swizzled">True for IM2/IM3 swizzled 8-bit pictures.</param>
    /// <param name="noImage">True when the entry reserves only a canvas (IM3 <c>no_image</c>).</param>
    public static List<Tim2Picture> Decode(ReadOnlySpan<byte> data, bool swizzled = false, bool noImage = false)
    {
        if (!IsTim2(data) || data[5] > 1)
            throw new InvalidDataException("Not a TIM2 image.");

        int count = LittleEndian.U16(data, 6);
        if (count == 0 || count > 256)
            throw new InvalidDataException($"Invalid TIM2 picture count {count}.");

        // Format 1 aligns the first picture to 128 bytes.
        int position = data[5] != 0 ? 0x80 : 0x10;
        var pictures = new List<Tim2Picture>(count);
        for (int i = 0; i < count; i++)
        {
            var picture = DecodePicture(data, position, swizzled, noImage);
            pictures.Add(picture);
            position += picture.ByteSize;
        }
        return pictures;
    }

    private static Tim2Picture DecodePicture(ReadOnlySpan<byte> data, int offset, bool swizzled, bool noImage)
    {
        if (!LittleEndian.InRange(data, offset, PictureHeaderSize))
            throw new InvalidDataException("TIM2 picture header outside the file.");

        var header = data.Slice(offset, PictureHeaderSize);
        int totalSize = LittleEndian.I32(header, 0);
        int clutSize = LittleEndian.I32(header, 4);
        int imageSize = LittleEndian.I32(header, 8);
        int headerSize = LittleEndian.U16(header, 12);
        int mipCount = header[17];
        byte clutType = header[18];
        var type = (ImageType)header[19];
        int width = LittleEndian.U16(header, 20);
        int height = LittleEndian.U16(header, 22);
        ulong tex1 = LittleEndian.U64(header, 32);

        if (width == 0 || height == 0 || type < ImageType.Rgb555 || type > ImageType.Indexed8 || mipCount > 16)
            throw new InvalidDataException("Invalid TIM2 picture format.");

        int minimum = headerSize + (noImage ? 0 : imageSize + clutSize);
        // With mip levels the header grows by the mip register block and a size per level.
        int minimumHeader = mipCount > 1 ? 0x40 + 4 * Math.Min(mipCount, 4) : PictureHeaderSize;
        if (headerSize < minimumHeader || !LittleEndian.InRange(data, offset, minimum))
            throw new InvalidDataException("Invalid TIM2 picture sizes.");
        if (totalSize < minimum || !LittleEndian.InRange(data, offset, totalSize))
            totalSize = minimum;

        var levels = new List<RgbaLevel>();
        if (!noImage)
        {
            byte[]? palette = null;
            if (type >= ImageType.Indexed4)
            {
                if (clutSize == 0)
                    throw new InvalidDataException("Indexed TIM2 picture without a CLUT.");
                palette = DecodePalette(data.Slice(offset + headerSize + imageSize, clutSize), type, clutType);
            }

            int position = offset + headerSize;
            int imageEnd = offset + headerSize + imageSize;
            int levelCount = Math.Clamp(mipCount, 1, 4);
            for (int i = 0; i < levelCount; i++)
            {
                int levelWidth = Math.Max(width >> i, 1);
                int levelHeight = Math.Max(height >> i, 1);
                int needed = LevelByteSize(levelWidth, levelHeight, type);
                if (position + needed > imageEnd)
                    throw new InvalidDataException("TIM2 mip level extends beyond its image.");

                levels.Add(DecodeLevel(data.Slice(position, needed), levelWidth, levelHeight, type, swizzled, palette));
                position += mipCount > 1 ? LittleEndian.I32(data, offset + 0x40 + i * 4) : needed;
            }
        }

        return new Tim2Picture
        {
            Width = width,
            Height = height,
            SourceBpp = type switch
            {
                ImageType.Rgb555 => 16,
                ImageType.Rgb888 => 24,
                ImageType.Rgba8888 => 32,
                ImageType.Indexed4 => 4,
                _ => 8,
            },
            Tex1 = tex1,
            Levels = levels,
            ByteSize = totalSize,
        };
    }

    private static int LevelByteSize(int width, int height, ImageType type)
    {
        int count = checked(width * height);
        return type switch
        {
            ImageType.Rgb555 => count * 2,
            ImageType.Rgb888 => count * 3,
            ImageType.Rgba8888 => count * 4,
            ImageType.Indexed4 => (count + 1) / 2,
            _ => count,
        };
    }

    private static RgbaLevel DecodeLevel(ReadOnlySpan<byte> raw, int width, int height, ImageType type, bool swizzled, byte[]? palette)
    {
        int count = width * height;
        var pixels = new byte[count * 4];
        switch (type)
        {
            case ImageType.Rgb555:
                for (int i = 0; i < count; i++)
                    WriteRgb555(pixels, i, LittleEndian.U16(raw, i * 2));
                break;

            case ImageType.Rgb888:
                for (int i = 0; i < count; i++)
                {
                    pixels[i * 4] = raw[i * 3];
                    pixels[i * 4 + 1] = raw[i * 3 + 1];
                    pixels[i * 4 + 2] = raw[i * 3 + 2];
                    pixels[i * 4 + 3] = 0xFF;
                }
                break;

            case ImageType.Rgba8888:
                for (int i = 0; i < count; i++)
                {
                    pixels[i * 4] = raw[i * 4];
                    pixels[i * 4 + 1] = raw[i * 4 + 1];
                    pixels[i * 4 + 2] = raw[i * 4 + 2];
                    pixels[i * 4 + 3] = ScaleAlpha(raw[i * 4 + 3]);
                }
                break;

            case ImageType.Indexed4:
                for (int i = 0; i < count; i++)
                {
                    int index = (raw[i >> 1] >> ((i & 1) * 4)) & 0xF;
                    palette.AsSpan(index * 4, 4).CopyTo(pixels.AsSpan(i * 4));
                }
                break;

            case ImageType.Indexed8:
                bool unswizzle = swizzled && width % 16 == 0 && height % 16 == 0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int source = unswizzle ? SwizzledIndex8(x, y, width) : y * width + x;
                        palette.AsSpan(raw[source] * 4, 4).CopyTo(pixels.AsSpan((y * width + x) * 4));
                    }
                }
                break;
        }
        return new RgbaLevel(width, height, pixels);
    }

    private static byte[] DecodePalette(ReadOnlySpan<byte> raw, ImageType type, byte clutType)
    {
        int count = type == ImageType.Indexed4 ? 16 : 256;
        bool sixteenBit = (clutType & 0x3F) == 1;
        // CSM1 stores 8-bit palettes in GS column order; bit 7/6 of the CLUT type mark other layouts.
        bool csm1 = type == ImageType.Indexed8 && (clutType & 0xC0) == 0;

        var palette = new byte[256 * 4];
        for (int i = 0; i < count; i++)
        {
            int slot = csm1 ? (i & ~0x18) | ((i & 0x08) << 1) | ((i & 0x10) >> 1) : i;
            if (sixteenBit)
            {
                WriteRgb555(palette, slot, LittleEndian.U16(raw, i * 2));
            }
            else
            {
                palette[slot * 4] = raw[i * 4];
                palette[slot * 4 + 1] = raw[i * 4 + 1];
                palette[slot * 4 + 2] = raw[i * 4 + 2];
                palette[slot * 4 + 3] = ScaleAlpha(raw[i * 4 + 3]);
            }
        }
        return palette;
    }

    private static void WriteRgb555(byte[] destination, int pixel, ushort value)
    {
        byte r = (byte)((value & 0x1F) << 3);
        byte g = (byte)(((value >> 5) & 0x1F) << 3);
        byte b = (byte)(((value >> 10) & 0x1F) << 3);
        destination[pixel * 4] = r;
        destination[pixel * 4 + 1] = g;
        destination[pixel * 4 + 2] = b;
        destination[pixel * 4 + 3] = (value & 0x8000) != 0 || (r | g | b) != 0 ? (byte)0xFF : (byte)0;
    }

    /// <summary>GS alpha: 0x80 is fully opaque.</summary>
    private static byte ScaleAlpha(byte alpha) => (byte)Math.Min(alpha * 2, 0xFF);

    /// <summary>
    /// Byte position of texel (x, y) in an 8-bit picture stored with the PSMT8-as-PSMCT32 swizzle
    /// (16x16 blocks, rows interleaved in pairs of columns).
    /// </summary>
    private static int SwizzledIndex8(int x, int y, int width)
    {
        int block = (y & ~15) * width + (x & ~15) * 2;
        int swap = (((y + 2) >> 2) & 1) * 4;
        int row = (((y & ~3) >> 1) + (y & 1)) & 7;
        int column = row * width * 2 + ((x + swap) & 7) * 4;
        return block + column + (((y >> 1) & 1) + ((x >> 2) & 2));
    }
}
