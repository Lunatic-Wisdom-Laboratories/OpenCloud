using System;
using System.Collections.Generic;
using Godot;

namespace OpenCloud.Assets.Textures;

/// <summary>A texture registered under its retail name, with the sampler state the GS would use.</summary>
public sealed record GameTexture(string Name, Texture2D Texture, bool HasTransparency, bool ClampU, bool ClampV, int Block);

/// <summary>
/// Name-indexed set of textures, the Godot-side counterpart of <c>mgCTextureManager</c>:
/// materials name their texture (<c>MDT_MATERIAL_::texture</c>) and are resolved here. The
/// retail hash lookup is case-sensitive, so exact matches win and a case-insensitive match is
/// the fallback.
/// </summary>
public sealed class TextureLibrary
{
    private readonly Dictionary<string, GameTexture> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GameTexture> _folded = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _exact.Count;

    public IEnumerable<GameTexture> Textures => _exact.Values;

    /// <summary>Registers a texture. As in <c>SearchTexture</c>, the first texture of a name is kept.</summary>
    public void Add(GameTexture texture)
    {
        if (_exact.TryAdd(texture.Name, texture))
            _folded.TryAdd(texture.Name, texture);
    }

    public void AddRange(TextureLibrary other)
    {
        foreach (var texture in other.Textures)
            Add(texture);
    }

    public GameTexture? Find(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        if (_exact.TryGetValue(name, out var texture) || _folded.TryGetValue(name, out texture))
            return texture;
        return null;
    }
}
