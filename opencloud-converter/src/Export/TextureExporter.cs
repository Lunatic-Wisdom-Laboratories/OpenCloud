using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenCloud.Assets.Models;
using OpenCloud.Assets.Textures;
using OpenCloud.Core.IO;

namespace OpenCloud.Converter.Export;

/// <summary>
/// Writes the pictures of an IMG archive or TIM2 file as PNGs and a <see cref="TextureDocument"/>:
/// <c>dir/menu.img</c> → <c>dir/menu.textures.json</c> with <c>dir/menu.0.png</c>, <c>dir/menu.1.png</c>...
/// </summary>
public static class TextureExporter
{
    /// <summary>Returns the document path, or null when the data holds no picture.</summary>
    public static string? Export(OutputTree output, string sourcePath, byte[] data, string outputDirectory, string? stem = null)
    {
        var library = new TextureLibrary();
        MdsModelLoader.TryAddTextures(library, VfsPath.GetFileName(sourcePath), data);
        if (library.Count == 0)
            return null;

        stem ??= System.IO.Path.GetFileNameWithoutExtension(VfsPath.GetFileName(sourcePath));
        string documentPath = output.Unique(outputDirectory, stem, VfsPath.GetExtension(sourcePath), TextureDocument.Extension);
        string pngStem = VfsPath.GetFileName(documentPath)[..^TextureDocument.Extension.Length];

        var document = new TextureDocument { Source = sourcePath };
        int index = 0;
        foreach (var texture in library.Textures)
        {
            var image = texture.Texture.GetImage();
            if (image == null)
                continue;
            if (image.HasMipmaps())
            {
                image = (Image)image.Duplicate();
                image.ClearMipmaps();
            }
            string png = $"{pngStem}.{index++}.png";
            output.Write(VfsPath.Combine(outputDirectory, png), image.SavePngToBuffer());
            document.Textures.Add(new TextureEntry(texture.Name, png, texture.HasTransparency, texture.ClampU, texture.ClampV, texture.Block));
        }
        output.WriteJson(documentPath, document.ToJson());
        return documentPath;
    }
}
