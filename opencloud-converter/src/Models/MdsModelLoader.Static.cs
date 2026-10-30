using System.Collections.Generic;
using Godot;
using OpenCloud.Assets.Textures;

namespace OpenCloud.Assets.Models;

/// <summary>
/// Static scenery: map pieces and skies are never skinned or animated by motions, so their
/// MDS objects are built as plain meshes (shared by every placement of the model) and frame
/// transforms instead of a skeleton.
/// </summary>
public sealed partial class MdsModelLoader
{
    /// <summary>Starts a new <see cref="LastReport"/> for a load that does not go through <see cref="Load"/>.</summary>
    public void ResetReport() => LastReport = new Report();

    /// <summary>The mesh of every object that owns a drawable MDT model, by object index.</summary>
    public Dictionary<int, ArrayMesh> BuildObjectMeshes(MdsFile mds, TextureLibrary textures)
    {
        var meshes = new Dictionary<int, ArrayMesh>();
        foreach (var obj in mds.Objects)
        {
            LastReport.Objects++;
            if (obj.Model == null || obj.Model.Primitives.Count == 0)
                continue;
            var mesh = BuildMesh(obj.Model, textures, null);
            if (mesh == null)
                continue;
            meshes[obj.Index] = mesh;
            LastReport.Meshes++;
        }
        return meshes;
    }

    /// <summary>
    /// Scene-space transform of every object, with each root's local matrix passed through
    /// <paramref name="rootLocal"/> (the piece transform a map applies to the model's frame).
    /// </summary>
    public static Transform3D[] ObjectTransforms(MdsFile mds, System.Func<Transform3D, Transform3D>? rootLocal = null)
    {
        var transforms = new Transform3D[mds.Objects.Count];
        foreach (var obj in mds.Objects)
        {
            var local = ToTransform(obj.Matrix);
            transforms[obj.Index] = obj.Parent >= 0 && obj.Parent < obj.Index
                ? transforms[obj.Parent] * local
                : rootLocal?.Invoke(local) ?? local;
        }
        return transforms;
    }

    /// <summary>
    /// Builds the frames of an MDS as a tree of <see cref="Node3D"/>s named after the objects
    /// (attribute text removed), with each object's mesh as a <c>Mesh</c> child.
    /// </summary>
    public static Node3D BuildFrameTree(MdsFile mds, IReadOnlyDictionary<int, ArrayMesh> meshes, string name)
    {
        var root = new Node3D { Name = SafeNodeName(name) };
        var nodes = new Node3D[mds.Objects.Count];
        foreach (var obj in mds.Objects)
        {
            var node = new Node3D { Name = SafeNodeName(obj.BaseName.Length > 0 ? obj.BaseName : $"frame{obj.Index}"), Transform = ToTransform(obj.Matrix) };
            node.SetMeta(FrameMeta, obj.Index);
            var parent = obj.Parent >= 0 && obj.Parent < obj.Index ? nodes[obj.Parent] : root;
            parent.AddChild(node);
            nodes[obj.Index] = node;
            if (meshes.TryGetValue(obj.Index, out var mesh))
                node.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = mesh });
        }
        return root;
    }

    /// <summary>
    /// Every triangle of the objects' models in scene space, three corners each, in the game's
    /// winding. Collision models (<c>CreateCollisionMDT</c>) use plain triangle lists; strips
    /// and fans are expanded too, so nothing is lost if a model has them.
    /// </summary>
    public static List<Vector3> CollectTriangles(MdsFile mds, Transform3D[] transforms)
    {
        var corners = new List<Vector3>();
        foreach (var obj in mds.Objects)
        {
            var model = obj.Model;
            if (model == null)
                continue;
            var transform = transforms[obj.Index];
            foreach (var primitive in model.Primitives)
            {
                foreach (var (a, b, c) in Triangulate(primitive))
                {
                    int ia = primitive.PositionIndex(a), ib = primitive.PositionIndex(b), ic = primitive.PositionIndex(c);
                    if (ia == ib || ib == ic || ia == ic ||
                        (uint)ia >= model.Positions.Length || (uint)ib >= model.Positions.Length || (uint)ic >= model.Positions.Length)
                        continue;
                    corners.Add(transform * ToVector(model.Positions[ia]));
                    corners.Add(transform * ToVector(model.Positions[ib]));
                    corners.Add(transform * ToVector(model.Positions[ic]));
                }
            }
        }
        return corners;
    }

    /// <summary>
    /// The collision triangles of every object, in the object's own space, as unlit meshes
    /// without materials (Godot's clockwise winding). The engine builds collision shapes from them.
    /// </summary>
    public static Dictionary<int, ArrayMesh> BuildCollisionMeshes(MdsFile mds)
    {
        var meshes = new Dictionary<int, ArrayMesh>();
        foreach (var obj in mds.Objects)
        {
            if (obj.Model == null)
                continue;
            var single = new MdsFile { Objects = new[] { new MdsObject { Index = 0, Name = obj.Name, Parent = -1, Matrix = obj.Matrix, Model = obj.Model } } };
            var corners = CollectTriangles(single, new[] { Transform3D.Identity });
            if (corners.Count == 0)
                continue;
            var vertices = new Vector3[corners.Count];
            for (int i = 0; i + 2 < corners.Count; i += 3)
            {
                vertices[i] = corners[i];
                vertices[i + 1] = corners[i + 2];
                vertices[i + 2] = corners[i + 1];
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            meshes[obj.Index] = mesh;
        }
        return meshes;
    }

    private static Vector3 ToVector(System.Numerics.Vector4 v) => new(v.X, v.Y, v.Z);
}
