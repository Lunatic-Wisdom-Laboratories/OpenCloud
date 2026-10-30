using System;
using System.Collections.Generic;
using System.IO;
using OpenCloud.Core;

namespace OpenCloud.Assets.Textures;

/// <summary>
/// IMG texture archives (<c>EnterIMGFile</c>):
/// <list type="bullet">
/// <item><c>IMG</c>/<c>IM2</c>: count at +4, 0x30-byte entries from +0x10 holding name[32] and the
/// TIM2 offset at +0x20. IM2 pictures are swizzled.</item>
/// <item><c>IM3</c>: count at +8, 0x40-byte entries from +0x10: name[32], offset +0x24, swizzled
/// +0x28, texture block +0x2C, no_image +0x30, script size +0x34 and GS CLAMP +0x38. Entries whose
/// name starts with '#' are texture animation scripts (CFG), not pictures.</item>
/// </list>
/// </summary>
public sealed class ImgArchive
{
    public enum ArchiveKind
    {
        Img,
        Im2,
        Im3,
    }

    public sealed record Entry(string Name, int Offset, bool Swizzled, int Block, bool NoImage, ulong Clamp, int ScriptSize)
    {
        /// <summary>True for a texture animation script rather than a picture.</summary>
        public bool IsScript => Name.StartsWith('#');
    }

    private readonly byte[] _data;

    private ImgArchive(byte[] data, ArchiveKind kind, List<Entry> entries)
    {
        _data = data;
        Kind = kind;
        Entries = entries;
    }

    public ArchiveKind Kind { get; }

    public IReadOnlyList<Entry> Entries { get; }

    public static bool IsImg(ReadOnlySpan<byte> data) =>
        data.Length >= 16 && data[0] == 'I' && data[1] == 'M' && (data[2] == 'G' || data[2] == '2' || data[2] == '3') && data[3] == 0;

    public static ImgArchive Parse(byte[] data)
    {
        if (!IsImg(data))
            throw new InvalidDataException("Not an IMG archive.");

        var kind = data[2] switch
        {
            (byte)'G' => ArchiveKind.Img,
            (byte)'2' => ArchiveKind.Im2,
            _ => ArchiveKind.Im3,
        };
        bool im3 = kind == ArchiveKind.Im3;
        int count = LittleEndian.I32(data, im3 ? 8 : 4);
        int stride = im3 ? 0x40 : 0x30;
        if (count < 0 || count > 16384 || !LittleEndian.InRange(data, 0x10, (long)count * stride))
            throw new InvalidDataException("Invalid IMG entry count.");

        var entries = new List<Entry>(count);
        for (int i = 0; i < count; i++)
        {
            var e = data.AsSpan(0x10 + i * stride, stride);
            string name = GameText.ReadCString(e, 0, 32);
            if (im3)
            {
                entries.Add(new Entry(
                    name,
                    LittleEndian.I32(e, 0x24),
                    LittleEndian.I32(e, 0x28) != 0,
                    Math.Max(LittleEndian.I32(e, 0x2C), 0),
                    LittleEndian.U16(e, 0x30) != 0,
                    LittleEndian.U64(e, 0x38),
                    LittleEndian.I32(e, 0x34)));
            }
            else
            {
                entries.Add(new Entry(name, LittleEndian.I32(e, 0x20), kind == ArchiveKind.Im2, 0, false, 0, 0));
            }
        }
        return new ImgArchive(data, kind, entries);
    }

    /// <summary>Decodes the pictures of one entry. One IMG entry can hold several TIM2 pictures.</summary>
    public List<Tim2Picture> DecodePictures(Entry entry)
    {
        if (entry.IsScript || entry.Offset <= 0 || entry.Offset >= _data.Length)
            return new List<Tim2Picture>();
        return Tim2Decoder.Decode(_data.AsSpan(entry.Offset), entry.Swizzled, entry.NoImage);
    }

    /// <summary>The CFG text of a texture animation script entry.</summary>
    public ReadOnlySpan<byte> GetScript(Entry entry)
    {
        if (!entry.IsScript || !LittleEndian.InRange(_data, entry.Offset, entry.ScriptSize))
            return ReadOnlySpan<byte>.Empty;
        return _data.AsSpan(entry.Offset, entry.ScriptSize);
    }
}
