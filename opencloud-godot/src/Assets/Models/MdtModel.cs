using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OpenCloud.Core;

namespace OpenCloud.Assets.Models;

/// <summary>
/// Primitive bits of a face record (<c>mgFaceType</c>). The low
/// three bits are the GS primitive; the rest say which index streams each vertex carries.
/// </summary>
[Flags]
public enum MdtFaceFlags : uint
{
    PrimitiveMask = 0x7,
    Flat = 0x8,
    NoTexture = 0x10,
    Colour = 0x100,
    NoNormal = 0x200,
}

/// <summary>GS primitive kinds stored in the low bits of a face type.</summary>
public enum MdtPrimitive
{
    Point = 0,
    Line = 1,
    LineStrip = 2,
    Triangle = 3,
    TriangleStrip = 4,
    TriangleFan = 5,
}

/// <summary>
/// One primitive (<c>FACES_ID</c>): a run of vertices drawn with one material. Each vertex has
/// <see cref="Stride"/> indices, in the order position, normal, UV, colour, leaving out the
/// streams its flags exclude.
/// </summary>
public sealed class MdtPrimitiveRecord
{
    public required MdtFaceFlags Flags { get; init; }
    public required int VertexCount { get; init; }
    public required int Material { get; init; }
    public required int[] Indices { get; init; }

    public MdtPrimitive Primitive => (MdtPrimitive)(Flags & MdtFaceFlags.PrimitiveMask);
    public bool HasNormal => (Flags & MdtFaceFlags.NoNormal) == 0;
    public bool HasUv => (Flags & MdtFaceFlags.NoTexture) == 0;
    public bool HasColour => (Flags & MdtFaceFlags.Colour) != 0;

    public int Stride => StrideFor(Flags);

    public static int StrideFor(MdtFaceFlags flags)
    {
        int stride = 3;
        if ((flags & MdtFaceFlags.Colour) != 0)
            stride++;
        if ((flags & MdtFaceFlags.NoNormal) != 0)
            stride--;
        if ((flags & MdtFaceFlags.NoTexture) != 0)
            stride--;
        return stride;
    }

    public int PositionIndex(int vertex) => Indices[vertex * Stride];

    public int NormalIndex(int vertex) => HasNormal ? Indices[vertex * Stride + 1] : -1;

    public int UvIndex(int vertex) => HasUv ? Indices[vertex * Stride + (HasNormal ? 2 : 1)] : -1;

    public int ColourIndex(int vertex) => HasColour ? Indices[vertex * Stride + Stride - 1] : -1;
}

/// <summary>One MDT material record (<c>MDT_MATERIAL_</c>, 0x60 bytes).</summary>
public sealed record MdtMaterial(Vector4 Diffuse, Vector4 Ambient, string Texture);

/// <summary>
/// An MDT model (<c>MDT_HEADER</c>, 0x40 bytes): separate streams
/// of positions, normals, colours and UVs (all 16-byte float vectors), material records, and a
/// face section of indexed primitives. Offsets are relative to the MDT header.
/// </summary>
public sealed class MdtModel
{
    private const int HeaderSize = 0x40;
    private const int MaterialSize = 0x60;

    public required Vector4[] Positions { get; init; }
    public required Vector4[] Normals { get; init; }
    public required Vector4[] Colours { get; init; }
    public required Vector4[] Uvs { get; init; }
    public required MdtMaterial[] Materials { get; init; }
    public required IReadOnlyList<MdtPrimitiveRecord> Primitives { get; init; }

    public static MdtModel Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || data[0] != 'M' || data[1] != 'D' || data[2] != 'T')
            throw new InvalidDataException("Not an MDT model.");

        var positions = ReadVectors(data, LittleEndian.I32(data, 0x0C), LittleEndian.I32(data, 0x10), "vertex");
        var normals = ReadVectors(data, LittleEndian.I32(data, 0x14), LittleEndian.I32(data, 0x18), "normal");
        var colours = ReadVectors(data, LittleEndian.I32(data, 0x1C), LittleEndian.I32(data, 0x20), "colour");
        int facesSize = LittleEndian.I32(data, 0x24);
        int facesOffset = LittleEndian.I32(data, 0x28);
        var uvs = ReadVectors(data, LittleEndian.I32(data, 0x2C), LittleEndian.I32(data, 0x30), "uv");
        var materials = ReadMaterials(data, LittleEndian.I32(data, 0x34), LittleEndian.I32(data, 0x38));
        var primitives = facesOffset > 0 && facesSize > 0
            ? ReadFaces(data, facesOffset, facesSize)
            : new List<MdtPrimitiveRecord>();

        return new MdtModel
        {
            Positions = positions,
            Normals = normals,
            Colours = colours,
            Uvs = uvs,
            Materials = materials,
            Primitives = primitives,
        };
    }

    private static Vector4[] ReadVectors(ReadOnlySpan<byte> data, int count, int offset, string stream)
    {
        if (count <= 0)
            return Array.Empty<Vector4>();
        if (!LittleEndian.InRange(data, offset, (long)count * 16))
            throw new InvalidDataException($"MDT {stream} stream lies outside the model.");

        var vectors = new Vector4[count];
        for (int i = 0; i < count; i++)
        {
            int at = offset + i * 16;
            vectors[i] = new Vector4(
                LittleEndian.F32(data, at),
                LittleEndian.F32(data, at + 4),
                LittleEndian.F32(data, at + 8),
                LittleEndian.F32(data, at + 12));
        }
        return vectors;
    }

    private static MdtMaterial[] ReadMaterials(ReadOnlySpan<byte> data, int count, int offset)
    {
        if (count <= 0)
            return Array.Empty<MdtMaterial>();
        if (!LittleEndian.InRange(data, offset, (long)count * MaterialSize))
            throw new InvalidDataException("MDT materials lie outside the model.");

        var materials = new MdtMaterial[count];
        for (int i = 0; i < count; i++)
        {
            var record = data.Slice(offset + i * MaterialSize, MaterialSize);
            materials[i] = new MdtMaterial(
                new Vector4(LittleEndian.F32(record, 0), LittleEndian.F32(record, 4), LittleEndian.F32(record, 8), LittleEndian.F32(record, 12)),
                new Vector4(LittleEndian.F32(record, 16), LittleEndian.F32(record, 20), LittleEndian.F32(record, 24), LittleEndian.F32(record, 28)),
                GameText.ReadCString(record, 0x34, 32));
        }
        return materials;
    }

    /// <summary>
    /// Face section: <c>MDT_FACES { ?, header_size, prim_num, ? }</c>, then <c>FACES_ID</c>
    /// records <c>{ type, vertex_num, material, index[vertex_num * stride] }</c> packed back to
    /// back. <c>CreateFace</c> reads the three header words as 16-bit values.
    /// </summary>
    private static List<MdtPrimitiveRecord> ReadFaces(ReadOnlySpan<byte> data, int offset, int size)
    {
        if (!LittleEndian.InRange(data, offset, 0x10))
            throw new InvalidDataException("MDT face section lies outside the model.");

        int end = (int)Math.Min((long)offset + size, data.Length);
        int headerSize = LittleEndian.I32(data, offset + 4);
        int count = LittleEndian.I32(data, offset + 8);
        int cursor = offset + (headerSize > 0 ? headerSize : 0x10);

        var primitives = new List<MdtPrimitiveRecord>(Math.Max(count, 0));
        for (int i = 0; i < count; i++)
        {
            if (cursor + 12 > end)
                throw new InvalidDataException($"MDT primitive {i} lies outside the face section.");

            var flags = (MdtFaceFlags)LittleEndian.U16(data, cursor);
            int vertexCount = LittleEndian.U16(data, cursor + 4);
            int material = LittleEndian.U16(data, cursor + 8);
            int indexCount = vertexCount * MdtPrimitiveRecord.StrideFor(flags);
            cursor += 12;
            if (cursor + indexCount * 4 > end)
                throw new InvalidDataException($"MDT primitive {i} indices lie outside the face section.");

            var indices = new int[indexCount];
            for (int k = 0; k < indexCount; k++)
                indices[k] = LittleEndian.I32(data, cursor + k * 4);
            cursor += indexCount * 4;

            primitives.Add(new MdtPrimitiveRecord
            {
                Flags = flags,
                VertexCount = vertexCount,
                Material = material,
                Indices = indices,
            });
        }
        return primitives;
    }
}
