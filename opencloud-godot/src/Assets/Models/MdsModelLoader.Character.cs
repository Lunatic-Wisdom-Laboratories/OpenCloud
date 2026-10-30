using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenCloud.Assets.Textures;
using OpenCloud.Core.IO;

namespace OpenCloud.Assets.Models;

public sealed partial class MdsModelLoader
{
    /// <summary>Name of the <see cref="AnimationPlayer"/> added to an animated character's model.</summary>
    public const string AnimationPlayerName = "AnimationPlayer";

    /// <summary>Meta key holding a clip's position in its slot's KEY list (players list clips by name).</summary>
    public const string KeyIndexMeta = "key_index";

    /// <summary>
    /// Loads a character pack the way <c>CCharacter2::LoadChrFile</c> reads its
    /// <see cref="CharacterInfo.FileName"/>: the <c>MODEL</c> scene, skinned by the
    /// <c>&lt;model&gt;.wgt</c> weights on the <c>VERTEX_ANIME</c> frames, and one animation per
    /// <c>KEY</c> interval of every <c>MOTION</c> slot, played by an <see cref="AnimationPlayer"/>
    /// whose libraries are named <c>m&lt;slot&gt;</c>. Other MDS members (shadow and LOD models)
    /// are kept hidden. Returns null when the script names no loadable model.
    /// </summary>
    private Node3D? TryLoadCharacter(PackArchive pack, string name, byte[] infoData, TextureLibrary textures, int depth)
    {
        CharacterInfo info;
        try
        {
            info = CharacterInfo.Parse(infoData);
        }
        catch (Exception exception) when (exception is System.IO.InvalidDataException or ArgumentException)
        {
            GD.PushWarning($"{name}/{CharacterInfo.FileName}: {exception.Message}");
            return null;
        }

        if (info.Model == null || pack.TryRead(info.Model) is not { } modelData || !MdsFile.LooksLikeMds(modelData))
            return null;

        var mds = MdsFile.Parse(modelData);
        LastReport.Character = info;

        var motions = new List<(MotionSet Set, List<MotionKeyList> Lists)>();
        IReadOnlyList<System.Numerics.Matrix4x4>? bindPose = null;
        foreach (var set in info.Motions.Values)
        {
            var lists = ReadKeyLists(pack, set.MotionFile, name);
            if (lists != null)
                motions.Add((set, lists));
            // Every _MOTION copies its bind-pose file over the same base matrices; the last one stays.
            if (set.BindPoseFile != null && pack.TryRead(set.BindPoseFile) is { } bbp)
                bindPose = MotionFile.ParseBindPose(bbp);
        }

        SkinningData? skinning = null;
        if (info.SkinnedFrames.Count > 0)
        {
            var weightLists = ReadKeyLists(pack, VfsPath.ChangeExtension(info.Model, "wgt"), name);
            if (info.ShapeAnime)
                LastReport.Notes.Add("SHAPE_ANIME: CPU shape deformation is not implemented; skinned frames are drawn rigid.");
            else if (weightLists != null)
                skinning = new SkinningData { WeightLists = weightLists, SkinnedFrames = info.SkinnedFrames, BindPose = bindPose };
        }

        var root = new Node3D { Name = SafeNodeName(name) };
        var model = BuildScene(mds, info.Model, textures, skinning);
        root.AddChild(model);

        // What a costume (LoadSkin) needs from this character: its deform frames and base matrices.
        model.SetMeta(DeformFramesMeta, info.SkinnedFrames.ToArray());
        var skeleton = model.GetNode<Skeleton3D>(SkeletonNodeName);
        model.SetMeta(BaseMatricesMeta, new Godot.Collections.Array(BaseMatrices(skeleton, bindPose).Select(t => Variant.From(t))));

        if (motions.Count > 0)
            model.AddChild(BuildAnimationPlayer(mds, model, motions));

        foreach (var entry in pack.Entries)
        {
            string extension = VfsPath.GetExtension(entry.Name);
            if (extension == "mds" && !VfsPath.Comparer.Equals(entry.Name, info.Model))
            {
                var data = pack.GetSpan(entry);
                if (!MdsFile.LooksLikeMds(data))
                    continue;
                try
                {
                    var extra = BuildScene(MdsFile.Parse(data), entry.Name, textures);
                    extra.Visible = false;
                    root.AddChild(extra);
                }
                catch (System.IO.InvalidDataException exception)
                {
                    GD.PushWarning($"{name}/{entry.Name}: {exception.Message}");
                }
            }
            else if (depth < 4 && extension is "chr" or "pak" or "pac" or "pcp")
            {
                var inner = PackArchive.TryParse(pack.Read(entry), entry.Name);
                if (inner != null)
                    root.AddChild(LoadPack(inner, entry.Name, textures, depth + 1));
            }
        }
        return root;
    }

    private List<MotionKeyList>? ReadKeyLists(PackArchive pack, string? file, string packName)
    {
        if (file == null || pack.TryRead(file) is not { } data)
            return null;
        try
        {
            return MotionFile.Parse(data);
        }
        catch (System.IO.InvalidDataException exception)
        {
            GD.PushWarning($"{packName}/{file}: {exception.Message}");
            return null;
        }
    }

    private AnimationPlayer BuildAnimationPlayer(MdsFile mds, Node3D model, List<(MotionSet Set, List<MotionKeyList> Lists)> motions)
    {
        var boneNames = (string[])model.GetMeta(BoneNamesMeta);
        var rest = mds.Objects.Select(o => o.Matrix).ToArray();
        var player = new AnimationPlayer { Name = AnimationPlayerName };

        foreach (var (set, lists) in motions)
        {
            var library = new AnimationLibrary();
            var used = new HashSet<string>(StringComparer.Ordinal);
            var intervals = set.Keys.Count > 0
                ? set.Keys
                : new List<MotionKeyInterval> { new("all", 0, (int)lists.SelectMany(l => l.Keys).Select(k => k.Frame).DefaultIfEmpty(0u).Max(), 1.0f) };

            for (int index = 0; index < intervals.Count; index++)
            {
                var key = intervals[index];
                string clipName = UniqueClipName(key.Name, used);
                var animation = MotionAnimationBuilder.Build(lists, SkeletonNodeName, boneNames, rest, key.StartFrame, key.EndFrame, loop: true);
                animation.SetMeta("game_name", key.Name);
                animation.SetMeta("step", key.Step);
                animation.SetMeta(KeyIndexMeta, index);
                library.AddAnimation(clipName, animation);
                LastReport.Animations++;
            }
            player.AddAnimationLibrary($"m{set.Id}", library);
        }
        return player;
    }

    /// <summary>Animation names cannot hold '/', ':', ',' or '['; repeats get a numeric suffix.</summary>
    private static string UniqueClipName(string name, HashSet<string> used)
    {
        foreach (char c in "/:,[")
            name = name.Replace(c, '_');
        if (name.Length == 0)
            name = "clip";
        string unique = name;
        for (int i = 2; !used.Add(unique); i++)
            unique = $"{name}_{i}";
        return unique;
    }
}
