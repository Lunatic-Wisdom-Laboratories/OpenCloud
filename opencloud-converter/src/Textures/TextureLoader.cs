using System;
using System.Collections.Generic;
using Godot;

namespace OpenCloud.Assets.Textures;

/// <summary>
/// Turns the game's IMG/IM2/IM3 archives and TIM2 files (from the disc, <c>DATA.DAT</c> or
/// PACKs) into Godot <see cref="ImageTexture"/>s, which the converter writes as PNG.
/// </summary>
public static class TextureLoader
{
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
}
