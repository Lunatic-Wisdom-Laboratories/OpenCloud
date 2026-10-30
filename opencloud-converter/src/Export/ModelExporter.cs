using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenCloud.Assets.Models;
using OpenCloud.Core;

namespace OpenCloud.Converter.Export;

/// <summary>A converted model: the GLB file and its JSON companion.</summary>
public sealed record ExportedModel(byte[] Glb, ModelDocument Document, IReadOnlyList<string> Warnings);

/// <summary>
/// Turns a node tree built by <see cref="MdsModelLoader"/> (a character, a costume, a model
/// bundle or a single scene) into a GLB file and its <see cref="ModelDocument"/>.
/// </summary>
public static class ModelExporter
{
    public static ExportedModel Export(Node3D scene, string kind, string source)
    {
        var document = new ModelDocument { Kind = kind, Source = source };
        var clipNames = new Dictionary<(AnimationPlayer, string, string), string>();
        var usedClips = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in Walk(scene))
        {
            if (node is Node3D { Visible: false } hidden && hidden != scene)
                document.HiddenNodes.Add(hidden.Name);

            // Skinned meshes are named after their frame so the engine finds them after import.
            if (node is MeshInstance3D { Skin: not null } skinned && skinned.HasMeta(MdsModelLoader.FrameMeta))
                skinned.Name = NodeNames.SkinnedMesh((int)skinned.GetMeta(MdsModelLoader.FrameMeta));

            if (node is not Node3D model || !model.HasMeta(MdsModelLoader.BoneNamesMeta))
                continue;
            var part = new ModelPart
            {
                Node = model.Name,
                Frames = (string[])model.GetMeta(MdsModelLoader.BoneNamesMeta),
                DeformFrames = model.HasMeta(MdsModelLoader.DeformFramesMeta) ? (string[])model.GetMeta(MdsModelLoader.DeformFramesMeta) : Array.Empty<string>(),
                BaseMatrices = model.HasMeta(MdsModelLoader.BaseMatricesMeta)
                    ? ((Godot.Collections.Array)model.GetMeta(MdsModelLoader.BaseMatricesMeta)).Select(v => v.AsTransform3D()).ToArray()
                    : Array.Empty<Transform3D>(),
            };
            if (model.GetNodeOrNull<AnimationPlayer>(MdsModelLoader.AnimationPlayerName) is { } player)
                AddAnimations(part, player, clipNames, usedClips);
            document.Models.Add(part);
        }

        var writer = new GltfWriter
        {
            ClipName = (player, library, clip) => clipNames.TryGetValue((player, library, clip), out var name) ? name : $"{library}_{clip}",
        };
        var glb = writer.Write(scene);
        return new ExportedModel(glb, document, writer.Warnings);
    }

    /// <summary>
    /// Lists every clip of a model's player. Libraries are the motion slots (<c>m&lt;slot&gt;</c>);
    /// glTF names are <c>m&lt;slot&gt;_&lt;clip&gt;</c>, prefixed with the model when a file holds
    /// several animated models with the same clip.
    /// </summary>
    private static void AddAnimations(ModelPart part, AnimationPlayer player, Dictionary<(AnimationPlayer, string, string), string> names, HashSet<string> used)
    {
        foreach (var libraryName in player.GetAnimationLibraryList())
        {
            string library = libraryName.ToString();
            int slot = library.StartsWith('m') && int.TryParse(library.AsSpan(1), out int parsed) ? parsed : 0;
            var clips = player.GetAnimationLibrary(libraryName);
            foreach (var clipName in clips.GetAnimationList())
            {
                string clip = clipName.ToString();
                var animation = clips.GetAnimation(clipName);
                string name = $"{library}_{clip}";
                if (!used.Add(name))
                {
                    name = $"{part.Node}-{library}_{clip}";
                    used.Add(name);
                }
                names[(player, library, clip)] = name;
                part.Animations.Add(new ModelAnimation(
                    name,
                    slot,
                    clip,
                    animation.HasMeta("game_name") ? (string)animation.GetMeta("game_name") : clip,
                    animation.HasMeta("step") ? (float)animation.GetMeta("step") : 1.0f,
                    animation.HasMeta(MdsModelLoader.KeyIndexMeta) ? (int)animation.GetMeta(MdsModelLoader.KeyIndexMeta) : part.Animations.Count,
                    animation.LoopMode != Animation.LoopModeEnum.None));
            }
        }
    }

    private static IEnumerable<Node> Walk(Node root)
    {
        yield return root;
        foreach (var child in root.GetChildren())
        {
            foreach (var node in Walk(child))
                yield return node;
        }
    }
}
