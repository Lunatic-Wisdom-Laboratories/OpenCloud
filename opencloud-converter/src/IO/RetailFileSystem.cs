using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCloud.Core.IO;

namespace OpenCloud.Core.IO;

/// <summary>
/// The user's original game data as the converter reads it: a disc image (with the files packed
/// in its <c>DATA.HD4</c>/<c>DATA.DAT</c>), a <c>DATA.HD4</c> index, or an extracted folder.
/// Paths can reach into PACK archives: <c>chara/c01_base.chr/c01_base.mds</c> reads the member
/// <c>c01_base.mds</c> of <c>chara/c01_base.chr</c>, at any nesting depth.
/// </summary>
public sealed class RetailFileSystem : IDisposable
{
    private const int PackCacheCapacity = 32;

    private readonly VirtualFileSystem _files = new();
    private readonly Dictionary<string, PackArchive?> _packCache = new(VfsPath.Comparer);
    private readonly object _lock = new();

    public IReadOnlyList<IFileSource> Sources => _files.Sources;

    public event Action<string>? Log
    {
        add => _files.Log += value;
        remove => _files.Log -= value;
    }

    /// <summary>
    /// Mounts a disc image, a <c>DATA.HD4</c> index or a folder. A folder that holds a disc image or
    /// <c>DATA.HD4</c> mounts those too, below the loose files beside them.
    /// </summary>
    public int Mount(string path)
    {
        path = Path.GetFullPath(path);
        if (Directory.Exists(path))
            return MountDirectory(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Game data path not found.", path);

        if (Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            return MountIso(path);
        if (string.Equals(Path.GetFileName(path), "DATA.HD4", StringComparison.OrdinalIgnoreCase))
        {
            Add(DataDatSource.Open(path));
            return 1;
        }
        throw new NotSupportedException($"Not a disc image, DATA.HD4 or folder: {path}");
    }

    private void Add(IFileSource source)
    {
        _files.Mount(source);
        lock (_lock)
            _packCache.Clear();
    }

    private int MountDirectory(string path)
    {
        int mounted = 0;
        foreach (var file in Directory.EnumerateFiles(path))
        {
            if (file.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                mounted += MountIso(file);
        }

        string? index = DataDatSource.FindSibling(Path.Combine(path, "DATA.HD4"), "DATA.HD4");
        if (index != null && DataDatSource.FindSibling(index, "DATA.DAT") != null)
        {
            Add(DataDatSource.Open(index));
            mounted++;
        }

        Add(new DirectorySource(path));
        return mounted + 1;
    }

    private int MountIso(string path)
    {
        var iso = new IsoSource(path);
        Add(iso);
        var data = iso.TryOpenDataIndex();
        if (data == null)
            return 1;
        Add(data);
        return 2;
    }

    /// <summary>True when a source holds the file. A path into a PACK opens the PACK to check its member.</summary>
    public bool Exists(string path)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
            return false;
        return _files.Exists(path) || (path.Contains('/') && TryReadFromPack(path) != null);
    }

    /// <summary>Reads a file, or returns null when no mounted source (or PACK inside one) holds it.</summary>
    public byte[]? TryRead(string path)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
            return null;
        return _files.TryRead(path) ?? TryReadFromPack(path);
    }

    public byte[] ReadAllBytes(string path) =>
        TryRead(path) ?? throw new FileNotFoundException($"'{path}' is not in the game data.", path);

    /// <summary>Opens a PACK archive by its path, caching recently used ones.</summary>
    public PackArchive? TryOpenPack(string path)
    {
        path = VfsPath.Normalize(path);
        lock (_lock)
        {
            if (_packCache.TryGetValue(path, out var cached))
                return cached;
        }

        var data = TryRead(path);
        var pack = data == null ? null : PackArchive.TryParse(data, $"pack {path}");
        lock (_lock)
        {
            if (_packCache.Count >= PackCacheCapacity)
                _packCache.Clear();
            _packCache[path] = pack;
        }
        return pack;
    }

    private byte[]? TryReadFromPack(string path)
    {
        var parts = path.Split('/');
        for (int split = parts.Length - 1; split >= 1; split--)
        {
            string container = string.Join('/', parts, 0, split);
            var pack = TryOpenPack(container);
            if (pack == null)
                continue;

            string member = string.Join('/', parts, split, parts.Length - split);
            if (split == parts.Length - 1)
                return pack.TryRead(member);

            // Nested PACK: read the next level as a member of this one.
            var inner = pack.TryRead(parts[split]);
            var innerPack = inner == null ? null : PackArchive.TryParse(inner);
            return innerPack == null ? null : ReadNested(innerPack, parts, split + 1);
        }
        return null;
    }

    private static byte[]? ReadNested(PackArchive pack, string[] parts, int index)
    {
        if (index == parts.Length - 1)
            return pack.TryRead(parts[index]);
        var inner = pack.TryRead(parts[index]);
        var innerPack = inner == null ? null : PackArchive.TryParse(inner);
        return innerPack == null ? null : ReadNested(innerPack, parts, index + 1);
    }

    /// <summary>Every file path of every mounted source, without duplicates. PACK members are not expanded.</summary>
    public IEnumerable<string> EnumerateFiles() => _files.EnumerateFiles();

    public IEnumerable<string> EnumerateFiles(params string[] extensions) => _files.EnumerateFiles(extensions);

    public void Dispose()
    {
        _files.Dispose();
        lock (_lock)
            _packCache.Clear();
    }
}
