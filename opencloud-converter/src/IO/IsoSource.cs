using System;
using System.Collections.Generic;
using System.IO;

namespace OpenCloud.Core.IO;

/// <summary>
/// ISO 9660 disc image (2048-byte sectors), such as the user's own copy of the game disc. Files
/// are read straight from the image. When the disc carries <c>DATA.HD4</c>/<c>DATA.DAT</c>,
/// <see cref="TryOpenDataIndex"/> exposes the game files packed inside them.
/// </summary>
public sealed class IsoSource : IFileSource
{
    private const int SectorSize = 2048;

    private readonly FileByteRange _image;
    private readonly Dictionary<string, (long Offset, long Size)> _files = new(VfsPath.Comparer);

    public IsoSource(string path)
    {
        _image = new FileByteRange(path);
        try
        {
            var descriptor = new byte[SectorSize];
            _image.Read(16 * SectorSize, descriptor);
            if (descriptor[0] != 1 || System.Text.Encoding.ASCII.GetString(descriptor, 1, 5) != "CD001")
                throw new InvalidDataException($"{path}: no ISO 9660 primary volume descriptor.");
            ReadDirectory(descriptor.AsSpan(156, 34), string.Empty, 0);
        }
        catch
        {
            _image.Dispose();
            throw;
        }
    }

    public string Description => $"iso {_image.Path} ({_files.Count} files)";

    public bool Contains(string path) => _files.ContainsKey(path);

    public IEnumerable<string> EnumerateFiles() => _files.Keys;

    public byte[]? TryRead(string path)
    {
        if (!_files.TryGetValue(path, out var file))
            return null;
        var buffer = new byte[file.Size];
        _image.Read(file.Offset, buffer);
        return buffer;
    }

    /// <summary>Returns the retail DATA.HD4/DATA.DAT index stored on this disc, if any.</summary>
    public DataDatSource? TryOpenDataIndex()
    {
        var header = TryRead("DATA.HD4");
        if (header == null || !_files.TryGetValue("DATA.DAT", out var data))
            return null;
        var range = new SubByteRange(_image, data.Offset, data.Size);
        return new DataDatSource(header, range, $"DATA.HD4 in {_image.Path}");
    }

    private void ReadDirectory(ReadOnlySpan<byte> record, string prefix, int depth)
    {
        if (depth > 32)
            return;

        long extent = (long)LittleEndian.U32(record, 2) * SectorSize;
        long length = LittleEndian.U32(record, 10);
        if (extent + length > _image.Length)
            return;

        var directory = new byte[length];
        _image.Read(extent, directory);

        int cursor = 0;
        while (cursor < directory.Length)
        {
            int size = directory[cursor];
            if (size == 0)
            {
                // Records never cross a sector boundary; zero fill pads to the next sector.
                cursor = (cursor / SectorSize + 1) * SectorSize;
                continue;
            }
            if (cursor + size > directory.Length || size < 34)
                break;

            var entry = directory.AsSpan(cursor, size);
            int nameLength = entry[32];
            byte flags = entry[25];
            var rawName = entry.Slice(33, Math.Min(nameLength, size - 33));
            cursor += size;

            if (nameLength == 1 && (rawName[0] == 0 || rawName[0] == 1))
                continue;

            string name = VfsPath.Normalize(System.Text.Encoding.ASCII.GetString(rawName)).TrimEnd('.');
            string path = prefix.Length == 0 ? name : prefix + "/" + name;
            if ((flags & 0x02) != 0)
                ReadDirectory(entry, path, depth + 1);
            else
                _files.TryAdd(path, ((long)LittleEndian.U32(entry, 2) * SectorSize, LittleEndian.U32(entry, 10)));
        }
    }

    public void Dispose() => _image.Dispose();
}
