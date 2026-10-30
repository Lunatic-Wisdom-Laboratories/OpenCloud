using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace OpenCloud.Core.IO;

/// <summary>
/// Reader for a <c>data.zip</c> of converted game data and for datapacks: a single-disk ZIP, classic or ZIP64, whose entries are stored (method 0), Zstandard
/// compressed (method 93) or, for hand-made datapacks, deflated (method 8). Every read checks
/// the entry's CRC-32.
/// </summary>
public sealed class ZipArchiveSource : IFileSource
{
    private const uint LocalHeaderSignature = 0x04034B50;
    private const uint CentralHeaderSignature = 0x02014B50;
    private const uint EndSignature = 0x06054B50;
    private const uint Zip64EndSignature = 0x06064B50;
    private const uint Zip64LocatorSignature = 0x07064B50;

    private const ushort MethodStored = 0;
    private const ushort MethodDeflate = 8;
    private const ushort MethodZstd = 93;

    private readonly FileByteRange _file;
    private readonly Dictionary<string, Entry> _entries = new(VfsPath.Comparer);

    private readonly record struct Entry(
        string Name, ushort Method, ushort Flags, uint Crc, long CompressedSize, long Size, long LocalHeaderOffset);

    public ZipArchiveSource(string path)
    {
        _file = new FileByteRange(path);
        try
        {
            ReadCentralDirectory();
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public string Description => $"zip {_file.Path} ({_entries.Count} entries)";

    public int Count => _entries.Count;

    public bool Contains(string path) => _entries.ContainsKey(path);

    public IEnumerable<string> EnumerateFiles() => _entries.Keys;

    public byte[]? TryRead(string path)
    {
        if (!_entries.TryGetValue(path, out var entry))
            return null;

        if ((entry.Flags & 1) != 0)
            throw new InvalidDataException($"{entry.Name}: encrypted ZIP entries are not supported.");

        Span<byte> local = stackalloc byte[30];
        _file.Read(entry.LocalHeaderOffset, local);
        if (LittleEndian.U32(local, 0) != LocalHeaderSignature)
            throw new InvalidDataException($"{entry.Name}: bad local header signature.");
        long dataOffset = entry.LocalHeaderOffset + 30 + LittleEndian.U16(local, 26) + LittleEndian.U16(local, 28);

        var compressed = new byte[entry.CompressedSize];
        _file.Read(dataOffset, compressed);

        byte[] output = entry.Method switch
        {
            MethodStored => compressed,
            MethodZstd => DecompressZstd(compressed, entry),
            MethodDeflate => DecompressDeflate(compressed, entry),
            _ => throw new InvalidDataException($"{entry.Name}: unsupported compression method {entry.Method}."),
        };

        if (output.LongLength != entry.Size)
            throw new InvalidDataException($"{entry.Name}: expected {entry.Size} bytes, got {output.LongLength}.");
        if (Crc32.Compute(output) != entry.Crc)
            throw new InvalidDataException($"{entry.Name}: CRC-32 mismatch.");
        return output;
    }

    private static byte[] DecompressZstd(byte[] compressed, Entry entry)
    {
        var output = new byte[entry.Size];
        using var decompressor = new ZstdSharp.Decompressor();
        int written = decompressor.Unwrap(compressed, output, 0);
        return written == output.Length ? output : output.AsSpan(0, written).ToArray();
    }

    private static byte[] DecompressDeflate(byte[] compressed, Entry entry)
    {
        var output = new byte[entry.Size];
        using var stream = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
        int total = 0;
        while (total < output.Length)
        {
            int read = stream.Read(output, total, output.Length - total);
            if (read <= 0)
                break;
            total += read;
        }
        return total == output.Length ? output : output.AsSpan(0, total).ToArray();
    }

    private void ReadCentralDirectory()
    {
        // The classic end record sits in the last 64 KiB + 22 bytes (its comment is at most 64 KiB).
        int tailLength = (int)Math.Min(_file.Length, 0xFFFF + 22);
        var tail = new byte[tailLength];
        long tailStart = _file.Length - tailLength;
        _file.Read(tailStart, tail);

        int end = -1;
        for (int i = tailLength - 22; i >= 0; i--)
        {
            if (LittleEndian.U32(tail, i) == EndSignature)
            {
                end = i;
                break;
            }
        }
        if (end < 0)
            throw new InvalidDataException($"{_file.Path}: no ZIP end of central directory record.");

        long entryCount = LittleEndian.U16(tail, end + 10);
        long directorySize = LittleEndian.U32(tail, end + 12);
        long directoryOffset = LittleEndian.U32(tail, end + 16);

        // ZIP64: the locator immediately precedes the classic end record.
        long locator = tailStart + end - 20;
        if (locator >= 0)
        {
            Span<byte> loc = stackalloc byte[20];
            _file.Read(locator, loc);
            if (LittleEndian.U32(loc, 0) == Zip64LocatorSignature)
            {
                long zip64End = (long)LittleEndian.U64(loc, 8);
                Span<byte> record = stackalloc byte[56];
                _file.Read(zip64End, record);
                if (LittleEndian.U32(record, 0) != Zip64EndSignature)
                    throw new InvalidDataException($"{_file.Path}: bad ZIP64 end record.");
                entryCount = (long)LittleEndian.U64(record, 32);
                directorySize = (long)LittleEndian.U64(record, 40);
                directoryOffset = (long)LittleEndian.U64(record, 48);
            }
        }

        if (directoryOffset < 0 || directorySize < 0 || directoryOffset + directorySize > _file.Length)
            throw new InvalidDataException($"{_file.Path}: central directory outside the file.");

        var directory = new byte[directorySize];
        _file.Read(directoryOffset, directory);

        int cursor = 0;
        for (long i = 0; i < entryCount; i++)
        {
            ReadOnlySpan<byte> d = directory;
            if (!LittleEndian.InRange(d, cursor, 46) || LittleEndian.U32(d, cursor) != CentralHeaderSignature)
                throw new InvalidDataException($"{_file.Path}: bad central directory entry {i}.");

            ushort flags = LittleEndian.U16(d, cursor + 8);
            ushort method = LittleEndian.U16(d, cursor + 10);
            uint crc = LittleEndian.U32(d, cursor + 16);
            long compressedSize = LittleEndian.U32(d, cursor + 20);
            long size = LittleEndian.U32(d, cursor + 24);
            int nameLength = LittleEndian.U16(d, cursor + 28);
            int extraLength = LittleEndian.U16(d, cursor + 30);
            int commentLength = LittleEndian.U16(d, cursor + 32);
            long localOffset = LittleEndian.U32(d, cursor + 42);

            string name = System.Text.Encoding.UTF8.GetString(d.Slice(cursor + 46, nameLength));
            ApplyZip64Extra(d.Slice(cursor + 46 + nameLength, extraLength), ref size, ref compressedSize, ref localOffset);

            cursor += 46 + nameLength + extraLength + commentLength;

            if (name.EndsWith('/'))
                continue;
            string key = VfsPath.Normalize(name);
            _entries.TryAdd(key, new Entry(key, method, flags, crc, compressedSize, size, localOffset));
        }
    }

    /// <summary>The ZIP64 extra field lists, in order, only the values whose 32-bit field is saturated.</summary>
    private static void ApplyZip64Extra(ReadOnlySpan<byte> extra, ref long size, ref long compressedSize, ref long localOffset)
    {
        int cursor = 0;
        while (cursor + 4 <= extra.Length)
        {
            ushort id = LittleEndian.U16(extra, cursor);
            ushort length = LittleEndian.U16(extra, cursor + 2);
            var field = extra.Slice(cursor + 4, Math.Min(length, extra.Length - cursor - 4));
            if (id == 0x0001)
            {
                int at = 0;
                if (size == 0xFFFFFFFF && at + 8 <= field.Length) { size = (long)LittleEndian.U64(field, at); at += 8; }
                if (compressedSize == 0xFFFFFFFF && at + 8 <= field.Length) { compressedSize = (long)LittleEndian.U64(field, at); at += 8; }
                if (localOffset == 0xFFFFFFFF && at + 8 <= field.Length) { localOffset = (long)LittleEndian.U64(field, at); }
                return;
            }
            cursor += 4 + length;
        }
    }

    public void Dispose() => _file.Dispose();
}
