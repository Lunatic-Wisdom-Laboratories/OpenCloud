using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenCloud.Assets.Textures;
using OpenCloud.Core;
using OpenCloud.Core.IO;
using OpenCloud.Core.Scripts;

namespace OpenCloud.Assets.Models;

public sealed partial class MdsModelLoader
{
    /// <summary>Meta key on a character model: names of its deform (<c>VERTEX_ANIME</c>) frames.</summary>
    public const string DeformFramesMeta = ModelMeta.DeformFrames;

    /// <summary>Meta key on a model: scene-space base matrix (Transform3D) of every bone.</summary>
    public const string BaseMatricesMeta = ModelMeta.BaseMatrices;

    /// <summary>Tag table of the costume reader (character <c>skin_tag</c>); binary scripts index it.</summary>
    public static readonly IReadOnlyList<string> SkinTags = new[] { "IMG", "IMG_END", "MODEL", "MOTION" };

    /// <summary>
    /// True when a pack is a costume (<c>CCharacter2::LoadSkin</c>) rather than a character: read
    /// with the skin tags, its <c>info.cfg</c> names a model and a <c>MOTION slot, ?, "x.bbp", "x.wgt"</c>
    /// whose weight file the pack holds, and read as a character it has no <c>KEY</c> actions.
    /// </summary>
    public static bool IsCostume(PackArchive pack)
    {
        if (pack.TryRead(CharacterInfo.FileName) is not { } infoData)
            return false;
        try
        {
            var commands = GameScript.Parse(infoData, SkinTags);
            string? model = commands.LastOrDefault(c => c.Tag == "MODEL")?.Arg(0).AsString;
            string? weights = commands.LastOrDefault(c => c.Tag == "MOTION")?.Arg(3).AsString;
            if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(weights) || !pack.Contains(model) || !pack.Contains(weights))
                return false;
            return CharacterInfo.Parse(infoData).Motions.Values.All(m => m.Keys.Count == 0);
        }
        catch (Exception exception) when (exception is System.IO.InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds a costume pack as <c>CCharacter2::LoadSkin</c> reads it (character <c>_SKIN_MODEL</c>,
    /// <c>_SKIN_MOTION</c>): the <c>MODEL</c> scene with every frame that has weights in the
    /// <c>MOTION</c> line's <c>.wgt</c> skinned to the costume's own frames at its <c>.bbp</c>
    /// bind pose. The engine moves these visuals onto the character it dresses
    /// (<c>CreateChangeFrame</c>), skinning a frame only when the character deforms it.
    /// </summary>
    public Node3D LoadCostume(PackArchive pack, string name, TextureLibrary? sharedTextures = null)
    {
        var infoData = pack.TryRead(CharacterInfo.FileName)
            ?? throw new System.IO.InvalidDataException($"'{name}' has no {CharacterInfo.FileName}.");

        // A costume's info.cfg is read with the skin tags; in text form the tags are the same.
        var commands = GameScript.Parse(infoData, SkinTags);
        string? modelName = commands.LastOrDefault(c => c.Tag == "MODEL")?.Arg(0).AsString;
        var motion = commands.LastOrDefault(c => c.Tag == "MOTION");
        if (modelName == null || pack.TryRead(modelName) is not { } modelData || !MdsFile.LooksLikeMds(modelData))
            throw new System.IO.InvalidDataException($"'{name}': {CharacterInfo.FileName} names no costume model.");

        var mds = MdsFile.Parse(modelData);
        var weightLists = ReadKeyLists(pack, NonEmpty(motion?.Arg(3).AsString), name);
        var bindPose = NonEmpty(motion?.Arg(2).AsString) is { } bbpName && pack.TryRead(bbpName) is { } bbp
            ? MotionFile.ParseBindPose(bbp)
            : null;

        var textures = new TextureLibrary();
        if (sharedTextures != null)
            textures.AddRange(sharedTextures);
        foreach (var entry in pack.Entries)
            TryAddTextures(textures, entry.Name, pack.GetSpan(entry));

        var skinning = weightLists == null ? null : new SkinningData { WeightLists = weightLists, BindPose = bindPose };
        var root = new Node3D { Name = SafeNodeName(name) };
        var model = BuildScene(mds, modelName, textures, skinning);
        root.AddChild(model);

        // CreateChangeFrame copies the costume's bind matrix of a frame over the character's.
        if (bindPose != null)
        {
            var skeleton = model.GetNode<Skeleton3D>(SkeletonNodeName);
            model.SetMeta(BaseMatricesMeta, new Godot.Collections.Array(BaseMatrices(skeleton, bindPose).Select(t => Variant.From(t))));
        }
        return root;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
