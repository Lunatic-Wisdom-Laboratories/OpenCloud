using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using OpenCloud.Core.IO;

namespace OpenCloud.Assets.Textures;

/// <summary>
/// Turns game textures into Godot <see cref="ImageTexture"/>s at run time. Reads both forms the
/// user's data can take:
/// <list type="bullet">
/// <item>retail IMG/IM2/IM3 archives and TIM2 files (from the disc, <c>DATA.DAT</c> or PACKs);</item>
/// <item>converted PNGs and their schema 3 texture documents from <c>data.zip</c>.</item>
/// </list>
/// </summary>
public sealed class TextureLoader
{
    private readonly VirtualFileSystem _vfs;

    public TextureLoader(VirtualFileSystem vfs)
    {
        _vfs = vfs;
    }

    /// <summary>Builds a texture from a decoded TIM2 picture, with Godot-generated mipmaps.</summary>
    public static ImageTexture CreateTexture(Tim2Picture picture, bool mipmaps = true)
    {
        var level = picture.Levels.Count > 0
            ? picture.Levels[0]
            : new RgbaLevel(picture.Width, picture.Height, new byte[picture.Width * picture.Height * 4]);
        var image = Image.CreateFromData(level.Width, level.Height, false, Image.Format.Rgba8, level.Pixels);
        if (mipmaps)
            image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>Decodes a PNG held in memory.</summary>
    public static ImageTexture? CreateTextureFromPng(byte[] png, bool mipmaps = true)
    {
        var image = new Image();
        if (image.LoadPngFromBuffer(png) != Error.Ok)
            return null;
        if (mipmaps)
            image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>Registers every picture of an IMG archive. Additional pictures of one entry get a numeric suffix.</summary>
    public static void AddImgArchive(TextureLibrary library, byte[] data)
    {
        var archive = ImgArchive.Parse(data);
        foreach (var entry in archive.Entries)
        {
            if (entry.IsScript)
                continue;

            List<Tim2Picture> pictures;
            try
            {
                pictures = archive.DecodePictures(entry);
            }
            catch (Exception exception) when (exception is System.IO.InvalidDataException or ArgumentOutOfRangeException)
            {
                GD.PushWarning($"Texture '{entry.Name}': {exception.Message}");
                continue;
            }

            // CLAMP register: WMS in bits 0-1, WMT in bits 2-3; 1 clamps that axis.
            bool clampU = (entry.Clamp & 0x3) == 1;
            bool clampV = ((entry.Clamp >> 2) & 0x3) == 1;
            for (int i = 0; i < pictures.Count; i++)
            {
                string name = i == 0 ? entry.Name : $"{entry.Name}#{i}";
                library.Add(new GameTexture(name, CreateTexture(pictures[i]), pictures[i].HasTransparency, clampU, clampV, entry.Block));
            }
        }
    }

    /// <summary>Registers the pictures of a standalone TIM2 file under its file stem.</summary>
    public static void AddTim2(TextureLibrary library, string name, byte[] data)
    {
        var pictures = Tim2Decoder.Decode(data);
        for (int i = 0; i < pictures.Count; i++)
        {
            string pictureName = i == 0 ? name : $"{name}#{i}";
            library.Add(new GameTexture(pictureName, CreateTexture(pictures[i]), pictures[i].HasTransparency, false, false, 0));
        }
    }

    /// <summary>
    /// Registers textures from any supported file in the virtual file system: IMG archives, TIM2,
    /// PNG or a converted texture document (.json). Returns false when the file is missing or not
    /// a texture.
    /// </summary>
    public bool LoadInto(TextureLibrary library, string path)
    {
        path = VfsPath.Normalize(path);
        string extension = VfsPath.GetExtension(path);
        if (extension == "json")
            return LoadTextureDocument(library, path);

        var data = _vfs.TryRead(path);
        if (data == null)
            return false;

        string stem = System.IO.Path.GetFileNameWithoutExtension(VfsPath.GetFileName(path));
        if (ImgArchive.IsImg(data))
        {
            AddImgArchive(library, data);
            return true;
        }
        if (Tim2Decoder.IsTim2(data))
        {
            AddTim2(library, stem, data);
            return true;
        }
        if (extension == "png")
        {
            var texture = CreateTextureFromPng(data);
            if (texture == null)
                return false;
            library.Add(new GameTexture(stem, texture, texture.GetImage().DetectAlpha() != Image.AlphaMode.None, false, false, 0));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Reads a schema 3 texture document and its PNG levels. A relative name the archive does
    /// not hold is looked up again below <c>textures/</c>, as the port's loader does.
    /// </summary>
    public bool LoadTextureDocument(TextureLibrary library, string path)
    {
        var json = _vfs.TryRead(path);
        if (json == null && !path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
        {
            path = "textures/" + path;
            json = _vfs.TryRead(path);
        }
        if (json == null)
            return false;

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("entries", out var entries))
            return false;

        string directory = VfsPath.GetDirectory(path);
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("name", out var nameElement) || !entry.TryGetProperty("pictures", out var pictures))
                continue;
            string name = nameElement.GetString() ?? string.Empty;
            int block = entry.TryGetProperty("block", out var blockElement) ? blockElement.GetInt32() : 0;

            int index = 0;
            foreach (var picture in pictures.EnumerateArray())
            {
                if (picture.TryGetProperty("levels", out var levels) && levels.GetArrayLength() > 0)
                {
                    var png = _vfs.TryRead(VfsPath.Combine(directory, levels[0].GetString() ?? string.Empty));
                    var texture = png == null ? null : CreateTextureFromPng(png);
                    if (texture != null)
                    {
                        bool clampU = false, clampV = false;
                        if (picture.TryGetProperty("sampler", out var sampler))
                        {
                            clampU = sampler.TryGetProperty("wrap_u", out var u) && u.GetString() == "clamp";
                            clampV = sampler.TryGetProperty("wrap_v", out var v) && v.GetString() == "clamp";
                        }
                        string pictureName = index == 0 ? name : $"{name}#{index}";
                        library.Add(new GameTexture(pictureName, texture, texture.GetImage().DetectAlpha() != Image.AlphaMode.None, clampU, clampV, block));
                    }
                }
                index++;
            }
        }
        return true;
    }
}
