using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Godot;
using OpenCloud.Core;
using OpenCloud.Core.IO;
using OpenCloud.Rendering;

namespace OpenCloud.Assets.Models;

/// <summary>
/// Loads converted models: a glTF 2.0 file (<c>.glb</c>, or <c>.gltf</c> with its buffers and
/// images beside it) and, when present, its <see cref="ModelDocument"/> companion. Godot's glTF
/// importer builds the nodes; the companion then gives the scene what the engine expects of a
/// game model:
/// <list type="bullet">
/// <item>each model's skeleton is named <see cref="ModelMeta.SkeletonNodeName"/> and its skinned
/// meshes sit under it; the model carries its frame names, deform frames and base matrices;</item>
/// <item>every visual records the frame that owns it (<see cref="ModelMeta.Frame"/>);</item>
/// <item>each model's actions play from its own <see cref="AnimationPlayer"/>, one library
/// <c>m&lt;slot&gt;</c> per motion slot, clips carrying their original name and step;</item>
/// <item>shadow and LOD models are hidden, and materials follow the game's conventions.</item>
/// </list>
/// Costumes are put on a loaded character with <see cref="ApplySkin"/>.
/// </summary>
public sealed class ModelLoader
{
    /// <summary>Extensions the loader opens as models.</summary>
    public static readonly IReadOnlySet<string> ModelExtensions = new HashSet<string> { "glb", "gltf" };

    private readonly VirtualFileSystem _vfs;

    public ModelLoader(VirtualFileSystem vfs)
    {
        _vfs = vfs;
    }

    /// <summary>The companion of the last loaded model, or null when it had none.</summary>
    public ModelDocument? LastDocument { get; private set; }

    /// <summary>Returns the first candidate path that exists in the mounted data, or null.</summary>
    public string? FindFirst(IEnumerable<string> candidates) =>
        candidates.Where(_vfs.Exists).Select(VfsPath.Normalize).FirstOrDefault();

    public Node3D Load(string path)
    {
        path = VfsPath.Normalize(path);
        var bytes = _vfs.ReadAllBytes(path);
        byte[] document = VfsPath.GetExtension(path) == "glb" ? bytes : EmbedExternalResources(path, bytes);

        var gltf = new GltfDocument();
        var state = new GltfState();
        var error = gltf.AppendFromBuffer(document, string.Empty, state);
        if (error != Error.Ok)
            throw new System.IO.InvalidDataException($"Godot could not parse glTF '{path}': {error}.");
        var scene = gltf.GenerateScene(state) as Node3D
            ?? throw new System.IO.InvalidDataException($"glTF '{path}' produced no 3D scene.");
        scene.Name = NodeNames.Safe(VfsPath.GetFileName(path));

        string companion = ModelDocument.PathFor(path);
        LastDocument = _vfs.TryRead(companion) is { } json ? ModelDocument.FromJson(JsonValues.Parse(json, companion), companion) : null;
        Normalise(scene, LastDocument);
        return scene;
    }

    private void Normalise(Node3D scene, ModelDocument? document)
    {
        var imported = scene.GetChildren().OfType<AnimationPlayer>().FirstOrDefault();
        var retargets = new List<(Node3D Model, Animation Animation, int Track, Node Target, string Bone)>();
        var players = new List<(Node3D Model, AnimationPlayer Player)>();

        if (document != null)
        {
            foreach (var part in document.Models)
            {
                if (scene.FindChild(part.Node, true, false) is not Node3D model)
                    continue;
                model.SetMeta(ModelMeta.BoneNames, part.Frames);
                model.SetMeta(ModelMeta.DeformFrames, part.DeformFrames);
                if (part.BaseMatrices.Length > 0)
                    model.SetMeta(ModelMeta.BaseMatrices, new Godot.Collections.Array(part.BaseMatrices.Select(t => Variant.From(t))));
                if (imported != null && part.Animations.Count > 0)
                    players.Add((model, BuildPlayer(scene, model, imported, part, retargets)));
            }
            foreach (var name in document.HiddenNodes)
            {
                if (scene.FindChild(NodeNames.Safe(name), true, false) is Node3D hidden)
                    hidden.Visible = false;
            }
        }

        foreach (var skeleton in FindAll<Skeleton3D>(scene).ToList())
            NormaliseSkeleton(skeleton, document);

        // Track paths are relative to each model's player, computed once the nodes have their final names.
        foreach (var (model, animation, track, target, bone) in retargets)
        {
            string nodePath = model.GetPathTo(target).ToString();
            animation.TrackSetPath(track, new NodePath(bone.Length > 0 ? $"{nodePath}:{bone}" : nodePath));
        }
        foreach (var (model, player) in players)
            model.AddChild(player);
        if (imported != null && players.Count > 0)
        {
            scene.RemoveChild(imported);
            imported.QueueFree();
        }

        foreach (var mesh in FindAll<MeshInstance3D>(scene))
            NormaliseMaterials(mesh);
    }

    /// <summary>
    /// Names the skeleton as the engine expects, moves skinned meshes under it, and marks each
    /// visual with its frame (<see cref="ModelMeta.Frame"/>).
    /// </summary>
    private static void NormaliseSkeleton(Skeleton3D skeleton, ModelDocument? document)
    {
        var model = skeleton.GetParent() as Node3D;
        var frames = model != null && model.HasMeta(ModelMeta.BoneNames) ? (string[])model.GetMeta(ModelMeta.BoneNames) : null;
        if (model != null && model.GetNodeOrNull(ModelMeta.SkeletonNodeName) == null)
            skeleton.Name = ModelMeta.SkeletonNodeName;

        // A mesh node under a frame's node comes back from the importer as a bone of its own
        // ("Mesh", "Mesh_2"...) between the frame and the attachment: walk up to the frame.
        int FrameOfBone(string bone)
        {
            if (frames == null)
                return skeleton.FindBone(bone);
            for (int b = skeleton.FindBone(bone); b >= 0; b = skeleton.GetBoneParent(b))
            {
                string name = skeleton.GetBoneName(b);
                int frame = Array.FindIndex(frames, f => BoneNameMatches(f, name));
                if (frame >= 0)
                    return frame;
            }
            return -1;
        }

        foreach (var mesh in FindAll<MeshInstance3D>(model ?? (Node)skeleton).Where(m => m.Skin != null).ToList())
        {
            if (mesh.GetParent() != skeleton)
            {
                mesh.GetParent().RemoveChild(mesh);
                skeleton.AddChild(mesh);
                mesh.Transform = Transform3D.Identity;
                mesh.Skeleton = new NodePath("..");
            }
            int frame = NodeNames.SkinnedMeshFrame(mesh.Name);
            if (frame >= 0)
                mesh.SetMeta(ModelMeta.Frame, frame);
        }
        foreach (var attachment in skeleton.GetChildren().OfType<BoneAttachment3D>())
        {
            int frame = FrameOfBone(attachment.BoneName);
            if (frame >= 0)
                attachment.SetMeta(ModelMeta.Frame, frame);
        }
    }

    /// <summary>The importer replaces ':' and '/' in bone names; compare with that in mind.</summary>
    private static bool BoneNameMatches(string frame, string bone) =>
        frame == bone || frame.Replace(':', '_').Replace('/', '_') == bone;

    /// <summary>A model's player: one library per motion slot, clips retargeted to the model.</summary>
    private static AnimationPlayer BuildPlayer(Node3D scene, Node3D model, AnimationPlayer imported, ModelPart part, List<(Node3D, Animation, int, Node, string)> retargets)
    {
        var player = new AnimationPlayer { Name = ModelMeta.AnimationPlayerName };
        var root = imported.GetNodeOrNull(imported.RootNode) ?? scene;
        foreach (var slot in part.Animations.GroupBy(a => a.Slot).OrderBy(g => g.Key))
        {
            var library = new AnimationLibrary();
            foreach (var action in slot.OrderBy(a => a.KeyIndex))
            {
                var source = FindClip(imported, action.Clip);
                if (source == null)
                {
                    GD.PushWarning($"{scene.Name}: clip {action.Clip} is missing from the glTF.");
                    continue;
                }
                var animation = (Animation)source.Duplicate();
                animation.LoopMode = action.Loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
                animation.SetMeta(ModelMeta.GameName, action.GameName);
                animation.SetMeta(ModelMeta.Step, action.Step);
                animation.SetMeta(ModelMeta.KeyIndex, action.KeyIndex);
                for (int track = 0; track < animation.GetTrackCount(); track++)
                {
                    var path = animation.TrackGetPath(track);
                    var target = root.GetNodeOrNull(new NodePath(path.GetConcatenatedNames()));
                    if (target != null)
                        retargets.Add((model, animation, track, target, path.GetConcatenatedSubNames()));
                }
                if (!library.HasAnimation(action.Name))
                    library.AddAnimation(action.Name, animation);
            }
            player.AddAnimationLibrary($"m{slot.Key}", library);
        }
        return player;
    }

    /// <summary>The importer keeps clip names, but may sanitise them; match either form.</summary>
    private static Animation? FindClip(AnimationPlayer player, string clip)
    {
        if (player.HasAnimation(clip))
            return player.GetAnimation(clip);
        string sanitised = clip;
        foreach (char c in "/:,[")
            sanitised = sanitised.Replace(c, '_');
        return player.GetAnimationList().Where(a => a.ToString().Replace('/', '_') == sanitised).Select(a => player.GetAnimation(a)).FirstOrDefault();
    }

    /// <summary>
    /// The game draws model faces from both sides unless asked to cull, modulates textures with
    /// the material colour and linear vertex colours, has no specular term, and blends
    /// translucent surfaces with a depth pre-pass.
    /// </summary>
    private static void NormaliseMaterials(MeshInstance3D instance)
    {
        if (instance.Mesh == null)
            return;
        for (int surface = 0; surface < instance.Mesh.GetSurfaceCount(); surface++)
        {
            if (instance.Mesh.SurfaceGetMaterial(surface) is StandardMaterial3D material)
                GameMaterialFactory.ApplyGameConventions(material);
        }
    }

    // --- Costumes ------------------------------------------------------------------------------

    /// <summary>
    /// Dresses a loaded character in a converted costume, as <c>CCharacter2::LoadSkin</c> does
    /// (<c>_SKIN_MODEL</c>, <c>_SKIN_MOTION</c>, <c>CreateChangeFrame</c>):
    /// <list type="bullet">
    /// <item>Every costume frame with a visual gives it to the character's frame of the same name
    /// (compared up to "--"), replacing what that frame drew (the rig's placeholder triangles);
    /// costume frames the character lacks are dropped.</item>
    /// <item>On the character's deform frames, the costume's weighted visuals are skinned to the
    /// character's skeleton: bones found by name (<c>ChangeWeight</c>), the costume's bind matrix of
    /// the frame replacing the character's base matrix. Other visuals move rigidly with the frame.</item>
    /// </list>
    /// Returns the number of frames dressed.
    /// </summary>
    /// <param name="model">A character model (the parent of its skeleton) built by this loader.</param>
    public int ApplySkin(Node3D model, string costumePath)
    {
        var skeleton = model.GetNodeOrNull<Skeleton3D>(ModelMeta.SkeletonNodeName)
            ?? throw new ArgumentException("The model has no skeleton.", nameof(model));
        var boneNames = model.HasMeta(ModelMeta.BoneNames)
            ? (string[])model.GetMeta(ModelMeta.BoneNames)
            : Enumerable.Range(0, skeleton.GetBoneCount()).Select(skeleton.GetBoneName).ToArray();
        var deformFrames = model.HasMeta(ModelMeta.DeformFrames) ? (string[])model.GetMeta(ModelMeta.DeformFrames) : Array.Empty<string>();
        var bases = model.HasMeta(ModelMeta.BaseMatrices)
            ? ((Godot.Collections.Array)model.GetMeta(ModelMeta.BaseMatrices)).Select(v => v.AsTransform3D()).ToArray()
            : boneNames.Select(name => BoneOf(skeleton, name) is var b and >= 0 ? skeleton.GetBoneGlobalRest(b) : Transform3D.Identity).ToArray();

        var costume = Load(costumePath);
        try
        {
            var document = LastDocument;
            var part = document?.Models.FirstOrDefault();
            if (document?.Kind != ModelDocument.KindCostume || part == null ||
                costume.FindChild(part.Node, true, false) is not Node3D costumeModel ||
                costumeModel.GetNodeOrNull<Skeleton3D>(ModelMeta.SkeletonNodeName) is not { } costumeSkeleton)
                throw new System.IO.InvalidDataException($"'{costumePath}' is not a converted costume.");

            // Character frame of each name (mgFrameNameComp: up to "--").
            var characterFrame = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < boneNames.Length; i++)
                characterFrame.TryAdd(NodeNames.StripAttributes(boneNames[i]), i);
            int FrameOf(string name) => characterFrame.TryGetValue(NodeNames.StripAttributes(name), out int frame) ? frame : -1;

            int dressed = 0;
            var visuals = costumeSkeleton.GetChildren()
                .Where(c => c.HasMeta(ModelMeta.Frame))
                .GroupBy(c => (int)c.GetMeta(ModelMeta.Frame))
                .OrderBy(g => g.Key)
                .ToList();
            foreach (var group in visuals)
            {
                int costumeFrame = group.Key;
                if (costumeFrame < 0 || costumeFrame >= part.Frames.Length)
                    continue;
                string frameName = part.Frames[costumeFrame];
                int target = FrameOf(frameName);
                int targetBone = target >= 0 ? BoneOf(skeleton, boneNames[target]) : -1;
                if (targetBone < 0)
                    continue;

                bool deform = deformFrames.Any(name => NodeNames.StripAttributes(name) == NodeNames.StripAttributes(frameName));
                RemoveFrameVisuals(skeleton, target);
                foreach (var visual in group)
                {
                    if (visual is MeshInstance3D { Skin: { } costumeSkin, Mesh: { } mesh } && deform)
                    {
                        var modelBase = costumeFrame < part.BaseMatrices.Length ? part.BaseMatrices[costumeFrame] : bases[target];
                        var skin = new Skin();
                        for (int slot = 0; slot < costumeSkin.GetBindCount(); slot++)
                        {
                            string boneName = costumeSkin.GetBindName(slot).ToString();
                            if (boneName.Length == 0 && costumeSkin.GetBindBone(slot) is var b and >= 0 && b < costumeSkeleton.GetBoneCount())
                                boneName = costumeSkeleton.GetBoneName(b);
                            int frame = FrameOf(boneName);
                            int bone = frame >= 0 ? BoneOf(skeleton, boneNames[frame]) : -1;
                            if (bone >= 0)
                                skin.AddBind(bone, bases[frame].AffineInverse() * modelBase);
                            else
                                skin.AddBind(0, Transform3D.Identity);
                        }
                        skeleton.AddChild(new MeshInstance3D
                        {
                            Name = NodeNames.SkinnedMesh(target),
                            Mesh = mesh,
                            Skin = skin,
                            Skeleton = new NodePath(".."),
                        }.WithMeta(ModelMeta.Frame, target));
                    }
                    else
                    {
                        foreach (var instance in visual is MeshInstance3D single ? new[] { single } : FindAll<MeshInstance3D>(visual).ToArray())
                        {
                            var attachment = new BoneAttachment3D { Name = NodeNames.Safe("skin_" + boneNames[target]) }.WithMeta(ModelMeta.Frame, target);
                            skeleton.AddChild(attachment);
                            attachment.BoneName = skeleton.GetBoneName(targetBone);
                            attachment.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = instance.Mesh });
                        }
                    }
                }
                dressed++;
            }
            return dressed;
        }
        finally
        {
            costume.Free();
        }
    }

    private static int BoneOf(Skeleton3D skeleton, string frameName)
    {
        int bone = skeleton.FindBone(frameName);
        return bone >= 0 ? bone : skeleton.FindBone(frameName.Replace(':', '_').Replace('/', '_'));
    }

    /// <summary>Removes what a frame draws: <c>SetVisual</c> replaces a frame's single visual.</summary>
    private static void RemoveFrameVisuals(Skeleton3D skeleton, int frame)
    {
        foreach (var child in skeleton.GetChildren())
        {
            if (child.HasMeta(ModelMeta.Frame) && (int)child.GetMeta(ModelMeta.Frame) == frame)
            {
                skeleton.RemoveChild(child);
                child.QueueFree();
            }
        }
    }

    // --- glTF with separate files --------------------------------------------------------------

    /// <summary>
    /// A <c>.gltf</c> names its buffers and images by relative URI. Godot's importer cannot reach
    /// inside <c>data.zip</c>, so those files are read through the file system and embedded.
    /// </summary>
    private byte[] EmbedExternalResources(string path, byte[] json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new System.IO.InvalidDataException($"glTF '{path}' is not a JSON object.");
        string directory = VfsPath.GetDirectory(path);
        Embed(root["buffers"] as JsonArray, directory, "application/octet-stream", path);
        Embed(root["images"] as JsonArray, directory, "image/png", path);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private void Embed(JsonArray? items, string directory, string mimeType, string path)
    {
        if (items == null)
            return;
        foreach (var item in items)
        {
            if (item is not JsonObject obj || obj["uri"]?.GetValue<string>() is not { } uri || uri.StartsWith("data:", StringComparison.Ordinal))
                continue;
            string resolved = VfsPath.Combine(directory, Uri.UnescapeDataString(uri));
            var data = _vfs.TryRead(resolved);
            if (data == null)
            {
                GD.PushWarning($"glTF '{path}': missing external resource '{resolved}'.");
                continue;
            }
            obj["uri"] = $"data:{mimeType};base64,{Convert.ToBase64String(data)}";
            if (mimeType.StartsWith("image/", StringComparison.Ordinal))
                obj["mimeType"] = mimeType;
        }
    }

    internal static IEnumerable<T> FindAll<T>(Node root) where T : Node
    {
        foreach (var child in root.GetChildren())
        {
            if (child is T match)
                yield return match;
            foreach (var nested in FindAll<T>(child))
                yield return nested;
        }
    }
}
