using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using OpenCloud.Core;
using OpenCloud.Core.IO;

namespace OpenCloud.Assets.Textures;

/// <summary>One picture of a converted texture file: a PNG beside the document and its sampler state.</summary>
/// <param name="Name">Name materials and scripts use to find it (an IMG entry name, or a TIM2 file stem).</param>
/// <param name="Png">PNG file name, relative to the document.</param>
/// <param name="Alpha">The picture has transparent texels, so materials using it blend.</param>
/// <param name="Block">IMG3 texture group.</param>
public sealed record TextureEntry(string Name, string Png, bool Alpha, bool ClampU, bool ClampV, int Block);

/// <summary>
/// A converted texture file (<c>&lt;name&gt;.textures.json</c>): the pictures of one original
/// IMG archive or TIM2 file, each a PNG beside the document.
/// </summary>
public sealed class TextureDocument
{
    public const string Format = "opencloud.textures";
    public const int Version = 1;
    public const string Extension = ".textures.json";

    public string Source { get; init; } = string.Empty;
    public List<TextureEntry> Textures { get; } = new();

    public JsonObject ToJson()
    {
        var json = JsonValues.Header(Format, Version);
        json["source"] = Source;
        json["textures"] = new JsonArray(Textures.Select(t =>
        {
            var entry = new JsonObject { ["name"] = t.Name, ["png"] = t.Png };
            if (t.Alpha)
                entry["alpha"] = true;
            if (t.ClampU)
                entry["clamp_u"] = true;
            if (t.ClampV)
                entry["clamp_v"] = true;
            if (t.Block != 0)
                entry["block"] = t.Block;
            return (JsonNode?)entry;
        }).ToArray());
        return json;
    }

    public static TextureDocument FromJson(JsonObject json, string path)
    {
        JsonValues.CheckFormat(json, Format, Version, path);
        var document = new TextureDocument { Source = JsonValues.Str(json, "source") ?? string.Empty };
        foreach (var entry in JsonValues.Objects(json, "textures"))
        {
            document.Textures.Add(new TextureEntry(
                JsonValues.Str(entry, "name") ?? string.Empty,
                JsonValues.Str(entry, "png") ?? string.Empty,
                JsonValues.Bool(entry, "alpha"),
                JsonValues.Bool(entry, "clamp_u"),
                JsonValues.Bool(entry, "clamp_v"),
                JsonValues.Int(entry, "block")));
        }
        return document;
    }

    /// <summary>Reads a texture document and registers its pictures. Returns false when the file is missing.</summary>
    public static bool LoadInto(VirtualFileSystem vfs, TextureLibrary library, string path)
    {
        path = VfsPath.Normalize(path);
        var data = vfs.TryRead(path);
        if (data == null)
            return false;
        var document = FromJson(JsonValues.Parse(data, path), path);
        string directory = VfsPath.GetDirectory(path);
        foreach (var entry in document.Textures)
        {
            var png = vfs.TryRead(VfsPath.Combine(directory, entry.Png));
            var image = new Image();
            if (png == null || image.LoadPngFromBuffer(png) != Error.Ok)
            {
                GD.PushWarning($"{path}: picture {entry.Name} ({entry.Png}) could not be read.");
                continue;
            }
            image.GenerateMipmaps();
            library.Add(new GameTexture(entry.Name, ImageTexture.CreateFromImage(image), entry.Alpha, entry.ClampU, entry.ClampV, entry.Block));
        }
        return true;
    }
}
