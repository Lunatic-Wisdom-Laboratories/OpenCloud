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
        if (models.Count == 0)
            models.Add("chara/c01_base.chr");

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
