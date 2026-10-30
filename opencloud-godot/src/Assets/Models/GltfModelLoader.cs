using System;
using System.Text;
using System.Text.Json.Nodes;
using Godot;
using OpenCloud.Core.IO;

namespace OpenCloud.Assets.Models;

/// <summary>
/// Loads the converted models of <c>data.zip</c>: standard glTF 2.0
/// whose buffer (<c>&lt;file&gt;.gltf.bin</c>) and PNG images are separate archive entries
/// referenced by relative, percent-encoded URIs. Godot's importer cannot reach inside the
/// archive, so those references are resolved through the virtual file system and embedded as
/// <c>data:</c> URIs before the document is handed to <see cref="GltfDocument"/>.
/// </summary>
public sealed class GltfModelLoader
{
    private readonly VirtualFileSystem _vfs;

    public GltfModelLoader(VirtualFileSystem vfs)
    {
        _vfs = vfs;
    }

    public Node3D Load(string path)
    {
        path = VfsPath.Normalize(path);
        var bytes = _vfs.ReadAllBytes(path);

        byte[] document = VfsPath.GetExtension(path) == "glb" ? bytes : EmbedExternalResources(path, bytes);

        var gltf = new GltfDocument();
        var state = new GltfState();
        var error = gltf.AppendFromBuffer(document, string.Empty, state);
        if (error != Error.Ok)
            throw new System.IO.InvalidDataException($"Godot could not parse glTF '{path}': {error}.");

        var scene = gltf.GenerateScene(state) as Node3D
            ?? throw new System.IO.InvalidDataException($"glTF '{path}' produced no 3D scene.");
        scene.Name = MdsModelLoader.SafeNodeName(VfsPath.GetFileName(path));
        return scene;
    }

    private byte[] EmbedExternalResources(string path, byte[] json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new System.IO.InvalidDataException($"glTF '{path}' is not a JSON object.");
        string directory = VfsPath.GetDirectory(path);

        Embed(root["buffers"] as JsonArray, directory, "application/octet-stream", path);
        Embed(root["images"] as JsonArray, directory, "image/png", path);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private void Embed(JsonArray? items, string directory, string mimeType, string path)
    {
        if (items == null)
            return;
        foreach (var item in items)
        {
            if (item is not JsonObject obj || obj["uri"]?.GetValue<string>() is not { } uri || uri.StartsWith("data:", StringComparison.Ordinal))
                continue;

            string resolved = VfsPath.Combine(directory, Uri.UnescapeDataString(uri));
            var data = _vfs.TryRead(resolved);
            if (data == null)
            {
                GD.PushWarning($"glTF '{path}': missing external resource '{resolved}'.");
                continue;
            }
            obj["uri"] = $"data:{mimeType};base64,{Convert.ToBase64String(data)}";
            if (mimeType.StartsWith("image/", StringComparison.Ordinal))
                obj["mimeType"] = mimeType;
        }
    }
}
