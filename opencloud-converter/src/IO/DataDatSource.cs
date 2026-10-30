using System;
using System.Collections.Generic;
using System.IO;

namespace OpenCloud.Core.IO;

/// <summary>
/// The retail disc's file index: <c>DATA.HD4</c> is a table of
/// 12-byte records <c>{ name offset, size, sector }</c>, where the first name offset is also the
/// size of the table, and <c>DATA.DAT</c> holds the files at 2048-byte sectors. Names use '\'
/// separators and are matched case-insensitively.
/// </summary>
public sealed class DataDatSource : IFileSource
{
    public const int SectorSize = 2048;
    private const int RecordSize = 12;

    private readonly IByteRange _data;
    private readonly IDisposable? _owner;
    private readonly Dictionary<string, (long Offset, int Size)> _files = new(VfsPath.Comparer);

    public DataDatSource(byte[] header, IByteRange data, string description, IDisposable? owner = null)
    {
        _data = data;
        _owner = owner;
        Description = description;
        Parse(header);
    }

    /// <summary>Opens a <c>DATA.HD4</c> with its sibling <c>DATA.DAT</c>.</summary>
    public static DataDatSource Open(string headerPath)
    {
        string dataPath = FindSibling(headerPath, "DATA.DAT")
            ?? throw new FileNotFoundException("DATA.DAT not found beside the index.", headerPath);
        var data = new FileByteRange(dataPath);
        return new DataDatSource(File.ReadAllBytes(headerPath), data, $"DATA.HD4 {headerPath}", data);
    }

    public string Description { get; }

    public int Count => _files.Count;

    public bool Contains(string path) => _files.ContainsKey(path);

    public IEnumerable<string> EnumerateFiles() => _files.Keys;

    public byte[]? TryRead(string path)
    {
        if (!_files.TryGetValue(path, out var file))
            return null;
        var buffer = new byte[file.Size];
        _data.Read(file.Offset, buffer);
        return buffer;
    }

    private void Parse(byte[] header)
    {
        if (header.Length < RecordSize)
            throw new InvalidDataException("DATA.HD4 is too small.");

        int count = LittleEndian.I32(header, 0) / RecordSize;
        if (count <= 0 || count * RecordSize > header.Length)
            throw new InvalidDataException("DATA.HD4 record table is malformed.");

        for (int i = 0; i < count; i++)
        {
            int at = i * RecordSize;
            int nameOffset = LittleEndian.I32(header, at);
            int size = LittleEndian.I32(header, at + 4);
            long offset = (long)LittleEndian.I32(header, at + 8) * SectorSize;
            if (nameOffset <= 0 || nameOffset >= header.Length || size < 0 || offset + size > _data.Length)
                continue;

            string name = VfsPath.Normalize(GameText.ReadCString(header, nameOffset));
            if (name.Length > 0)
                _files.TryAdd(name, (offset, size));
        }
    }

    internal static string? FindSibling(string path, string name)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory == null)
            return null;
        foreach (var candidate in Directory.EnumerateFiles(directory))
        {
            if (string.Equals(Path.GetFileName(candidate), name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }

    public void Dispose() => _owner?.Dispose();
}
