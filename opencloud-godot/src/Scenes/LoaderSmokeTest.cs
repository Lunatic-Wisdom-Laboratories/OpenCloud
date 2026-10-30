using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenCloud.Assets.Maps;
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
        var maps = args.Where(a => a.StartsWith("--map=", StringComparison.Ordinal)).Select(a => a["--map=".Length..]).ToList();
        if (models.Count == 0 && maps.Count == 0)
            models.Add("chara/c01_base.glb");

        var attaches = args.Where(a => a.StartsWith("--attach=", StringComparison.Ordinal)).Select(a => a["--attach=".Length..]).ToList();
        if (attaches.Count > 0)
        {
            ShowAttachedParts(new ModelLoader(vfs), models[0], skins, attaches);
            return;
        }

        var loader = new ModelLoader(vfs);
        foreach (var path in models)
        {
            try
            {
                var node = loader.Load(path);
                var document = loader.LastDocument;
                foreach (var skin in skins)
                {
                    var model = FindAll<Node3D>(node).FirstOrDefault(n => n.HasMeta(ModelMeta.DeformFrames));
                    if (model != null)
                        GD.Print($"  costume {skin}: {loader.ApplySkin(model, skin)} frames");
                }
                Describe(node, 1);
                if (document != null)
                {
                    foreach (var part in document.Models)
                        GD.Print($"  document: {document.Kind} {part.Node}: frames {part.Frames.Length}, deform [{string.Join(", ", part.DeformFrames)}], base matrices {part.BaseMatrices.Length}, actions {part.Animations.Count}");
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

        foreach (var path in textures)
        {
            var library = new TextureLibrary();
            if (!TextureDocument.LoadInto(vfs, library, path))
            {
                GD.PrintErr($"texture {path}: not loaded");
                failures++;
                continue;
            }
            foreach (var texture in library.Textures)
                GD.Print($"texture {path}: {texture.Name} {DescribeTexture(texture.Texture)} alpha={texture.HasTransparency}");
        }

        if (args.Contains("--items"))
        {
            try
            {
                var catalog = OpenCloud.Game.Items.ItemCatalog.Load(vfs);
                GD.Print($"items: {catalog.Count}, fingerprint {catalog.Fingerprint:x16}, " +
                         string.Join(" ", catalog.Records.Select(r => $"[{r.ItemNo} {r.Type} {r.StackNum}/{r.MaxNum} {r.FileName} {r.Attribute}]")));
            }
            catch (Exception exception)
            {
                GD.PrintErr($"items: {exception.Message}");
                failures++;
            }
        }

        var mapLoader = new MapLoader(vfs, new ModelLoader(vfs));
        foreach (var path in maps)
        {
            try
            {
                failures += CheckMap(mapLoader, path);
            }
            catch (Exception exception)
            {
                GD.PrintErr($"map {path}: {exception}");
                failures++;
            }
        }

        GD.Print(failures == 0 ? "SMOKE TEST PASSED" : $"SMOKE TEST FAILED ({failures})");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    /// <summary>
    /// Loads a map, prints what was built (every placed node with its map-space origin) and
    /// steps the clock through the day so time-banded pieces and lighting sets can be checked.
    /// </summary>
    private int CheckMap(MapLoader loader, string path)
    {
        var map = loader.Load(path);
        var r = map.Report;
        var info = map.Info;
        GD.Print($"map {map.Path}: textures {string.Join(",", info.TexturePacks)}, models {string.Join(",", info.ModelPacks)}, lighting sets {info.LightingSets.Count(s => s != null)}, sky info {info.SkyInfo}");
        GD.Print($"  layout: parts {map.Layout.Parts.Count} [{string.Join(", ", map.Layout.Parts.Select(p => $"{p.Name}: {p.Pieces.Count} pieces, {p.FuncPoints.Count} points"))}], placements {map.Layout.Placements.Count}, map points {map.Layout.FuncPoints.Count}");
        GD.Print($"  report: lists {r.ModelLists} ({r.Models} models), placed {r.Placements}, pieces {r.PlacedPieces}, meshes {r.MeshInstances}, characters {r.Characters}, collision tris {r.CollisionTriangles}, points {r.FuncPoints}");
        GD.Print($"  missing models [{string.Join(", ", r.MissingModels)}], missing parts [{string.Join(", ", r.MissingParts)}]");
        foreach (var note in r.Notes)
            GD.Print($"  note: {note}");
        AddChild(map.Root);
        DescribePlaced(map.Root, Transform3D.Identity, 1);
        foreach (float hour in new[] { 3.0f, 7.0f, 12.0f, 19.0f, 22.0f })
        {
            map.SetClock(hour);
            var shown = map.Pieces.Where(p => p.Node.Visible).Select(p => $"{p.Placement.Name}/{p.Node.Name}");
            GD.Print($"  clock {hour:00}h: band {MapInfo.TimeBand(map.NowTime)}, lighting {map.Lighting?.Index.ToString() ?? "-"}, visible [{string.Join(", ", shown)}]");
        }
        map.SetShowHidden(true);
        GD.Print($"  show hidden: {map.Pieces.Count(p => p.Node.Visible)} of {map.Pieces.Count} pieces visible, bounds {map.ComputeBounds()}");
        RemoveChild(map.Root);
        map.Root.Free();
        return r.Placements > 0 ? 0 : 1;
    }

    private static void DescribePlaced(Node node, Transform3D parent, int depth)
    {
        foreach (var child in node.GetChildren())
        {
            var transform = child is Node3D spatial ? parent * spatial.Transform : parent;
            string detail = child switch
            {
                CollisionShape3D { Shape: ConcavePolygonShape3D shape } => $" faces={shape.GetFaces().Length / 3} first={(shape.GetFaces().Length > 0 ? transform * shape.GetFaces()[0] : Vector3.Zero)}",
                StaticBody3D body => $" layer={body.CollisionLayer}",
                MeshInstance3D mesh => $" at {transform.Origin} x-axis {transform.Basis.Column0} layers={mesh.Layers}",
                Node3D => $" at {transform.Origin}",
                _ => string.Empty,
            };
            if (child is Label3D)
                continue;
            GD.Print($"{new string(' ', depth * 2)}{child.Name} ({child.GetClass()}){detail}");
            if (child is not MeshInstance3D || child.GetChildCount() > 0)
                DescribePlaced(child, transform, depth + 1);
        }
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
                GD.Print($"  animation {name}: length {animation.Length:0.###}s, tracks {animation.GetTrackCount()}, game name {(animation.HasMeta(ModelMeta.GameName) ? animation.GetMeta(ModelMeta.GameName) : "-")}, step {(animation.HasMeta(ModelMeta.Step) ? animation.GetMeta(ModelMeta.Step) : "-")}");
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
    /// Loads a character, dresses it, hangs parts on bones (<c>path@bone</c>) as the viewer does and,
    /// once Godot has posed everything, prints where every mesh really is: its global basis and its
    /// world bounds. Quits when done.
    /// </summary>
    private async void ShowAttachedParts(ModelLoader loader, string modelPath, List<string> skins, List<string> attaches)
    {
        var node = loader.Load(modelPath);
        foreach (var skin in skins)
        {
            var model = FindAll<Node3D>(node).FirstOrDefault(n => n.HasMeta(ModelMeta.DeformFrames));
            if (model != null)
                GD.Print($"  costume {skin}: {loader.ApplySkin(model, skin)} frames");
        }
        AddChild(node);
        foreach (var attach in attaches)
        {
            var parts = attach.Split('@');
            string bone = parts.Length > 1 ? parts[1] : "hat";
            var skeleton = FindAll<Skeleton3D>(node).FirstOrDefault(s => s.IsVisibleInTree() && s.FindBone(bone) >= 0)
                ?? FindAll<Skeleton3D>(node).FirstOrDefault(s => Enumerable.Range(0, s.GetBoneCount()).Any(i => NodeNames.StripAttributes(s.GetBoneName(i)) == bone));
            if (skeleton == null)
            {
                GD.PrintErr($"  no skeleton with bone {bone}");
                continue;
            }
            int index = Enumerable.Range(0, skeleton.GetBoneCount()).First(i => NodeNames.StripAttributes(skeleton.GetBoneName(i)) == bone);
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
