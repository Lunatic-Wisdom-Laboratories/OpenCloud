using System;
using System.Collections.Generic;
using System.IO;

namespace OpenCloud.Core.IO;

/// <summary>
/// A folder of extracted or converted files. The tree is indexed once so that lookups are
/// case-insensitive on every host OS, matching the retail <c>strcasecmp</c> search.
/// </summary>
public sealed class DirectorySource : IFileSource
{
    private readonly Dictionary<string, string> _files = new(VfsPath.Comparer);

    public DirectorySource(string root)
    {
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root))
            throw new DirectoryNotFoundException(Root);

        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            string relative = VfsPath.Normalize(Path.GetRelativePath(Root, file));
            _files.TryAdd(relative, file);
        }
    }

    public string Root { get; }

    public string Description => $"directory {Root} ({_files.Count} files)";

    public bool Contains(string path) => _files.ContainsKey(path);

    public byte[]? TryRead(string path) =>
        _files.TryGetValue(path, out var full) ? File.ReadAllBytes(full) : null;

    public IEnumerable<string> EnumerateFiles() => _files.Keys;

    public void Dispose()
    {
    }
}
