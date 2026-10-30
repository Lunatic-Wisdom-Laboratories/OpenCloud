using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenCloud.Assets.Textures;
using OpenCloud.Core.IO;
using OpenCloud.Rendering;
using NVector4 = System.Numerics.Vector4;

namespace OpenCloud.Assets.Models;

/// <summary>
/// Converts retail MDS scenes into Godot nodes at run time.
///
/// Every MDS object becomes a bone of one <see cref="Skeleton3D"/> (its rest pose is the
/// object's local matrix, so animation in a later stage drives these bones), and every object
/// that owns an MDT model gets a <see cref="MeshInstance3D"/> attached to its bone. MDT vertices
/// are stored in their object's space, so the rest pose shows the model as authored.
///
/// The game's axes and units already match Godot's (right-handed, +Y up). Row-vector game
/// matrices map row i to basis column i. Game front faces are counter-clockwise; Godot's are
/// clockwise, so triangles are emitted in reverse order.
/// </summary>
public sealed class MdsModelLoader
{
    private readonly VirtualFileSystem _vfs;

    public MdsModelLoader(VirtualFileSystem vfs)
    {
        _vfs = vfs;
    }

    /// <summary>Statistics of the last conversion, for diagnostics.</summary>
    public sealed class Report
    {
        public int Objects;
        public int Meshes;
        public int Triangles;
        public int SkippedPrimitives;
        public readonly SortedSet<string> MissingTextures = new(StringComparer.Ordinal);
    }

    public Report LastReport { get; private set; } = new();

    /// <summary>
    /// Loads an MDS file, or every MDS of a PACK (<c>.chr</c>, <c>.pak</c>, <c>.pcp</c>...), with
    /// the textures of the IMG/TIM2 files beside it.
    /// </summary>
    public Node3D Load(string path, TextureLibrary? sharedTextures = null)
    {
        path = VfsPath.Normalize(path);
        LastReport = new Report();

        if (VfsPath.GetExtension(path) == "mds")
        {
            var data = _vfs.ReadAllBytes(path);
            var textures = new TextureLibrary();
            if (sharedTextures != null)
                textures.AddRange(sharedTextures);
            LoadSiblingTextures(path, textures);
            return BuildScene(MdsFile.Parse(data), VfsPath.GetFileName(path), textures);
        }

        var pack = _vfs.TryOpenPack(path)
            ?? throw new System.IO.InvalidDataException($"'{path}' is neither an MDS file nor a PACK archive.");
        return LoadPack(pack, VfsPath.GetFileName(path), sharedTextures);
    }

    /// <summary>
    /// Loads a PACK as a character or model bundle: textures from every IMG/TIM2 member, then
    /// every MDS member as a child scene. Nested PACKs (child characters) are loaded too.
    /// </summary>
    public Node3D LoadPack(PackArchive pack, string name, TextureLibrary? sharedTextures = null, int depth = 0)
    {
        var textures = new TextureLibrary();
        if (sharedTextures != null)
            textures.AddRange(sharedTextures);
        foreach (var entry in pack.Entries)
            TryAddTextures(textures, entry.Name, pack.GetSpan(entry));

        var root = new Node3D { Name = SafeNodeName(name) };
        foreach (var entry in pack.Entries)
        {
            var data = pack.GetSpan(entry);
            string extension = VfsPath.GetExtension(entry.Name);
            if (extension == "mds" && MdsFile.LooksLikeMds(data))
            {
                try
                {
                    root.AddChild(BuildScene(MdsFile.Parse(data), entry.Name, textures));
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

    private void LoadSiblingTextures(string mdsPath, TextureLibrary textures)
    {
        string stem = VfsPath.ChangeExtension(mdsPath, string.Empty).TrimEnd('.');
        foreach (var extension in new[] { "img", "tm2" })
        {
            var data = _vfs.TryRead(stem + "." + extension);
            if (data != null)
                TryAddTextures(textures, stem + "." + extension, data);
        }
    }

    private static void TryAddTextures(TextureLibrary textures, string name, ReadOnlySpan<byte> data)
    {
        try
        {
            if (ImgArchive.IsImg(data))
                TextureLoader.AddImgArchive(textures, data.ToArray());
            else if (Tim2Decoder.IsTim2(data))
                TextureLoader.AddTim2(textures, System.IO.Path.GetFileNameWithoutExtension(name), data.ToArray());
        }
        catch (System.IO.InvalidDataException exception)
        {
            GD.PushWarning($"Textures '{name}': {exception.Message}");
        }
    }

    /// <summary>Builds the node tree of one MDS scene.</summary>
    public Node3D BuildScene(MdsFile mds, string name, TextureLibrary textures)
    {
        var root = new Node3D { Name = SafeNodeName(name) };
        var skeleton = new Skeleton3D { Name = "Skeleton" };
        root.AddChild(skeleton);

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var boneNames = new string[mds.Objects.Count];
        foreach (var obj in mds.Objects)
        {
            string boneName = obj.Name.Length > 0 ? obj.Name : $"frame{obj.Index}";
            if (!usedNames.Add(boneName))
            {
                boneName = $"{boneName}#{obj.Index}";
                usedNames.Add(boneName);
            }
            boneNames[obj.Index] = boneName;
            skeleton.AddBone(boneName);
        }

        foreach (var obj in mds.Objects)
        {
            // MDS parents always precede children in retail scenes; ignore anything that would form a loop.
            if (obj.Parent >= 0 && obj.Parent < obj.Index)
                skeleton.SetBoneParent(obj.Index, obj.Parent);
            skeleton.SetBoneRest(obj.Index, ToTransform(obj.Matrix));
        }
        skeleton.ResetBonePoses();

        foreach (var obj in mds.Objects)
        {
            LastReport.Objects++;
            if (obj.Model == null || obj.Model.Primitives.Count == 0)
                continue;

            var mesh = BuildMesh(obj.Model, textures);
            if (mesh == null)
                continue;

            var attachment = new BoneAttachment3D { Name = SafeNodeName(boneNames[obj.Index]) };
            skeleton.AddChild(attachment);
            attachment.BoneName = boneNames[obj.Index];
            attachment.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = mesh });
            LastReport.Meshes++;
        }
        return root;
    }

    /// <summary>Converts a game row-vector matrix into a Godot transform.</summary>
    public static Transform3D ToTransform(System.Numerics.Matrix4x4 m) =>
        new(new Basis(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33)),
            new Vector3(m.M41, m.M42, m.M43));

    private sealed class SurfaceBuilder
    {
        public readonly List<Vector3> Positions = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Vector2> Uvs = new();
        public readonly List<Color> Colours = new();
        public bool HasColours;
    }

    /// <summary>
    /// Expands one MDT model into an <see cref="ArrayMesh"/> with one surface per material.
    /// Triangle lists, strips and fans become triangles; point and line primitives are skipped.
    /// </summary>
    private ArrayMesh? BuildMesh(MdtModel model, TextureLibrary textures)
    {
        // Colours are floats; if any exceeds 1 the model uses the GS scale where 0x80 is 1.0.
        float colourScale = model.Colours.Any(c => c.X > 1.001f || c.Y > 1.001f || c.Z > 1.001f || c.W > 1.001f) ? 1.0f / 128.0f : 1.0f;

        var surfaces = new SortedDictionary<int, SurfaceBuilder>();
        foreach (var primitive in model.Primitives)
        {
            if (primitive.Primitive is not (MdtPrimitive.Triangle or MdtPrimitive.TriangleStrip or MdtPrimitive.TriangleFan))
            {
                LastReport.SkippedPrimitives++;
                continue;
            }
            if (!surfaces.TryGetValue(primitive.Material, out var surface))
                surfaces[primitive.Material] = surface = new SurfaceBuilder();
            surface.HasColours |= primitive.HasColour && model.Colours.Length > 0;

            foreach (var (a, b, c) in Triangulate(primitive))
            {
                if (primitive.PositionIndex(a) == primitive.PositionIndex(b) ||
                    primitive.PositionIndex(b) == primitive.PositionIndex(c) ||
                    primitive.PositionIndex(a) == primitive.PositionIndex(c))
                    continue;

                // Reverse to Godot's clockwise front faces.
                if (!AddCorner(surface, model, primitive, a, colourScale) |
                    !AddCorner(surface, model, primitive, c, colourScale) |
                    !AddCorner(surface, model, primitive, b, colourScale))
                {
                    surface.Positions.RemoveRange(surface.Positions.Count - 3, 3);
                    surface.Normals.RemoveRange(surface.Normals.Count - 3, 3);
                    surface.Uvs.RemoveRange(surface.Uvs.Count - 3, 3);
                    surface.Colours.RemoveRange(surface.Colours.Count - 3, 3);
                    continue;
                }
                FixMissingNormals(surface, primitive.HasNormal && model.Normals.Length > 0);
                LastReport.Triangles++;
            }
        }

        var mesh = new ArrayMesh();
        foreach (var (materialIndex, surface) in surfaces)
        {
            if (surface.Positions.Count == 0)
                continue;

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = surface.Positions.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = surface.Normals.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = surface.Uvs.ToArray();
            if (surface.HasColours)
                arrays[(int)Mesh.ArrayType.Color] = surface.Colours.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, CreateMaterial(model, materialIndex, textures, surface.HasColours));
        }
        return mesh.GetSurfaceCount() > 0 ? mesh : null;
    }

    /// <summary>Corner triples of a primitive, in the game's (counter-clockwise) winding.</summary>
    private static IEnumerable<(int, int, int)> Triangulate(MdtPrimitiveRecord primitive)
    {
        int n = primitive.VertexCount;
        switch (primitive.Primitive)
        {
            case MdtPrimitive.Triangle:
                for (int i = 0; i + 2 < n; i += 3)
                    yield return (i, i + 1, i + 2);
                break;
            case MdtPrimitive.TriangleStrip:
                // Strips alternate their winding, as glTF and the GS do.
                for (int i = 0; i + 2 < n; i++)
                    yield return (i & 1) == 0 ? (i, i + 1, i + 2) : (i + 1, i, i + 2);
                break;
            case MdtPrimitive.TriangleFan:
                for (int i = 1; i + 1 < n; i++)
                    yield return (0, i, i + 1);
                break;
        }
    }

    private static bool AddCorner(SurfaceBuilder surface, MdtModel model, MdtPrimitiveRecord primitive, int vertex, float colourScale)
    {
        bool valid = true;
        int position = primitive.PositionIndex(vertex);
        surface.Positions.Add(TryGet(model.Positions, position, ref valid) is { } p ? new Vector3(p.X, p.Y, p.Z) : Vector3.Zero);

        int normal = primitive.NormalIndex(vertex);
        var n = normal >= 0 && normal < model.Normals.Length ? model.Normals[normal] : NVector4.Zero;
        var godotNormal = new Vector3(n.X, n.Y, n.Z);
        surface.Normals.Add(godotNormal.LengthSquared() > 1e-12f ? godotNormal.Normalized() : Vector3.Zero);

        int uv = primitive.UvIndex(vertex);
        var t = uv >= 0 && uv < model.Uvs.Length ? model.Uvs[uv] : NVector4.Zero;
        surface.Uvs.Add(new Vector2(t.X, t.Y));

        int colour = primitive.ColourIndex(vertex);
        if (colour >= 0 && colour < model.Colours.Length)
        {
            var c = model.Colours[colour] * colourScale;
            surface.Colours.Add(new Color(Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1), Math.Clamp(c.Z, 0, 1), Math.Clamp(c.W, 0, 1)));
        }
        else
        {
            surface.Colours.Add(Colors.White);
        }
        return valid;
    }

    private static NVector4? TryGet(NVector4[] values, int index, ref bool valid)
    {
        if (index >= 0 && index < values.Length)
            return values[index];
        valid = false;
        return null;
    }

    /// <summary>Gives the last triangle a face normal where the model stores none.</summary>
    private static void FixMissingNormals(SurfaceBuilder surface, bool hasNormals)
    {
        int last = surface.Positions.Count - 3;
        bool missing = !hasNormals;
        for (int i = 0; i < 3 && !missing; i++)
            missing = surface.Normals[last + i] == Vector3.Zero;
        if (!missing)
            return;

        var p0 = surface.Positions[last];
        var p1 = surface.Positions[last + 1];
        var p2 = surface.Positions[last + 2];
        // Corners are stored clockwise for Godot, so this cross product points out of the front face.
        var faceNormal = (p2 - p0).Cross(p1 - p0).Normalized();
        for (int i = 0; i < 3; i++)
        {
            if (!hasNormals || surface.Normals[last + i] == Vector3.Zero)
                surface.Normals[last + i] = faceNormal;
        }
    }

    private Material CreateMaterial(MdtModel model, int index, TextureLibrary textures, bool vertexColours)
    {
        if (index < 0 || index >= model.Materials.Length)
            return GameMaterialFactory.CreateMissing(Colors.White, vertexColours);

        var record = model.Materials[index];
        var diffuse = record.Diffuse;
        // Untextured, colourless records carry no usable diffuse; fall back to white.
        if (diffuse.X <= 0 && diffuse.Y <= 0 && diffuse.Z <= 0)
            diffuse = new NVector4(1, 1, 1, diffuse.W);
        if (diffuse.W <= 0)
            diffuse.W = 1;
        var colour = new Color(Math.Clamp(diffuse.X, 0, 1), Math.Clamp(diffuse.Y, 0, 1), Math.Clamp(diffuse.Z, 0, 1), Math.Clamp(diffuse.W, 0, 1));

        if (string.IsNullOrEmpty(record.Texture))
            return GameMaterialFactory.Create(colour, null, vertexColours);

        var texture = textures.Find(record.Texture);
        if (texture == null)
        {
            LastReport.MissingTextures.Add(record.Texture);
            return GameMaterialFactory.CreateMissing(colour, vertexColours);
        }
        return GameMaterialFactory.Create(colour, texture, vertexColours);
    }

    internal static string SafeNodeName(string name)
    {
        // Godot reserves these characters in node names.
        foreach (char c in ".:@/\"%")
            name = name.Replace(c, '_');
        return name.Length == 0 ? "Node" : name;
    }
}
