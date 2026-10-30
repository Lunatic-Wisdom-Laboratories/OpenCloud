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
            AttachToFrame(loader, root, hat, "hat", notes);
        return root;
    }

    /// <summary><c>SetRef</c>: hangs a part on a named frame of the character's skeleton.</summary>
    private static void AttachToFrame(ModelLoader loader, Node3D root, string path, string frame, List<string> notes)
    {
        foreach (var skeleton in FindAll<Skeleton3D>(root))
        {
            int bone = Enumerable.Range(0, skeleton.GetBoneCount())
                .FirstOrDefault(i => NodeNames.StripAttributes(skeleton.GetBoneName(i)) == frame, -1);
            if (bone < 0)
                continue;
            try
            {
                var attachment = new BoneAttachment3D { Name = NodeNames.Safe("ref_" + frame) };
                skeleton.AddChild(attachment);
                attachment.BoneName = skeleton.GetBoneName(bone);
                var part = loader.Load(path);
                attachment.AddChild(part);
                // The part plays its own first motion, as the game steps a SetRef part: the
                // hat's motion is what turns it the right way up on the frame.
                notes.Add($"    part motion: {PlayFirstMotion(part) ?? "none"}");
                notes.Add($"  {path}: on {frame}");
            }
            catch (Exception exception) when (exception is System.IO.InvalidDataException or ArgumentException)
            {
                notes.Add($"  {path}: {exception.Message}");
            }
            return;
        }
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
