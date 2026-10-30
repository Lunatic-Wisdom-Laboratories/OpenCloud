using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenCloud.Assets.Models;
using OpenCloud.Core.IO;

namespace OpenCloud.Gameplay;

/// <summary>
/// Builds a player's model the way <c>SetupMainUnit</c> does in town: the character's town rig
/// (<c>chara/c01_base</c> for Max), dressed with the equipped clothes and footwear through the
/// costume loader (<c>LoadSkin</c>), and the headwear hung on the rig's <c>hat</c> frame.
/// Everything goes through <see cref="GameModelLoader"/>, so converted glTF models are used
/// when the data holds them instead of the retail packs.
/// </summary>
public static class PlayerModels
{
    /// <summary>What a player wears; the defaults are Max's starting equipment.</summary>
    public sealed record Equipment(string Rig, IReadOnlyList<string> Rigs, IReadOnlyList<string> Skins, string? Hat);

    /// <summary>
    /// Max in town: the town rig, then the field rig as a fallback, each retail or converted.
    /// Clothes then footwear, as <c>SetupMainUnit</c> puts them on.
    /// </summary>
    public static readonly Equipment Max = new(
        "max",
        new[] { "chara/c01_base.chr", "chara/c01_base.gltf", "mainchr/c01_base2.chr", "mainchr/c01_base2.gltf" },
        new[] { "mainchr/c01fuku1.chr", "mainchr/c01sh001.chr" },
        "mainchr/c01ha001.chr");

    /// <summary>Monica's town rig; her equipment files are not wired up yet.</summary>
    public static readonly Equipment Monica = new(
        "monica",
        new[] { "chara/c02_base.chr", "chara/c02_base.gltf", "mainchr/c02_base2.chr", "mainchr/c02_base2.gltf" },
        Array.Empty<string>(),
        null);

    public static Equipment ForSlot(int slot) => slot == 1 ? Monica : Max;

    /// <summary>Loads and dresses the model, or returns null with the reason in <paramref name="notes"/>.</summary>
    public static Node3D? Load(GameModelLoader loader, Equipment equipment, List<string> notes)
    {
        string? path = loader.FindFirst(equipment.Rigs);
        if (path == null)
        {
            notes.Add($"{equipment.Rig}: no model found (tried {string.Join(", ", equipment.Rigs)})");
            return null;
        }

        var root = loader.Load(path);
        notes.Add($"{equipment.Rig}: {path}");
        var rig = FindAll<Node3D>(root).FirstOrDefault(n => n.HasMeta(MdsModelLoader.DeformFramesMeta));
        if (rig != null)
        {
            foreach (var skin in equipment.Skins.Where(s => loader.FindFirst(new[] { s }) != null))
            {
                try
                {
                    notes.Add($"  {skin}: {loader.Mds.ApplySkin(rig, skin)} frames");
                }
                catch (Exception exception) when (exception is System.IO.InvalidDataException or ArgumentException)
                {
                    notes.Add($"  {skin}: {exception.Message}");
                }
            }
        }
        if (equipment.Hat != null && loader.FindFirst(new[] { equipment.Hat }) is { } hat)
            AttachToFrame(loader, root, hat, "hat", notes);
        return root;
    }

    /// <summary><c>SetRef</c>: hangs a part on a named frame of the character's skeleton.</summary>
    private static void AttachToFrame(GameModelLoader loader, Node3D root, string path, string frame, List<string> notes)
    {
        foreach (var skeleton in FindAll<Skeleton3D>(root))
        {
            int bone = Enumerable.Range(0, skeleton.GetBoneCount())
                .FirstOrDefault(i => MdsObject.StripAttributes(skeleton.GetBoneName(i)) == frame, -1);
            if (bone < 0)
                continue;
            try
            {
                var attachment = new BoneAttachment3D { Name = MdsModelLoader.SafeNodeName("ref_" + frame) };
                skeleton.AddChild(attachment);
                attachment.BoneName = skeleton.GetBoneName(bone);
                var part = loader.Load(path);
                // The part follows the frame; its own clips would fight the rig's.
                foreach (var player in FindAll<AnimationPlayer>(part))
                    player.QueueFree();
                attachment.AddChild(part);
                notes.Add($"  {path}: on {frame}");
            }
            catch (Exception exception) when (exception is System.IO.InvalidDataException or ArgumentException)
            {
                notes.Add($"  {path}: {exception.Message}");
            }
            return;
        }
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
