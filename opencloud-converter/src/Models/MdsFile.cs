using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OpenCloud.Core;

namespace OpenCloud.Assets.Models;

/// <summary>
/// An MDS scene: a 0x10-byte header <c>{ ?, ?, object_num,
/// object_ofs }</c> followed by <c>MDTOBJ_HEADER</c> records. Each object names itself, points
/// to its parent and may own an MDT model. Plain data, independent of Godot.
/// </summary>
public sealed class MdsFile
{
    private const int HeaderSize = 0x10;
    private const int ObjectRecordSize = 0x70;

    public required IReadOnlyList<MdsObject> Objects { get; init; }

    public static bool LooksLikeMds(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + ObjectRecordSize)
            return false;
        uint count = LittleEndian.U32(data, 8);
        int first = LittleEndian.I32(data, 12);
        return count > 0 && count < 4096 && first >= HeaderSize && LittleEndian.InRange(data, first, ObjectRecordSize);
    }

    public static MdsFile Parse(ReadOnlySpan<byte> data)
    {
        if (!LooksLikeMds(data))
            throw new InvalidDataException("Not an MDS scene.");

        int count = (int)LittleEndian.U32(data, 8);
        int cursor = LittleEndian.I32(data, 12);
        var objects = new List<MdsObject>(count);
        for (int i = 0; i < count; i++)
        {
            if (!LittleEndian.InRange(data, cursor, ObjectRecordSize))
                throw new InvalidDataException($"MDS object {i} lies outside the file.");

            var record = data.Slice(cursor, ObjectRecordSize);
            int size = LittleEndian.I32(record, 4);
            int mdtOffset = LittleEndian.I32(record, 0x28);
            int parent = LittleEndian.I32(record, 0x2C);

            MdtModel? model = null;
            if (mdtOffset > 0 && mdtOffset < data.Length)
                model = MdtModel.Parse(data[mdtOffset..]);

            objects.Add(new MdsObject
            {
                Index = i,
                Name = GameText.ReadCString(record, 8, 32),
                Parent = parent >= 0 && parent < count ? parent : -1,
                Matrix = ReadMatrix(record, 0x30),
                Model = model,
            });

            // The loader strides by each record's own size.
            cursor += size > 0 ? size : ObjectRecordSize;
        }
        return new MdsFile { Objects = objects };
    }

    /// <summary>Reads a row-vector <c>sceVu0FMATRIX</c>: rows are basis vectors, row 3 the translation.</summary>
    internal static Matrix4x4 ReadMatrix(ReadOnlySpan<byte> data, int offset)
    {
        Span<float> m = stackalloc float[16];
        for (int i = 0; i < 16; i++)
            m[i] = LittleEndian.F32(data, offset + i * 4);
        return new Matrix4x4(
            m[0], m[1], m[2], m[3],
            m[4], m[5], m[6], m[7],
            m[8], m[9], m[10], m[11],
            m[12], m[13], m[14], m[15]);
    }
}

/// <summary>One object (frame) of an MDS scene.</summary>
public sealed class MdsObject
{
    public required int Index { get; init; }

    /// <summary>Frame name; text after "--" carries frame attributes (<c>mgSetFrameAttr</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Index of the parent object, or -1 for a root.</summary>
    public required int Parent { get; init; }

    /// <summary>Transform relative to the parent, in the game's row-vector convention.</summary>
    public required Matrix4x4 Matrix { get; init; }

    public required MdtModel? Model { get; init; }

    /// <summary>Name without the attribute text.</summary>
    public string BaseName => StripAttributes(Name);

    /// <summary>A frame name without the attribute text after "--" (<c>mgFrameNameComp</c>).</summary>
    public static string StripAttributes(string name)
    {
        int attributes = name.IndexOf("--", StringComparison.Ordinal);
        return attributes < 0 ? name : name[..attributes];
    }
}
