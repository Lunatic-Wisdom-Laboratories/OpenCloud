using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenCloud.Core.IO;

/// <summary>
/// Single namespace over every mounted game data container. Later mounts take priority, so a
/// datapack mounted after <c>data.zip</c> overrides its files.
/// </summary>
public sealed class VirtualFileSystem : IDisposable
{
    /// <summary>The report the converter writes at the root of the data it converts.</summary>
    public const string ConvertedDataMarker = "opencloud-data.json";

    private readonly List<IFileSource> _sources = new();
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
        }
        Log?.Invoke($"Mounted {source.Description}");
    }

    /// <summary>
    /// Mounts converted game data: a ZIP of it (<c>data.zip</c>, datapacks) or a directory. A
    /// directory that holds a <c>data.zip</c> mounts it too, below the loose files beside it.
    /// The original disc formats are refused: the converter turns them into this data once.
    /// Returns the number of sources mounted.
    /// </summary>
    public int MountAuto(string path)
    {
        path = Path.GetFullPath(path);
        if (Directory.Exists(path))
        {
            if (!File.Exists(Path.Combine(path, ConvertedDataMarker)) &&
                Directory.EnumerateFiles(path).Any(f => f.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("DATA.HD4", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException($"{path} holds original game data. Convert it once with opencloud-converter and point the engine at the converted folder.");
            int mounted = 0;
            string zip = Path.Combine(path, "data.zip");
            if (File.Exists(zip))
            {
                Mount(new ZipArchiveSource(zip));
                mounted++;
            }
            Mount(new DirectorySource(path));
            return mounted + 1;
        }
        if (!File.Exists(path))
            throw new FileNotFoundException("Game data path not found.", path);

        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".zip")
        {
            Mount(new ZipArchiveSource(path));
            return 1;
        }
        if (extension == ".iso" || string.Equals(Path.GetFileName(path), "DATA.HD4", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{path} is original game data. Convert it once with opencloud-converter and point the engine at the converted folder.");
        throw new NotSupportedException($"Unrecognised game data container: {path}");
    }

    /// <summary>True when a source holds the file.</summary>
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
        return false;
    }

    /// <summary>Reads a file, or returns null when no mounted source holds it.</summary>
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

        return null;
    }

    public byte[] ReadAllBytes(string path) =>
        TryRead(path) ?? throw new FileNotFoundException($"'{path}' is not in any mounted game data source.", path);

    /// <summary>Every file path of every mounted source, without duplicates.</summary>
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
        }
    }
}
