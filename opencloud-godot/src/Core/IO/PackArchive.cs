using System;
using System.Collections.Generic;

namespace OpenCloud.Core.IO;

/// <summary>
/// The engine's PACK container (<c>.chr</c>, <c>.pak</c>, <c>.pac</c>, <c>.pcp</c>...): a chain of
/// 0x4C-byte entries <c>{ char name[64]; int offset; int size; int next; }</c>, where offset and
/// next are relative to the entry and an empty name ends the chain (dataread <c>PACK_ENTRY</c>,
/// <c>GetPackFile</c>). Lookups compare only the file name, case-insensitively.
/// </summary>
public sealed class PackArchive : IFileSource
{
    public const int EntrySize = 0x4C;

    private readonly byte[] _data;
    private readonly List<PackEntry> _entries;
    private readonly Dictionary<string, int> _byName = new(VfsPath.Comparer);

    public readonly record struct PackEntry(string Name, int Offset, int Size);

    private PackArchive(byte[] data, List<PackEntry> entries, string description)
    {
        _data = data;
        _entries = entries;
        Description = description;
        for (int i = 0; i < entries.Count; i++)
            _byName.TryAdd(entries[i].Name, i);
    }

    public string Description { get; }

    public IReadOnlyList<PackEntry> Entries => _entries;

    /// <summary>
    /// Parses a PACK, rejecting data that is not one (the check the extractor uses: printable
    /// names without separators, entries and payloads inside the buffer, no loops).
    /// </summary>
    public static PackArchive? TryParse(byte[] data, string description = "pack")
    {
        if (data.Length < EntrySize)
            return null;

        var entries = new List<PackEntry>();
        var visited = new HashSet<long>();
        long cursor = 0;
        while (visited.Add(cursor) && entries.Count < 16384)
        {
            if (!LittleEndian.InRange(data, cursor, EntrySize))
                return null;
            var entry = data.AsSpan((int)cursor, EntrySize);
            int nameLength = entry[..64].IndexOf((byte)0);
            if (nameLength == 0)
                return entries.Count == 0 ? null : new PackArchive(data, entries, description);
            if (nameLength < 0)
                return null;
            foreach (byte b in entry[..nameLength])
            {
                if (b < 0x20 || b == '/' || b == '\\')
                    return null;
            }

            int offset = LittleEndian.I32(entry, 64);
            int size = LittleEndian.I32(entry, 68);
            int next = LittleEndian.I32(entry, 72);
            if (offset < EntrySize || size < 0 || next < EntrySize || !LittleEndian.InRange(data, cursor + offset, size))
                return null;

            entries.Add(new PackEntry(GameText.Decode(entry[..nameLength]), (int)(cursor + offset), size));
            cursor += next;
        }
        return null;
    }

    public bool Contains(string path) => _byName.ContainsKey(VfsPath.GetFileName(path));

    public byte[]? TryRead(string path)
    {
        if (!_byName.TryGetValue(VfsPath.GetFileName(path), out int index))
            return null;
        return Read(_entries[index]);
    }

    public ReadOnlySpan<byte> GetSpan(PackEntry entry) => _data.AsSpan(entry.Offset, entry.Size);

    public byte[] Read(PackEntry entry) => GetSpan(entry).ToArray();

    /// <summary>Entries whose text after the first '.' matches the extension, as <c>GetPackFileExt</c> does.</summary>
    public IEnumerable<PackEntry> WithExtension(string extension)
    {
        foreach (var entry in _entries)
        {
            int dot = entry.Name.IndexOf('.');
            if (dot >= 0 && string.Equals(entry.Name[(dot + 1)..], extension, StringComparison.OrdinalIgnoreCase))
                yield return entry;
        }
    }

    public IEnumerable<string> EnumerateFiles()
    {
        foreach (var entry in _entries)
            yield return entry.Name;
    }

    public void Dispose()
    {
    }
}
