using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Godot;

namespace OpenCloud.Converter.Export;

/// <summary>
/// Writes a node tree built from the original data as a binary glTF 2.0 file (GLB). The engine
/// loads these files back with Godot's glTF importer, and any glTF tool (Blender...) opens them.
///
/// The tree is written as the converter builds it, with these rules:
/// <list type="bullet">
/// <item>The root node is the file itself: its children become the scene's root nodes.</item>
/// <item>Every bone of a <see cref="Skeleton3D"/> becomes a node at its rest transform, child of the
/// skeleton's parent. The skeleton itself is not written; its transform must be the identity.</item>
/// <item>One extra skin per skeleton lists every bone, so importers rebuild the whole frame
/// hierarchy as one skeleton even where no mesh is skinned to a frame.</item>
/// <item>The children of a <see cref="BoneAttachment3D"/> become children of their bone's node.</item>
/// <item>Godot's front faces are clockwise and glTF's counter-clockwise: triangles are reversed.</item>
/// <item>Each animation of every <see cref="AnimationPlayer"/> library becomes one glTF animation
/// named <c>&lt;library&gt;_&lt;clip&gt;</c> (or just the clip in the default library).</item>
/// </list>
/// Node visibility, metadata and the game's own semantics are not part of glTF: the converter
/// writes them in the model's JSON companion.
/// </summary>
public sealed class GltfWriter
{
    private readonly JsonArray _nodes = new();
    private readonly JsonArray _meshes = new();
    private readonly JsonArray _materials = new();
    private readonly JsonArray _textures = new();
    private readonly JsonArray _images = new();
    private readonly JsonArray _samplers = new();
    private readonly JsonArray _skins = new();
    private readonly JsonArray _animations = new();
    private readonly JsonArray _accessors = new();
    private readonly JsonArray _bufferViews = new();
    private readonly MemoryStream _binary = new();
    private readonly HashSet<string> _extensions = new(StringComparer.Ordinal);

    private readonly Dictionary<Node, int> _nodeIndex = new();
    private readonly Dictionary<Skeleton3D, int[]> _boneNodes = new();
    private readonly Dictionary<Material, int> _materialIndex = new();
    private readonly Dictionary<Texture2D, int> _textureIndex = new();
    private readonly Dictionary<string, int> _samplerIndex = new(StringComparer.Ordinal);
    private readonly List<(MeshInstance3D Mesh, int Node)> _skinned = new();
    private readonly List<string> _warnings = new();

    /// <summary>Problems met while writing (duplicate joints, unsupported tracks...).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// Names each glTF animation from its player, library and clip; by default
    /// <c>&lt;library&gt;_&lt;clip&gt;</c>, or the clip alone in the default library.
    /// </summary>
    public Func<AnimationPlayer, string, string, string> ClipName { get; init; } =
        (_, library, clip) => library.Length > 0 ? $"{library}_{clip}" : clip;

    /// <summary>Writes the tree under <paramref name="root"/> as a GLB file.</summary>
    public byte[] Write(Node3D root) => Build(root);

    private byte[] Build(Node3D root)
    {
        var sceneNodes = new JsonArray();
        foreach (var child in root.GetChildren())
        {
            foreach (int index in AddNode(child))
                sceneNodes.Add(index);
        }

        foreach (var (mesh, node) in _skinned)
            AddSkin(mesh, node);
        foreach (var (skeleton, bones) in _boneNodes)
            AddSkeletonSkin(skeleton, bones);
        foreach (var player in FindAll<AnimationPlayer>(root))
            AddAnimations(player);

        var document = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "OpenCloud converter" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["name"] = root.Name.ToString(), ["nodes"] = sceneNodes }),
            ["nodes"] = _nodes,
        };
        void Put(string key, JsonArray array)
        {
            if (array.Count > 0)
                document[key] = array;
        }
        Put("meshes", _meshes);
        Put("materials", _materials);
        Put("textures", _textures);
        Put("images", _images);
        Put("samplers", _samplers);
        Put("skins", _skins);
        Put("animations", _animations);
        Put("accessors", _accessors);
        Put("bufferViews", _bufferViews);
        if (_binary.Length > 0)
            document["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = _binary.Length });
        if (_extensions.Count > 0)
            document["extensionsUsed"] = new JsonArray(_extensions.Order(StringComparer.Ordinal).Select(e => (JsonNode)e!).ToArray());

        return Glb(Encoding.UTF8.GetBytes(document.ToJsonString()), _binary.ToArray());
    }

    private static byte[] Glb(byte[] json, byte[] binary)
    {
        int jsonLength = Align(json.Length);
        int binaryLength = Align(binary.Length);
        int total = 12 + 8 + jsonLength + (binary.Length > 0 ? 8 + binaryLength : 0);
        using var stream = new MemoryStream(total);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67u); // "glTF"
        writer.Write(2u);
        writer.Write((uint)total);
        writer.Write((uint)jsonLength);
        writer.Write(0x4E4F534Au); // "JSON"
        writer.Write(json);
        for (int i = json.Length; i < jsonLength; i++)
            writer.Write((byte)' ');
        if (binary.Length > 0)
        {
            writer.Write((uint)binaryLength);
            writer.Write(0x004E4942u); // "BIN\0"
            writer.Write(binary);
            for (int i = binary.Length; i < binaryLength; i++)
                writer.Write((byte)0);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static int Align(int value) => (value + 3) & ~3;

    // --- Nodes ---------------------------------------------------------------------------------

    /// <summary>Adds a node and its subtree; returns the glTF nodes that stand for it.</summary>
    private IEnumerable<int> AddNode(Node node)
    {
        switch (node)
        {
            case AnimationPlayer:
                return Array.Empty<int>();
            case Skeleton3D skeleton:
                return AddSkeleton(skeleton);
            case BoneAttachment3D:
                // Handled by its skeleton.
                return Array.Empty<int>();
            case Node3D spatial:
            {
                var json = new JsonObject { ["name"] = spatial.Name.ToString() };
                WriteTransform(json, spatial.Transform);
                int index = Push(json);
                _nodeIndex[spatial] = index;
                if (spatial is MeshInstance3D { Mesh: ArrayMesh mesh } instance)
                {
                    json["mesh"] = AddMesh(mesh, instance.Name.ToString());
                    if (instance.Skin != null)
                        _skinned.Add((instance, index));
                }
                var children = new JsonArray();
                foreach (var child in spatial.GetChildren())
                {
                    foreach (int childIndex in AddNode(child))
                        children.Add(childIndex);
                }
                if (children.Count > 0)
                    json["children"] = children;
                return new[] { index };
            }
            default:
                return Array.Empty<int>();
        }
    }

    private int Push(JsonObject node)
    {
        _nodes.Add(node);
        return _nodes.Count - 1;
    }

    private IEnumerable<int> AddSkeleton(Skeleton3D skeleton)
    {
        if (!skeleton.Transform.IsEqualApprox(Transform3D.Identity))
            _warnings.Add($"skeleton {skeleton.Name} has a transform of its own; it is dropped");

        int count = skeleton.GetBoneCount();
        var bones = new int[count];
        var children = new JsonArray[count];
        for (int bone = 0; bone < count; bone++)
        {
            var json = new JsonObject { ["name"] = skeleton.GetBoneName(bone) };
            WriteTransform(json, skeleton.GetBoneRest(bone));
            bones[bone] = Push(json);
            children[bone] = new JsonArray();
        }
        _boneNodes[skeleton] = bones;

        var roots = new List<int>();
        for (int bone = 0; bone < count; bone++)
        {
            int parent = skeleton.GetBoneParent(bone);
            if (parent >= 0)
                children[parent].Add(bones[bone]);
            else
                roots.Add(bones[bone]);
        }

        // Children of the skeleton: skinned meshes stay beside the bones, attachments go under their bone.
        foreach (var child in skeleton.GetChildren())
        {
            if (child is BoneAttachment3D attachment)
            {
                int bone = skeleton.FindBone(attachment.BoneName);
                if (bone < 0)
                {
                    _warnings.Add($"attachment {attachment.Name} names missing bone {attachment.BoneName}");
                    continue;
                }
                foreach (var attached in attachment.GetChildren())
                {
                    foreach (int index in AddNode(attached))
                        children[bone].Add(index);
                }
                continue;
            }
            roots.AddRange(AddNode(child));
        }

        for (int bone = 0; bone < count; bone++)
        {
            if (children[bone].Count > 0)
                _nodes[bones[bone]]!["children"] = children[bone];
        }
        return roots;
    }

    private static void WriteTransform(JsonObject json, Transform3D transform)
    {
        var basis = transform.Basis;
        var scale = basis.Scale;
        // Store a matrix when the basis is sheared (or mirrored), which TRS cannot express.
        bool orthogonal = Math.Abs(basis.Column0.Dot(basis.Column1)) < 1e-4f * scale.X * scale.Y &&
                          Math.Abs(basis.Column1.Dot(basis.Column2)) < 1e-4f * scale.Y * scale.Z &&
                          Math.Abs(basis.Column0.Dot(basis.Column2)) < 1e-4f * scale.X * scale.Z;
        if (!orthogonal || basis.Determinant() <= 0 || scale.X == 0 || scale.Y == 0 || scale.Z == 0)
        {
            json["matrix"] = Matrix(transform);
            return;
        }
        var rotation = basis.Orthonormalized().GetRotationQuaternion();
        if (!transform.Origin.IsZeroApprox())
            json["translation"] = Vec(transform.Origin);
        if (!rotation.IsEqualApprox(Quaternion.Identity))
            json["rotation"] = Quat(rotation);
        if (!scale.IsEqualApprox(Vector3.One))
            json["scale"] = Vec(scale);
    }

    private static JsonArray Vec(Vector3 v) => new(v.X, v.Y, v.Z);

    private static JsonArray Quat(Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    private static JsonArray Matrix(Transform3D t)
    {
        var b = t.Basis;
        return new JsonArray(
            b.Column0.X, b.Column0.Y, b.Column0.Z, 0f,
            b.Column1.X, b.Column1.Y, b.Column1.Z, 0f,
            b.Column2.X, b.Column2.Y, b.Column2.Z, 0f,
            t.Origin.X, t.Origin.Y, t.Origin.Z, 1f);
    }

    // --- Meshes --------------------------------------------------------------------------------

    private int AddMesh(ArrayMesh mesh, string name)
    {
        var primitives = new JsonArray();
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            if (mesh.SurfaceGetPrimitiveType(surface) != Mesh.PrimitiveType.Triangles)
            {
                _warnings.Add($"mesh {name}: surface {surface} is not a triangle list; skipped");
                continue;
            }
            var arrays = mesh.SurfaceGetArrays(surface);
            var positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (positions.Length == 0)
                continue;

            var attributes = new JsonObject { ["POSITION"] = AddVec3(positions, withBounds: true) };
            var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            if (normals.Length == positions.Length)
                attributes["NORMAL"] = AddVec3(normals.Select(n => n.IsZeroApprox() ? Vector3.Up : n.Normalized()).ToArray(), false);
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            if (uvs.Length == positions.Length)
                attributes["TEXCOORD_0"] = AddFloats(uvs.SelectMany(v => new[] { v.X, v.Y }).ToArray(), "VEC2");

            var material = mesh.SurfaceGetMaterial(surface);
            var colours = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
            if (colours.Length == positions.Length && material is BaseMaterial3D { VertexColorUseAsAlbedo: true })
                attributes["COLOR_0"] = AddFloats(colours.SelectMany(c => new[] { c.R, c.G, c.B, c.A }).ToArray(), "VEC4");

            var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            if (bones.Length == positions.Length * 4 && weights.Length == positions.Length * 4)
            {
                attributes["JOINTS_0"] = AddJoints(bones);
                attributes["WEIGHTS_0"] = AddFloats(NormaliseWeights(weights), "VEC4");
            }

            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            if (indices.Length == 0)
                indices = Enumerable.Range(0, positions.Length).ToArray();
            var flipped = new int[indices.Length - indices.Length % 3];
            for (int i = 0; i < flipped.Length; i += 3)
            {
                flipped[i] = indices[i];
                flipped[i + 1] = indices[i + 2];
                flipped[i + 2] = indices[i + 1];
            }

            var primitive = new JsonObject
            {
                ["attributes"] = attributes,
                ["indices"] = AddIndices(flipped),
                ["mode"] = 4,
            };
            if (material != null)
                primitive["material"] = AddMaterial(material);
            primitives.Add(primitive);
        }
        _meshes.Add(new JsonObject { ["name"] = name, ["primitives"] = primitives });
        return _meshes.Count - 1;
    }

    private static float[] NormaliseWeights(float[] weights)
    {
        var result = (float[])weights.Clone();
        for (int v = 0; v < result.Length; v += 4)
        {
            float sum = result[v] + result[v + 1] + result[v + 2] + result[v + 3];
            if (sum <= 0)
            {
                result[v] = 1;
                continue;
            }
            for (int i = 0; i < 4; i++)
                result[v + i] /= sum;
        }
        return result;
    }

    // --- Skins ---------------------------------------------------------------------------------

    private void AddSkin(MeshInstance3D instance, int node)
    {
        var skin = instance.Skin!;
        var skeleton = instance.GetNodeOrNull<Skeleton3D>(instance.Skeleton);
        if (skeleton == null || !_boneNodes.TryGetValue(skeleton, out var bones))
        {
            _warnings.Add($"skinned mesh {instance.Name} has no exported skeleton; written unskinned");
            return;
        }

        // glTF joints are unique; Godot binds may repeat a bone (slots whose bone is missing follow the root).
        var joints = new List<int>();
        var poses = new List<Transform3D>();
        var slotJoint = new int[skin.GetBindCount()];
        for (int slot = 0; slot < slotJoint.Length; slot++)
        {
            int bone = skin.GetBindBone(slot);
            if (bone < 0)
                bone = skeleton.FindBone(skin.GetBindName(slot));
            if (bone < 0 || bone >= bones.Length)
                bone = 0;
            var pose = skin.GetBindPose(slot);
            int existing = joints.IndexOf(bones[bone]);
            if (existing >= 0)
            {
                if (!poses[existing].IsEqualApprox(pose))
                    _warnings.Add($"skin of {instance.Name}: slot {slot} repeats bone {skeleton.GetBoneName(bone)} with another bind pose; the first is kept");
                slotJoint[slot] = existing;
                continue;
            }
            slotJoint[slot] = joints.Count;
            joints.Add(bones[bone]);
            poses.Add(pose);
        }

        // Rewrite the mesh's joint indices from bind slots to joint positions.
        int meshIndex = _nodes[node]!["mesh"]!.GetValue<int>();
        foreach (var primitive in _meshes[meshIndex]!["primitives"]!.AsArray())
        {
            if (primitive!["attributes"]!["JOINTS_0"] is { } accessor)
                RemapJoints(accessor.GetValue<int>(), slotJoint);
        }

        _skins.Add(new JsonObject
        {
            ["name"] = instance.Name.ToString(),
            ["joints"] = new JsonArray(joints.Select(j => (JsonNode)j!).ToArray()),
            ["inverseBindMatrices"] = AddMatrices(poses),
        });
        _nodes[node]!["skin"] = _skins.Count - 1;
    }

    /// <summary>A skin over every bone, at the inverse of its rest pose in skeleton space.</summary>
    private void AddSkeletonSkin(Skeleton3D skeleton, int[] bones)
    {
        var poses = Enumerable.Range(0, bones.Length).Select(b => skeleton.GetBoneGlobalRest(b).AffineInverse()).ToList();
        _skins.Add(new JsonObject
        {
            ["name"] = skeleton.Name.ToString(),
            ["joints"] = new JsonArray(bones.Select(j => (JsonNode)j!).ToArray()),
            ["inverseBindMatrices"] = AddMatrices(poses),
        });
    }

    // --- Materials -----------------------------------------------------------------------------

    private int AddMaterial(Material material)
    {
        if (_materialIndex.TryGetValue(material, out int existing))
            return existing;

        var json = new JsonObject();
        if (!string.IsNullOrEmpty(material.ResourceName))
            json["name"] = material.ResourceName;
        var pbr = new JsonObject { ["metallicFactor"] = 0.0f, ["roughnessFactor"] = 1.0f };
        json["pbrMetallicRoughness"] = pbr;

        if (material is BaseMaterial3D standard)
        {
            var albedo = standard.AlbedoColor;
            pbr["baseColorFactor"] = new JsonArray(albedo.R, albedo.G, albedo.B, albedo.A);
            if (standard.AlbedoTexture is { } texture)
                pbr["baseColorTexture"] = new JsonObject { ["index"] = AddTexture(texture, standard) };
            if (standard.CullMode == BaseMaterial3D.CullModeEnum.Disabled)
                json["doubleSided"] = true;
            switch (standard.Transparency)
            {
                case BaseMaterial3D.TransparencyEnum.Alpha:
                case BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass:
                case BaseMaterial3D.TransparencyEnum.AlphaHash:
                    json["alphaMode"] = "BLEND";
                    break;
                case BaseMaterial3D.TransparencyEnum.AlphaScissor:
                    json["alphaMode"] = "MASK";
                    json["alphaCutoff"] = standard.AlphaScissorThreshold;
                    break;
            }
            if (standard.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded)
            {
                json["extensions"] = new JsonObject { ["KHR_materials_unlit"] = new JsonObject() };
                _extensions.Add("KHR_materials_unlit");
            }
        }

        _materials.Add(json);
        return _materialIndex[material] = _materials.Count - 1;
    }

    private int AddTexture(Texture2D texture, BaseMaterial3D material)
    {
        // Wrap is a sampler property; textures are shared per (image, sampler).
        if (!_textureIndex.TryGetValue(texture, out int image))
        {
            var png = texture.GetImage() is { } source ? Decompressed(source).SavePngToBuffer() : Array.Empty<byte>();
            var json = new JsonObject { ["bufferView"] = AddBufferView(png, null), ["mimeType"] = "image/png" };
            if (!string.IsNullOrEmpty(texture.ResourceName))
                json["name"] = texture.ResourceName;
            _images.Add(json);
            image = _textureIndex[texture] = _images.Count - 1;
        }

        int wrap = material.TextureRepeat ? 10497 : 33071;
        string key = $"{wrap}";
        if (!_samplerIndex.TryGetValue(key, out int sampler))
        {
            _samplers.Add(new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = wrap, ["wrapT"] = wrap });
            sampler = _samplerIndex[key] = _samplers.Count - 1;
        }

        for (int i = 0; i < _textures.Count; i++)
        {
            var existing = _textures[i]!;
            if (existing["source"]!.GetValue<int>() == image && existing["sampler"]!.GetValue<int>() == sampler)
                return i;
        }
        _textures.Add(new JsonObject { ["source"] = image, ["sampler"] = sampler });
        return _textures.Count - 1;
    }

    private static Image Decompressed(Image image)
    {
        if (!image.IsCompressed())
            return image;
        var copy = (Image)image.Duplicate();
        copy.Decompress();
        return copy;
    }

    // --- Animations ----------------------------------------------------------------------------

    private void AddAnimations(AnimationPlayer player)
    {
        var root = player.GetNodeOrNull(player.RootNode);
        if (root == null)
            return;
        foreach (var libraryName in player.GetAnimationLibraryList())
        {
            var library = player.GetAnimationLibrary(libraryName);
            foreach (var clip in library.GetAnimationList())
            {
                string name = ClipName(player, libraryName.ToString(), clip.ToString());
                var json = AddAnimation(root, library.GetAnimation(clip), name);
                if (json != null)
                    _animations.Add(json);
            }
        }
    }

    private JsonObject? AddAnimation(Node root, Animation animation, string name)
    {
        var samplers = new JsonArray();
        var channels = new JsonArray();
        for (int track = 0; track < animation.GetTrackCount(); track++)
        {
            var type = animation.TrackGetType(track);
            string path = type switch
            {
                Animation.TrackType.Position3D => "translation",
                Animation.TrackType.Rotation3D => "rotation",
                Animation.TrackType.Scale3D => "scale",
                _ => string.Empty,
            };
            if (path.Length == 0)
            {
                _warnings.Add($"animation {name}: track {track} of type {type} is not written");
                continue;
            }
            int keys = animation.TrackGetKeyCount(track);
            if (keys == 0)
                continue;

            var trackPath = animation.TrackGetPath(track);
            var target = root.GetNodeOrNull(new NodePath(trackPath.GetConcatenatedNames()));
            int node;
            if (target is Skeleton3D skeleton && trackPath.GetSubNameCount() > 0 && _boneNodes.TryGetValue(skeleton, out var bones))
            {
                int bone = skeleton.FindBone(trackPath.GetSubName(0));
                if (bone < 0)
                    continue;
                node = bones[bone];
            }
            else if (target != null && _nodeIndex.TryGetValue(target, out int index))
            {
                node = index;
            }
            else
            {
                _warnings.Add($"animation {name}: track target {trackPath} is not in the file");
                continue;
            }

            var times = new float[keys];
            var values = new List<float>();
            for (int key = 0; key < keys; key++)
            {
                times[key] = (float)animation.TrackGetKeyTime(track, key);
                var value = animation.TrackGetKeyValue(track, key);
                if (type == Animation.TrackType.Rotation3D)
                {
                    var q = value.AsQuaternion();
                    values.AddRange(new[] { q.X, q.Y, q.Z, q.W });
                }
                else
                {
                    var v = value.AsVector3();
                    values.AddRange(new[] { v.X, v.Y, v.Z });
                }
            }

            string interpolation = animation.TrackGetInterpolationType(track) == Animation.InterpolationType.Nearest ? "STEP" : "LINEAR";
            samplers.Add(new JsonObject
            {
                ["input"] = AddTimes(times),
                ["output"] = AddFloats(values.ToArray(), type == Animation.TrackType.Rotation3D ? "VEC4" : "VEC3", vertexData: false),
                ["interpolation"] = interpolation,
            });
            channels.Add(new JsonObject
            {
                ["sampler"] = samplers.Count - 1,
                ["target"] = new JsonObject { ["node"] = node, ["path"] = path },
            });
        }
        if (channels.Count == 0)
        {
            _warnings.Add($"animation {name} has no channels; it is not written");
            return null;
        }
        return new JsonObject { ["name"] = name, ["samplers"] = samplers, ["channels"] = channels };
    }

    // --- Buffers -------------------------------------------------------------------------------

    private int AddBufferView(byte[] data, int? target)
    {
        while (_binary.Length % 4 != 0)
            _binary.WriteByte(0);
        var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = _binary.Length, ["byteLength"] = data.Length };
        if (target != null)
            view["target"] = target.Value;
        _binary.Write(data);
        _bufferViews.Add(view);
        return _bufferViews.Count - 1;
    }

    private int AddAccessor(byte[] data, int componentType, int count, string type, int? target, JsonArray? min = null, JsonArray? max = null)
    {
        var accessor = new JsonObject
        {
            ["bufferView"] = AddBufferView(data, target),
            ["componentType"] = componentType,
            ["count"] = count,
            ["type"] = type,
        };
        if (min != null)
            accessor["min"] = min;
        if (max != null)
            accessor["max"] = max;
        _accessors.Add(accessor);
        return _accessors.Count - 1;
    }

    private const int ArrayBuffer = 34962;
    private const int ElementArrayBuffer = 34963;

    private int AddVec3(Vector3[] values, bool withBounds)
    {
        var floats = values.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray();
        if (!withBounds)
            return AddFloats(floats, "VEC3");
        var min = new Vector3(values.Min(v => v.X), values.Min(v => v.Y), values.Min(v => v.Z));
        var max = new Vector3(values.Max(v => v.X), values.Max(v => v.Y), values.Max(v => v.Z));
        return AddAccessor(Bytes(floats), 5126, values.Length, "VEC3", ArrayBuffer, Vec(min), Vec(max));
    }

    private int AddFloats(float[] values, string type, bool vertexData = true)
    {
        int width = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => 16 };
        return AddAccessor(Bytes(values), 5126, values.Length / width, type, vertexData ? ArrayBuffer : null);
    }

    private int AddTimes(float[] times) =>
        AddAccessor(Bytes(times), 5126, times.Length, "SCALAR", null, new JsonArray(times.Min()), new JsonArray(times.Max()));

    private int AddMatrices(List<Transform3D> transforms)
    {
        var floats = transforms.SelectMany(t => Matrix(t).Select(n => n!.GetValue<float>())).ToArray();
        return AddAccessor(Bytes(floats), 5126, transforms.Count, "MAT4", null);
    }

    private int AddIndices(int[] indices)
    {
        var bytes = new byte[indices.Length * 4];
        Buffer.BlockCopy(indices, 0, bytes, 0, bytes.Length);
        return AddAccessor(bytes, 5125, indices.Length, "SCALAR", ElementArrayBuffer);
    }

    private int AddJoints(int[] bones)
    {
        var bytes = new byte[bones.Length * 2];
        for (int i = 0; i < bones.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (ushort)Math.Max(0, bones[i]));
        return AddAccessor(bytes, 5123, bones.Length / 4, "VEC4", ArrayBuffer);
    }

    /// <summary>Rewrites a JOINTS_0 accessor in place through a slot → joint table.</summary>
    private void RemapJoints(int accessor, int[] slotJoint)
    {
        int view = _accessors[accessor]!["bufferView"]!.GetValue<int>();
        long offset = _bufferViews[view]!["byteOffset"]!.GetValue<long>();
        int length = _bufferViews[view]!["byteLength"]!.GetValue<int>();
        var buffer = _binary.GetBuffer();
        for (long at = offset; at < offset + length; at += 2)
        {
            int slot = BitConverter.ToUInt16(buffer, (int)at);
            int joint = slot < slotJoint.Length ? slotJoint[slot] : 0;
            BitConverter.TryWriteBytes(buffer.AsSpan((int)at), (ushort)joint);
        }
    }

    private static byte[] Bytes(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
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
}
