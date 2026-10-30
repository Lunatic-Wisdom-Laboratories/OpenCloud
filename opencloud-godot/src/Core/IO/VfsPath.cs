using System;
using System.Collections.Generic;

namespace OpenCloud.Core.IO;

/// <summary>
/// Canonical form of paths inside the virtual file system: '/' separators, no device prefix
/// (<c>cdrom0:</c>, <c>host0:</c>, <c>pfs0:</c>), no ISO version suffix (<c>;1</c>), no leading
/// slash and no <c>.</c> / <c>..</c> components. Lookups are case-insensitive, as the retail
/// <c>SearchFile</c> uses <c>strcasecmp</c>.
/// </summary>
public static class VfsPath
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        path = path.Replace('\\', '/');

        int colon = path.IndexOf(':');
        if (colon >= 0)
            path = path[(colon + 1)..];

        int version = path.LastIndexOf(';');
        if (version >= 0)
            path = path[..version];

        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    public static string Combine(string directory, string relative)
    {
        if (string.IsNullOrEmpty(directory))
            return Normalize(relative);
        return Normalize(directory + "/" + relative);
    }

    public static string GetDirectory(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    public static string GetFileName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>Extension without the dot, lower-case; empty when there is none.</summary>
    public static string GetExtension(string path)
    {
        string name = GetFileName(path);
        int dot = name.LastIndexOf('.');
        return dot < 0 ? string.Empty : name[(dot + 1)..].ToLowerInvariant();
    }

    public static string ChangeExtension(string path, string extension)
    {
        string name = GetFileName(path);
        int dot = name.LastIndexOf('.');
        string stem = dot < 0 ? path : path[..(path.Length - name.Length + dot)];
        return stem + "." + extension.TrimStart('.');
    }
}
