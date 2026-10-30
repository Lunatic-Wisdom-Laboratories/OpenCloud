using System.Collections.Generic;
using Godot;
using OpenCloud.Core.IO;

namespace OpenCloud.Assets.Models;

/// <summary>
/// Single entry point for loading a model by path: retail MDS files and PACK bundles go through
/// <see cref="MdsModelLoader"/>, converted glTF through <see cref="GltfModelLoader"/>.
/// </summary>
public sealed class GameModelLoader
{
    /// <summary>Extensions the loader can open as models.</summary>
    public static readonly IReadOnlySet<string> ModelExtensions = new HashSet<string> { "mds", "chr", "pcp", "gltf", "glb" };

    private readonly VirtualFileSystem _vfs;

    public GameModelLoader(VirtualFileSystem vfs)
    {
        _vfs = vfs;
        Mds = new MdsModelLoader(vfs);
        Gltf = new GltfModelLoader(vfs);
    }

    public MdsModelLoader Mds { get; }

    public GltfModelLoader Gltf { get; }

    public Node3D Load(string path) => VfsPath.GetExtension(path) switch
    {
        "gltf" or "glb" => Gltf.Load(path),
        _ => Mds.Load(path),
    };

    /// <summary>Returns the first candidate path that exists in the mounted data, or null.</summary>
    public string? FindFirst(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (_vfs.Exists(candidate))
                return VfsPath.Normalize(candidate);
        }
        return null;
    }
}
