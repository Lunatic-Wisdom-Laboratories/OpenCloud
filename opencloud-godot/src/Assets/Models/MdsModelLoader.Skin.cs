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
    public const string DeformFramesMeta = "opencloud_deform_frames";

    /// <summary>Meta key on a character model: scene-space base matrix (Transform3D) of every bone.</summary>
    public const string BaseMatricesMeta = "opencloud_base_matrices";

    /// <summary>Tag table of the costume reader (character <c>skin_tag</c>); binary scripts index it.</summary>
    public static readonly IReadOnlyList<string> SkinTags = new[] { "IMG", "IMG_END", "MODEL", "MOTION" };

    /// <summary>
    /// Dresses a loaded character in a costume pack, as <c>CCharacter2::LoadSkin</c> does
    /// (character <c>_SKIN_MODEL</c>, <c>_SKIN_MOTION</c>, <c>CreateChangeFrame</c>):
    /// <list type="bullet">
    /// <item>The costume's <c>MODEL</c> scene is read with the character's deform frames as
    /// skinned visuals and its <c>MOTION slot, ?, "x.bbp", "x.wgt"</c> bind pose and weights.</item>
    /// <item>Each skinned visual moves onto the character's skeleton: its bones are found by
    /// name (<c>ChangeWeight</c>) and its own bind matrix replaces the frame's base matrix.</item>
    /// <item>Every costume frame with a visual gives it to the character's frame of the same
    /// name, replacing what that frame drew (the rig's placeholder triangles); costume frames
    /// the character lacks are dropped.</item>
    /// </list>
    /// Returns the number of frames dressed.
    /// </summary>
    /// <param name="model">A character model scene built by this loader (the parent of its skeleton).</param>
    public int ApplySkin(Node3D model, string skinPath, TextureLibrary? sharedTextures = null)
    {
        skinPath = VfsPath.Normalize(skinPath);
        var skeleton = model.GetNodeOrNull<Skeleton3D>(SkeletonNodeName)
            ?? throw new ArgumentException("The model has no skeleton.", nameof(model));
        var pack = _vfs.TryOpenPack(skinPath)
            ?? throw new System.IO.InvalidDataException($"'{skinPath}' is not a PACK archive.");
        var infoData = pack.TryRead(CharacterInfo.FileName)
            ?? throw new System.IO.InvalidDataException($"'{skinPath}' has no {CharacterInfo.FileName}.");

        // A costume's info.cfg is read with the skin tags; in text form the tags are the same.
        var commands = GameScript.Parse(infoData, SkinTags);
        string? modelName = commands.LastOrDefault(c => c.Tag == "MODEL")?.Arg(0).AsString;
        var motion = commands.LastOrDefault(c => c.Tag == "MOTION");
        if (modelName == null || pack.TryRead(modelName) is not { } modelData || !MdsFile.LooksLikeMds(modelData))
            throw new System.IO.InvalidDataException($"'{skinPath}': {CharacterInfo.FileName} names no costume model.");

        var mds = MdsFile.Parse(modelData);
        var weightLists = ReadKeyLists(pack, NonEmpty(motion?.Arg(3).AsString), skinPath);
        var bindPose = NonEmpty(motion?.Arg(2).AsString) is { } bbpName && pack.TryRead(bbpName) is { } bbp
            ? MotionFile.ParseBindPose(bbp)
            : null;

        var textures = new TextureLibrary();
        if (sharedTextures != null)
            textures.AddRange(sharedTextures);
        foreach (var entry in pack.Entries)
            TryAddTextures(textures, entry.Name, pack.GetSpan(entry));

        var boneNames = (string[])model.GetMeta(BoneNamesMeta);
        var deformFrames = model.HasMeta(DeformFramesMeta) ? (string[])model.GetMeta(DeformFramesMeta) : Array.Empty<string>();
        var bases = model.HasMeta(BaseMatricesMeta)
            ? ((Godot.Collections.Array)model.GetMeta(BaseMatricesMeta)).Select(v => v.AsTransform3D()).ToArray()
            : BaseMatrices(skeleton, null);

        // Character bone of each costume frame, by name (mgFrameNameComp: up to "--").
        var characterBone = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < boneNames.Length; i++)
            characterBone.TryAdd(MdsObject.StripAttributes(boneNames[i]), i);
        int BoneOf(MdsObject frame) => characterBone.TryGetValue(frame.BaseName, out int bone) ? bone : -1;

        int dressed = 0;
        foreach (var frame in mds.Objects)
        {
            if (frame.Model == null || frame.Model.Primitives.Count == 0)
                continue;
            int target = BoneOf(frame);
            if (target < 0)
                continue;

            bool deform = deformFrames.Any(name => MdsObject.StripAttributes(name) == frame.BaseName);
            var weights = deform && weightLists != null
                ? VertexWeights.Build(weightLists, frame.Index, frame.Model.Positions.Length)
                : null;
            var mesh = BuildMesh(frame.Model, textures, weights);
            if (mesh == null)
                continue;

            RemoveFrameVisuals(skeleton, target);
            if (weights != null)
            {
                var bones = weights.Bones.Select(b => b >= 0 && b < mds.Objects.Count ? BoneOf(mds.Objects[b]) : -1);
                // CreateChangeFrame copies the costume's bind matrix of this frame over the character's.
                var modelBase = bindPose != null && frame.Index < bindPose.Length ? ToTransform(bindPose[frame.Index]) : bases[target];
                skeleton.AddChild(new MeshInstance3D
                {
                    Name = SafeNodeName("skin_" + boneNames[target]),
                    Mesh = mesh,
                    Skin = BuildSkin(bases, bones, modelBase),
                    Skeleton = new NodePath(".."),
                }.WithMeta(FrameMeta, target));
                LastReport.SkinnedMeshes++;
            }
            else
            {
                var attachment = new BoneAttachment3D { Name = SafeNodeName("skin_" + boneNames[target]) }.WithMeta(FrameMeta, target);
                skeleton.AddChild(attachment);
                attachment.BoneName = boneNames[target];
                attachment.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = mesh });
            }
            LastReport.Meshes++;
            dressed++;
        }
        return dressed;
    }

    /// <summary>Removes what a frame draws: <c>SetVisual</c> replaces a frame's single visual.</summary>
    private static void RemoveFrameVisuals(Skeleton3D skeleton, int frame)
    {
        foreach (var child in skeleton.GetChildren())
        {
            if (child.HasMeta(FrameMeta) && (int)child.GetMeta(FrameMeta) == frame)
            {
                skeleton.RemoveChild(child);
                child.QueueFree();
            }
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
