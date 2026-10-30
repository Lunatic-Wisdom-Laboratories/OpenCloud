using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenCloud.Core;
using OpenCloud.Core.IO;

namespace OpenCloud.Converter.Export;

/// <summary>
/// The converted data folder. Paths are data-root relative and <see cref="VfsPath"/> normalized;
/// a path is written once per run, and a second file for the same path is refused.
/// </summary>
public sealed class OutputTree
{
    private readonly HashSet<string> _written = new(VfsPath.Comparer);

    public OutputTree(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public int Files => _written.Count;

    public long Bytes { get; private set; }

    public bool Contains(string path) => _written.Contains(VfsPath.Normalize(path));

    public void Write(string path, byte[] data)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0 || path.Split('/').Contains(".."))
            throw new ArgumentException($"Invalid output path '{path}'.");
        if (!_written.Add(path))
            throw new InvalidOperationException($"'{path}' was already written by this conversion.");
        string full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
        Bytes += data.Length;
    }

    public void WriteJson(string path, JsonObject json) =>
        Write(path, System.Text.Encoding.UTF8.GetBytes(json.ToJsonString(JsonValues.Pretty) + "\n"));

    /// <summary>
    /// A file name for a converted asset that no other asset of this run took:
    /// <c>name.glb</c>, else <c>name.&lt;original extension&gt;.glb</c>, else numbered.
    /// </summary>
    public string Unique(string directory, string stem, string originalExtension, string extension)
    {
        string Candidate(string name) => VfsPath.Combine(directory, name);
        string safe = SafeFileName(stem);
        string path = Candidate(safe + extension);
        if (!Contains(path))
            return path;
        path = Candidate($"{safe}.{originalExtension}{extension}");
        for (int i = 2; Contains(path); i++)
            path = Candidate($"{safe}.{originalExtension}.{i}{extension}");
        return path;
    }

    /// <summary>Replaces characters file systems refuse.</summary>
    public static string SafeFileName(string name)
    {
        foreach (char c in "<>:\"/\\|?*")
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "unnamed" : name;
    }
}
