using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenCloud.Core.IO;

/// <summary>
/// Single namespace over every mounted game data container. Later mounts take priority, so a
/// datapack mounted after <c>data.zip</c> overrides its files (docs/PC.md datapacks). Paths can
/// reach into PACK archives: <c>chara/c01_base.chr/c01_base.mds</c> reads the member
/// <c>c01_base.mds</c> of <c>chara/c01_base.chr</c>, at any nesting depth.
/// </summary>
public sealed class VirtualFileSystem : IDisposable
{
    private const int PackCacheCapacity = 32;

    private readonly List<IFileSource> _sources = new();
    private readonly Dictionary<string, PackArchive?> _packCache = new(VfsPath.Comparer);
    private readonly object _lock = new();

    /// <summary>Mounted sources, highest priority first.</summary>
    public IReadOnlyList<IFileSource> Sources
    {
        get
        {
            lock (_lock)
                return _sources.ToArray();
        }
    }

    public event Action<string>? Log;

    /// <summary>Mounts a source above every source mounted before it.</summary>
    public void Mount(IFileSource source)
    {
        lock (_lock)
        {
            _sources.Insert(0, source);
            _packCache.Clear();
        }
        Log?.Invoke($"Mounted {source.Description}");
    }

    /// <summary>
    /// Detects what a path holds and mounts it: a ZIP (<c>data.zip</c>, datapacks), an ISO image,
    /// a <c>DATA.HD4</c> index, or a directory. A directory that holds <c>data.zip</c>,
    /// <c>DATA.HD4</c> or a disc image mounts those too, below the loose files beside them.
    /// Returns the number of sources mounted.
    /// </summary>
    public int MountAuto(string path)
    {
        path = Path.GetFullPath(path);
        if (Directory.Exists(path))
            return MountDirectory(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Game data path not found.", path);

        string name = Path.GetFileName(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".zip")
        {
            Mount(new ZipArchiveSource(path));
            return 1;
        }
        if (extension == ".iso")
            return MountIso(path);
        if (string.Equals(name, "DATA.HD4", StringComparison.OrdinalIgnoreCase))
        {
            Mount(DataDatSource.Open(path));
            return 1;
        }
        throw new NotSupportedException($"Unrecognised game data container: {path}");
    }

    private int MountDirectory(string path)
    {
        int mounted = 0;
        foreach (var file in Directory.EnumerateFiles(path))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                mounted += MountIso(file);
        }

        string? index = DataDatSource.FindSibling(Path.Combine(path, "DATA.HD4"), "DATA.HD4");
        if (index != null && DataDatSource.FindSibling(index, "DATA.DAT") != null)
        {
            Mount(DataDatSource.Open(index));
            mounted++;
        }

        string? zip = DataDatSource.FindSibling(Path.Combine(path, "data.zip"), "data.zip");
        if (zip != null)
        {
            Mount(new ZipArchiveSource(zip));
            mounted++;
        }

        Mount(new DirectorySource(path));
        return mounted + 1;
    }

    private int MountIso(string path)
    {
        var iso = new IsoSource(path);
        Mount(iso);
        var data = iso.TryOpenDataIndex();
        if (data == null)
            return 1;
        Mount(data);
        return 2;
    }

    /// <summary>True when a source holds the file. A path into a PACK opens the PACK to check its member.</summary>
    public bool Exists(string path)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
            return false;
        foreach (var source in Sources)
        {
            if (source.Contains(path))
                return true;
        }
        return path.Contains('/') && TryReadFromPack(path) != null;
    }

    /// <summary>Reads a file, or returns null when no mounted source (or PACK inside one) holds it.</summary>
    public byte[]? TryRead(string path)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
            return null;

        foreach (var source in Sources)
        {
            var data = source.TryRead(path);
            if (data != null)
                return data;
        }

        return TryReadFromPack(path);
    }

    public byte[] ReadAllBytes(string path) =>
        TryRead(path) ?? throw new FileNotFoundException($"'{path}' is not in any mounted game data source.", path);

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
    public IEnumerable<string> EnumerateFiles()
    {
        var seen = new HashSet<string>(VfsPath.Comparer);
        foreach (var source in Sources)
        {
            foreach (var file in source.EnumerateFiles())
            {
                if (seen.Add(file))
                    yield return file;
            }
        }
    }

    public IEnumerable<string> EnumerateFiles(params string[] extensions)
    {
        var wanted = new HashSet<string>(extensions.Select(e => e.TrimStart('.').ToLowerInvariant()));
        return EnumerateFiles().Where(f => wanted.Contains(VfsPath.GetExtension(f)));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var source in _sources)
                source.Dispose();
            _sources.Clear();
            _packCache.Clear();
        }
    }
}
