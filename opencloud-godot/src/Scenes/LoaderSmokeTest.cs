using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenCloud.Assets.Models;
using OpenCloud.Assets.Textures;
using OpenCloud.Core;
using OpenCloud.Core.IO;

namespace OpenCloud.Scenes;

/// <summary>
/// Headless check of the data pipeline: mounts the data, loads the given models (or Max) and
/// textures, prints what was built and quits with exit code 0 on success. Run with
/// <c>godot --headless res://src/Scenes/LoaderSmokeTest.tscn -- --data=&lt;path&gt; [--model=&lt;path&gt;] [--texture=&lt;path&gt;]</c>.
/// </summary>
public partial class LoaderSmokeTest : Node
{
    public override void _Ready()
    {
        int failures = 0;
        var errors = new List<string>();
        using var vfs = GameDataPaths.MountAll(GameDataPaths.Resolve(), errors);
        foreach (var error in errors)
        {
            GD.PrintErr($"mount error: {error}");
            failures++;
        }
        GD.Print($"sources: {vfs.Sources.Count}, files: {vfs.EnumerateFiles().Count()}");

        var args = OS.GetCmdlineUserArgs();
        var models = args.Where(a => a.StartsWith("--model=", StringComparison.Ordinal)).Select(a => a["--model=".Length..]).ToList();
        var skins = args.Where(a => a.StartsWith("--skin=", StringComparison.Ordinal)).Select(a => a["--skin=".Length..]).ToList();
        var textures = args.Where(a => a.StartsWith("--texture=", StringComparison.Ordinal)).Select(a => a["--texture=".Length..]).ToList();
        var dumps = args.Where(a => a.StartsWith("--dump=", StringComparison.Ordinal)).Select(a => a["--dump=".Length..]).ToList();
        if (dumps.Count > 0)
        {
            string? output = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))?["--out=".Length..];
            var text = new System.Text.StringBuilder();
            var frameFilter = args.FirstOrDefault(a => a.StartsWith("--frames=", StringComparison.Ordinal))?["--frames=".Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            foreach (var path in dumps)
                DumpPack(vfs, path, text, frameFilter);
            if (output != null)
                System.IO.File.WriteAllText(output, text.ToString());
            else
                GD.Print(text.ToString());
            GetTree().Quit(0);
            return;
        }
        if (models.Count == 0)
            models.Add("chara/c01_base.chr");

        var attaches = args.Where(a => a.StartsWith("--attach=", StringComparison.Ordinal)).Select(a => a["--attach=".Length..]).ToList();
        if (attaches.Count > 0)
        {
            ShowAttachedParts(new GameModelLoader(vfs), models[0], skins, attaches);
            return;
        }

        var loader = new GameModelLoader(vfs);
        foreach (var path in models)
        {
            try
            {
                var node = loader.Load(path);
                foreach (var skin in skins)
                {
                    var model = FindAll<Node3D>(node).FirstOrDefault(n => n.HasMeta(MdsModelLoader.DeformFramesMeta));
                    if (model != null)
                        GD.Print($"  costume {skin}: {loader.Mds.ApplySkin(model, skin)} frames");
                }
                Describe(node, 1);
                if (VfsPath.GetExtension(path) is not ("gltf" or "glb"))
                {
                    var r = loader.Mds.LastReport;
                    GD.Print($"  report: frames {r.Objects}, meshes {r.Meshes} ({r.SkinnedMeshes} skinned), triangles {r.Triangles}, skipped {r.SkippedPrimitives}, animations {r.Animations}, missing textures [{string.Join(", ", r.MissingTextures)}]");
                    foreach (var note in r.Notes)
                        GD.Print($"  note: {note}");
                }
                AddChild(node);
                PlayEveryAnimation(node);
                RemoveChild(node);
                node.Free();
            }
            catch (Exception exception)
            {
                GD.PrintErr($"model {path}: {exception}");
                failures++;
            }
        }

        var textureLoader = new TextureLoader(vfs);
        foreach (var path in textures)
        {
            var library = new TextureLibrary();
            if (!textureLoader.LoadInto(library, path))
            {
                GD.PrintErr($"texture {path}: not loaded");
                failures++;
                continue;
            }
            foreach (var texture in library.Textures)
                GD.Print($"texture {path}: {texture.Name} {DescribeTexture(texture.Texture)} alpha={texture.HasTransparency}");
        }

        GD.Print(failures == 0 ? "SMOKE TEST PASSED" : $"SMOKE TEST FAILED ({failures})");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    /// <summary>
    /// Plays each clip to its middle and prints the posed bones and the CPU-skinned corners of
    /// every skinned mesh, so skinning and motion can be checked without a window.
    /// </summary>
    private static void PlayEveryAnimation(Node root)
    {
        foreach (var player in FindAll<AnimationPlayer>(root))
        {
            foreach (var name in player.GetAnimationList())
            {
                var animation = player.GetAnimation(name);
                player.Play(name);
                player.Seek(animation.Length / 2, true);
                GD.Print($"  animation {name}: length {animation.Length:0.###}s, tracks {animation.GetTrackCount()}, game name {(animation.HasMeta("game_name") ? animation.GetMeta("game_name") : "-")}");
                foreach (var skeleton in FindAll<Skeleton3D>(player.GetParent()))
                {
                    for (int bone = 0; bone < skeleton.GetBoneCount(); bone++)
                    {
                        var pose = skeleton.GetBoneGlobalPose(bone);
                        GD.Print($"    {skeleton.GetBoneName(bone)} origin {pose.Origin} x-axis {pose.Basis.Column0}");
                    }
                    foreach (var mesh in FindAll<MeshInstance3D>(skeleton))
                    {
                        if (mesh.Skin != null && mesh.Mesh is ArrayMesh arrayMesh)
                            GD.Print($"    skinned {mesh.Name}: {string.Join(" ", SkinnedCorners(skeleton, mesh.Skin, arrayMesh))}");
                    }
                }
            }
        }
    }

    private static IEnumerable<string> SkinnedCorners(Skeleton3D skeleton, Skin skin, ArrayMesh mesh)
    {
        var arrays = mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
        var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
        for (int v = 0; v < Math.Min(vertices.Length, 6); v++)
        {
            var sum = Vector3.Zero;
            for (int i = 0; i < 4; i++)
            {
                int slot = bones[v * 4 + i];
                float weight = weights[v * 4 + i];
                if (weight == 0)
                    continue;
                var transform = skeleton.GetBoneGlobalPose(skin.GetBindBone(slot)) * skin.GetBindPose(slot);
                sum += transform * vertices[v] * weight;
            }
            yield return $"{vertices[v]}->{sum}";
        }
    }

    /// <summary>
    /// Raw numbers of a pack for checking placement against the game: its script, every MDS frame
    /// (parent, file matrix, vertex bounds) and every bind-pose matrix.
    /// </summary>
    private static void DumpPack(VirtualFileSystem vfs, string path, System.Text.StringBuilder text, HashSet<string>? frameFilter = null)
    {
        static string F(float v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        static string M(System.Numerics.Matrix4x4 m) =>
            $"[{F(m.M11)} {F(m.M12)} {F(m.M13)} {F(m.M14)} | {F(m.M21)} {F(m.M22)} {F(m.M23)} {F(m.M24)} | {F(m.M31)} {F(m.M32)} {F(m.M33)} {F(m.M34)} | {F(m.M41)} {F(m.M42)} {F(m.M43)} {F(m.M44)}]";

        text.AppendLine($"== {path}");
        var pack = vfs.TryOpenPack(VfsPath.Normalize(path));
        if (pack == null)
        {
            text.AppendLine("  not a pack");
            return;
        }
        foreach (var entry in pack.Entries)
            text.AppendLine($"  entry {entry.Name} {entry.Size}");
        if (pack.TryRead(CharacterInfo.FileName) is { } info)
        {
            foreach (var command in OpenCloud.Core.Scripts.GameScript.Parse(info, CharacterInfo.Tags))
                text.AppendLine($"  cfg {command.Tag} {string.Join(", ", command.Args.Select(a => a.AsString ?? F(a.AsFloat)))}");
        }
        foreach (var entry in pack.Entries)
        {
            var data = pack.GetSpan(entry);
            string extension = VfsPath.GetExtension(entry.Name);
            if (extension == "mds" && MdsFile.LooksLikeMds(data))
            {
                foreach (var frame in MdsFile.Parse(data).Objects)
                {
                    text.Append($"  {entry.Name} #{frame.Index} '{frame.Name}' parent {frame.Parent} {M(frame.Matrix)}");
                    if (frame.Model is { Positions.Length: > 0 } model)
                    {
                        var min = model.Positions.Aggregate(System.Numerics.Vector4.Min);
                        var max = model.Positions.Aggregate(System.Numerics.Vector4.Max);
                        var p0 = model.Positions[0];
                        text.Append($" verts {model.Positions.Length} min ({F(min.X)} {F(min.Y)} {F(min.Z)}) max ({F(max.X)} {F(max.Y)} {F(max.Z)}) v0 ({F(p0.X)} {F(p0.Y)} {F(p0.Z)} {F(p0.W)})");
                    }
                    text.AppendLine();
                }
            }
            else if (extension == "mot" && frameFilter != null)
            {
                // Frame names come from the pack's MDS of the same base name.
                var names = pack.TryRead(VfsPath.ChangeExtension(entry.Name, "mds")) is { } mdsData && MdsFile.LooksLikeMds(mdsData)
                    ? MdsFile.Parse(mdsData).Objects.Select(o => o.BaseName).ToArray()
                    : Array.Empty<string>();
                foreach (var list in MotionFile.Parse(data))
                {
                    string name = list.Frame >= 0 && list.Frame < names.Length ? names[list.Frame] : "?";
                    if (!frameFilter.Contains(name))
                        continue;
                    var shown = list.Keys.Where((k, i) => i < 3 || k.Frame is >= 360 and <= 366).Select(k => $"{k.Frame}:({F(k.Value.X)} {F(k.Value.Y)} {F(k.Value.Z)} {F(k.Value.W)})");
                    text.AppendLine($"  {entry.Name} frame #{list.Frame} '{name}' target {list.Target} type {list.Type} keys {list.Keys.Length}: {string.Join(" ", shown)}");
                }
            }
            else if (extension == "bbp")
            {
                var pose = MotionFile.ParseBindPose(data);
                for (int i = 0; i < pose.Length; i++)
                    text.AppendLine($"  {entry.Name} #{i} {M(pose[i])}");
            }
        }
    }

    /// <summary>
    /// Loads a character, dresses it, hangs parts on bones (<c>path@bone</c>) as the viewer does and,
    /// once Godot has posed everything, prints where every mesh really is: its global basis and its
    /// world bounds. Quits when done.
    /// </summary>
    private async void ShowAttachedParts(GameModelLoader loader, string modelPath, List<string> skins, List<string> attaches)
    {
        var node = loader.Load(modelPath);
        foreach (var skin in skins)
        {
            var model = FindAll<Node3D>(node).FirstOrDefault(n => n.HasMeta(MdsModelLoader.DeformFramesMeta));
            if (model != null)
                GD.Print($"  costume {skin}: {loader.Mds.ApplySkin(model, skin)} frames");
        }
        AddChild(node);
        foreach (var attach in attaches)
        {
            var parts = attach.Split('@');
            string bone = parts.Length > 1 ? parts[1] : "hat";
            var skeleton = FindAll<Skeleton3D>(node).FirstOrDefault(s => s.IsVisibleInTree() && s.FindBone(bone) >= 0)
                ?? FindAll<Skeleton3D>(node).FirstOrDefault(s => Enumerable.Range(0, s.GetBoneCount()).Any(i => MdsObject.StripAttributes(s.GetBoneName(i)) == bone));
            if (skeleton == null)
            {
                GD.PrintErr($"  no skeleton with bone {bone}");
                continue;
            }
            int index = Enumerable.Range(0, skeleton.GetBoneCount()).First(i => MdsObject.StripAttributes(skeleton.GetBoneName(i)) == bone);
            var attachment = new BoneAttachment3D { Name = "ref_" + bone };
            skeleton.AddChild(attachment);
            attachment.BoneName = skeleton.GetBoneName(index);
            attachment.AddChild(loader.Load(parts[0]));
        }

        for (int i = 0; i < 3; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        static string V(Vector3 v) => $"({v.X:0.###} {v.Y:0.###} {v.Z:0.###})";
        foreach (var mesh in FindAll<MeshInstance3D>(node))
        {
            if (mesh.Mesh == null)
                continue;
            var t = mesh.GlobalTransform;
            var box = t * mesh.Mesh.GetAabb();
            string skinned = mesh.Skin != null ? " skinned" : string.Empty;
            string hidden = mesh.IsVisibleInTree() ? string.Empty : " hidden";
            GD.Print($"  mesh {node.GetPathTo(mesh)}{skinned}{hidden}: basis x{V(t.Basis.X)} y{V(t.Basis.Y)} z{V(t.Basis.Z)} origin {V(t.Origin)} world box {V(box.Position)}..{V(box.End)}");
        }
        GetTree().Quit(0);
    }

    private static IEnumerable<T> FindAll<T>(Node root) where T : Node
    {
        foreach (var child in root.GetChildren())
        {
            if (child is T match)
                yield return match;
            foreach (var nested in FindAll<T>(child))
                yield return nested;
        }
    }

    private static void Describe(Node node, int depth)
    {
        string indent = new(' ', depth * 2);
        string detail = node switch
        {
            AnimationPlayer player => " " + string.Join(", ", player.GetAnimationList()),
            Skeleton3D skeleton => $" bones={skeleton.GetBoneCount()}" + string.Concat(Enumerable.Range(0, skeleton.GetBoneCount())
                .Select(i => $" [{skeleton.GetBoneName(i)} parent={skeleton.GetBoneParent(i)} rest={skeleton.GetBoneRest(i).Origin}]")),
            BoneAttachment3D attachment => $" bone={attachment.BoneName}",
            MeshInstance3D { Mesh: ArrayMesh mesh, Skin: { } skin } => $" skin binds={skin.GetBindCount()} " + string.Join(" ", Enumerable.Range(0, mesh.GetSurfaceCount()).Select(i => DescribeSurface(mesh, i))),
            MeshInstance3D { Mesh: ArrayMesh mesh } => " " + string.Join(" ", Enumerable.Range(0, mesh.GetSurfaceCount()).Select(i => DescribeSurface(mesh, i))),
            MeshInstance3D { Mesh: { } mesh } => $" surfaces={mesh.GetSurfaceCount()}",
            Node3D spatial => $" origin={spatial.Transform.Origin}",
            _ => string.Empty,
        };
        GD.Print($"{indent}{node.Name} ({node.GetClass()}){detail}");
        foreach (var child in node.GetChildren())
            Describe(child, depth + 1);
    }

    private static string DescribeSurface(ArrayMesh mesh, int surface)
    {
        var arrays = mesh.SurfaceGetArrays(surface);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var material = mesh.SurfaceGetMaterial(surface) as StandardMaterial3D;
        string texture = material?.AlbedoTexture is { } t ? DescribeTexture(t) : "none";
        return $"[surface {surface}: {vertices.Length / 3} tris, albedo {material?.AlbedoColor}, vertex colours {material?.VertexColorUseAsAlbedo}, transparency {material?.Transparency}, texture {texture}]";
    }

    private static string DescribeTexture(Texture2D texture)
    {
        var image = texture.GetImage();
        if (image.IsCompressed())
            return $"{texture.GetWidth()}x{texture.GetHeight()}";
        return $"{texture.GetWidth()}x{texture.GetHeight()} px(0,0)={image.GetPixel(0, 0)} px(w-1,0)={image.GetPixel(image.GetWidth() - 1, 0)}";
    }
}
