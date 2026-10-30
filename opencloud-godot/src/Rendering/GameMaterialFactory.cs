using Godot;
using OpenCloud.Assets.Textures;

namespace OpenCloud.Rendering;

/// <summary>
/// Builds Godot materials for game models. The PS2 renderer draws model faces from both sides
/// unless a model asks for culling, modulates the texture with the material diffuse colour and
/// optionally with per-vertex colours, and blends textures that carry alpha.
/// </summary>
public static class GameMaterialFactory
{
    /// <param name="diffuse">Material diffuse colour, already in 0..1.</param>
    /// <param name="texture">Resolved texture, or null for an untextured material.</param>
    /// <param name="vertexColours">True when the surface carries per-vertex colours.</param>
    public static StandardMaterial3D Create(Color diffuse, GameTexture? texture, bool vertexColours)
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = diffuse,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            VertexColorUseAsAlbedo = vertexColours,
            VertexColorIsSrgb = false,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            Roughness = 1.0f,
        };

        if (texture != null)
        {
            material.AlbedoTexture = texture.Texture;
            material.TextureRepeat = !(texture.ClampU && texture.ClampV);
            if (texture.HasTransparency)
                material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass;
        }

        if (diffuse.A < 0.999f)
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass;

        return material;
    }

    /// <summary>
    /// Brings a material read from a converted model to what <see cref="Create"/> builds: no
    /// specular term, linear vertex colours, mipmapped filtering, and translucency with a depth
    /// pre-pass. Colours, textures, culling and lighting mode come from the file.
    /// </summary>
    public static void ApplyGameConventions(StandardMaterial3D material)
    {
        material.SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled;
        material.Metallic = 0.0f;
        material.Roughness = 1.0f;
        material.VertexColorIsSrgb = false;
        material.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps;
        if (material.Transparency is BaseMaterial3D.TransparencyEnum.Alpha)
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass;
    }

    /// <summary>Material for geometry whose texture could not be found: visible, but clearly marked.</summary>
    public static StandardMaterial3D CreateMissing(Color diffuse, bool vertexColours)
    {
        var material = Create(diffuse, null, vertexColours);
        material.AlbedoColor = diffuse * new Color(1.0f, 0.5f, 1.0f);
        return material;
    }
}
