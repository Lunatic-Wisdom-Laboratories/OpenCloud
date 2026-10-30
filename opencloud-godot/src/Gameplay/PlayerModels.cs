using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenCloud.Assets.Models;
using OpenCloud.Core;

namespace OpenCloud.Gameplay;

/// <summary>
/// Builds a player's model the way <c>SetupMainUnit</c> does in town: the character's town rig
/// (<c>chara/c01_base</c> for Max), dressed with the equipped clothes and footwear through the
/// costume loader (<c>LoadSkin</c>), and the headwear hung on the rig's <c>hat</c> frame.
/// Everything is loaded from the converted models through <see cref="ModelLoader"/>.
/// </summary>
public static class PlayerModels
{
    /// <summary>What a player wears; the defaults are Max's starting equipment.</summary>
    public sealed record Equipment(string Rig, IReadOnlyList<string> Rigs, IReadOnlyList<string[]> Skins, string? Hat);

    /// <summary>
    /// Max: the field rig, then the town rig as a fallback. The equipment is made for the field
    /// rig: on the town rig the cap hangs upside down from its "hat" frame. Clothes then footwear, as
    /// <c>SetupMainUnit</c> puts them on; each piece is the first file of its list that exists
    /// (starting items first).
    /// </summary>
    public static readonly Equipment Max = new(
        "max",
        new[] { "mainchr/c01_base2.glb", "chara/c01_base.glb" },
        new[]
        {
            new[] { "mainchr/c01fuku1.glb" },
            new[] { "mainchr/c01sh001.glb", "mainchr/c01sh002.glb" },
        },
        "mainchr/c01ha001.glb");

    /// <summary>Monica's town rig; her equipment files are not wired up yet.</summary>
    public static readonly Equipment Monica = new(
        "monica",
        new[] { "chara/c02_base.glb", "mainchr/c02_base2.glb" },
        Array.Empty<string[]>(),
        null);

    /// <summary>
    /// Turn given to headwear about the character's up axis. Hung by the frame matrices alone,
    /// the cap's peak points at Max's right; why the game shows it facing forward is not known
    /// yet, so the cap is turned by hand until it is.
    /// </summary>
    public const float HatYawDegrees = 80.0f;

    /// <summary>How far the cap is pushed back from the frame, toward the back of the head.</summary>
    public const float HatBackOffset = 0.2f;

    public static Equipment ForSlot(int slot) => slot == 1 ? Monica : Max;

    /// <summary>Loads and dresses the model, or returns null with the reason in <paramref name="notes"/>.</summary>
    public static Node3D? Load(ModelLoader loader, Equipment equipment, List<string> notes)
    {
        string? path = loader.FindFirst(equipment.Rigs);
        if (path == null)
        {
            notes.Add($"{equipment.Rig}: no model found (tried {string.Join(", ", equipment.Rigs)})");
            return null;
        }

        var root = loader.Load(path);
        notes.Add($"{equipment.Rig}: {path}");
        var rig = FindAll<Node3D>(root).FirstOrDefault(n => n.HasMeta(ModelMeta.DeformFrames));
        if (rig != null)
        {
            foreach (var candidates in equipment.Skins)
            {
                if (loader.FindFirst(candidates) is not { } skin)
                {
                    notes.Add($"  not found: {string.Join(", ", candidates)}");
                    continue;
                }
                try
                {
                    notes.Add($"  {skin}: {loader.ApplySkin(rig, skin)} frames");
                }
                catch (Exception exception) when (exception is System.IO.InvalidDataException or ArgumentException)
                {
                    notes.Add($"  {skin}: {exception.Message}");
                }
            }
        }
        if (equipment.Hat != null && loader.FindFirst(new[] { equipment.Hat }) is { } hat)
            AttachToFrame(loader, root, rig, hat, "hat", notes);
        return root;
    }

    /// <summary><c>SetRef</c>: hangs a part on a named frame of the character's skeleton.</summary>
    private static void AttachToFrame(ModelLoader loader, Node3D root, Node3D? rig, string path, string frame, List<string> notes)
    {
        // Hung once the character is in the scene, as the asset viewer hangs it.
        if (!root.IsInsideTree())
        {
            root.Connect(Node.SignalName.TreeEntered, Callable.From(() => AttachToFrame(loader, root, rig, path, frame, notes)),
                (uint)GodotObject.ConnectFlags.OneShot);
            return;
        }
        // The rig's own skeleton, the one its motions pose; the pack also holds hidden models
        // (shadow, LOD) with the same frames that never move.
        var skeletons = FindAll<Skeleton3D>(root).Where(IsShown)
            .OrderBy(s => rig != null && s.GetParent() == rig ? 0 : 1);
        foreach (var skeleton in skeletons)
        {
            int bone = Enumerable.Range(0, skeleton.GetBoneCount())
                .FirstOrDefault(i => NodeNames.StripAttributes(skeleton.GetBoneName(i)) == frame, -1);
            if (bone < 0)
                continue;
            try
            {
                var attachment = new BoneAttachment3D { Name = NodeNames.Safe("ref_" + frame) };
                attachment.SetMeta(PartFrameMeta, frame);
                skeleton.AddChild(attachment);
                attachment.BoneName = skeleton.GetBoneName(bone);
                var part = loader.Load(path);
                attachment.AddChild(part);
                if (frame == "hat" && Mathf.Abs(HatYawDegrees) > 0.0f)
                {
                    // A turn about the character's up axis, written in the frame's space.
                    var basis = skeleton.GetBoneGlobalRest(bone).Basis.Orthonormalized();
                    part.Transform = new Transform3D(basis.Inverse() * new Basis(Vector3.Up, Mathf.DegToRad(HatYawDegrees)) * basis,
                        basis.Inverse() * new Vector3(0, 0, -HatBackOffset));
                }
                // The part plays its own first motion, as the game steps a SetRef part: the
                // hat's motion is what turns it the right way up on the frame.
                string? motion = PlayFirstMotion(part);
                notes.Add($"    part motion: {motion ?? "none"}");
                if (motion == null)
                {
                    PoseAtRest(part);
                    Callable.From(() => PoseAtRest(part)).CallDeferred();
                }
                notes.Add($"  {path}: on {frame} of {skeleton.GetParent()?.Name}");
            }
            catch (Exception exception) when (exception is System.IO.InvalidDataException or ArgumentException)
            {
                notes.Add($"  {path}: {exception.Message}");
            }
            return;
        }
    }

    /// <summary>
    /// Puts a part without motion on its rest pose. Its meshes hang on its own frames, and
    /// nothing moves those frames after it is hung, so Godot never places the meshes on them
    /// (the cap lost its root frame's half turn and hung upside down).
    /// </summary>
    private static void PoseAtRest(Node part)
    {
        if (!GodotObject.IsInstanceValid(part))
            return;
        foreach (var skeleton in FindAll<Skeleton3D>(part))
        {
            skeleton.ResetBonePoses();
            foreach (var attachment in skeleton.GetChildren().OfType<BoneAttachment3D>())
            {
                int bone = skeleton.FindBone(attachment.BoneName);
                if (bone >= 0)
                    attachment.Transform = skeleton.GetBoneGlobalPose(bone);
            }
        }
    }

    private const string PartFrameMeta = "opencloud_part_frame";

    /// <summary>
    /// Where a hung part sits right now, relative to the character: the frame's posed rotation
    /// and the part's meshes. Compared with the asset viewer's "hat now" line.
    /// </summary>
    public static IEnumerable<string> DescribeParts(Node3D model)
    {
        var inverse = model.GlobalTransform.AffineInverse();
        foreach (var attachment in FindAll<BoneAttachment3D>(model).Where(a => a.HasMeta(PartFrameMeta)))
        {
            string frame = (string)attachment.GetMeta(PartFrameMeta);
            if (attachment.GetParent() is Skeleton3D skeleton && skeleton.FindBone(attachment.BoneName) is var bone and >= 0)
                yield return $"  {frame} now {Describe(skeleton.GetBoneGlobalPose(bone))}";
            yield return $"  {frame} attachment {Describe(inverse * attachment.GlobalTransform)}";
            foreach (var mesh in FindAll<MeshInstance3D>(attachment).Take(2))
                yield return $"  {frame} mesh {mesh.Name} {Describe(inverse * mesh.GlobalTransform)}";
        }
    }

    private static string Describe(Transform3D t)
    {
        var euler = t.Basis.GetEuler() * (180.0f / Mathf.Pi);
        return $"pos ({t.Origin.X:0.#}, {t.Origin.Y:0.#}, {t.Origin.Z:0.#}) rot ({euler.X:0.#}, {euler.Y:0.#}, {euler.Z:0.#})";
    }

    /// <summary>Starts the part's first motion; returns its name, or null when it has none.</summary>
    private static string? PlayFirstMotion(Node part)
    {
        string? played = null;
        foreach (var player in FindAll<AnimationPlayer>(part))
        {
            var first = player.GetAnimationLibraryList()
                .OrderBy(l => int.TryParse(l.ToString().TrimStart('m'), out int slot) ? slot : int.MaxValue)
                .SelectMany(l => player.GetAnimationLibrary(l).GetAnimationList()
                    .Select(a => (Name: l.ToString().Length == 0 ? a.ToString() : $"{l}/{a}", Clip: player.GetAnimationLibrary(l).GetAnimation(a))))
                .OrderBy(a => a.Clip.HasMeta(ModelMeta.KeyIndex) ? (int)a.Clip.GetMeta(ModelMeta.KeyIndex) : int.MaxValue)
                .Select(a => a.Name)
                .FirstOrDefault();
            if (first == null)
                continue;
            // The model is built before it joins the scene, and a player outside the tree
            // does not pose its skeleton, so the motion starts when the player enters it.
            void Start()
            {
                player.Play(first);
                player.Seek(0, true);
            }
            if (player.IsInsideTree())
                Start();
            else
                player.Connect(Node.SignalName.TreeEntered, Callable.From(Start), (uint)GodotObject.ConnectFlags.OneShot);
            played ??= first;
        }
        return played;
    }

    private static bool IsShown(Node node)
    {
        for (var current = node; current != null; current = current.GetParent())
        {
            if (current is Node3D { Visible: false })
                return false;
        }
        return true;
    }

    private static IEnumerable<T> FindAll<T>(Node node) where T : Node
    {
        if (node is T match)
            yield return match;
        foreach (var child in node.GetChildren())
        {
            foreach (var found in FindAll<T>(child))
                yield return found;
        }
    }
}
