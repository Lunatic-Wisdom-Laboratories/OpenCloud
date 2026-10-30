using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OpenCloud.Core;

namespace OpenCloud.Assets.Models;

/// <summary>What a key list sets (<c>MotionKeyType</c>).</summary>
public enum MotionKeyType
{
    Rotation = 0,
    Scale = 1,
    Translation = 2,
    Vertex = 12,
    SkinWeighted = 20,
    SkinAveraged = 21,
    CameraPosition = 30,
    CameraTarget = 31,
    CameraRoll = 32,
    CameraFov = 33,
    MaterialAlpha = 40,
    MaterialColour = 41,
    Visible = 50,
    VisibleTree = 51,
}

/// <summary>One key: the motion frame it stands on (for weight lists, the vertex) and its value.</summary>
public readonly record struct MotionKey(uint Frame, Vector4 Value);

/// <summary>
/// One key list (<c>Mot_List</c>): the keys that drive one part of one frame of a model.
/// </summary>
public sealed class MotionKeyList
{
    /// <summary>Index of the MDS object (frame) the keys drive.</summary>
    public required int Frame { get; init; }

    /// <summary>Part of the frame: a vertex counted from 1, a material, or a bone frame.</summary>
    public required int Target { get; init; }

    public required MotionKeyType Type { get; init; }

    /// <summary>Keys in ascending frame order (as the game's binary search expects).</summary>
    public required MotionKey[] Keys { get; init; }
}

/// <summary>
/// The key-list container shared by motion (<c>.mot</c>), weight (<c>.wgt</c>) and skin key
/// files (gameutil <c>CreateAnimeDataEX</c>, visualmotion <c>CreateVertexWeight</c>): a run of
/// 0x20-byte list headers <c>{ frame, target, type, ?, key_count, more, ?, ? }</c>, each followed
/// by <c>key_count</c> 0x20-byte keys <c>{ u32 frame, pad[12], float value[4] }</c>, while the
/// header's <c>more</c> word is nonzero.
/// </summary>
public static class MotionFile
{
    private const int RecordSize = 0x20;

    public static List<MotionKeyList> Parse(ReadOnlySpan<byte> data)
    {
        var lists = new List<MotionKeyList>();
        int cursor = 0;
        while (LittleEndian.InRange(data, cursor, RecordSize))
        {
            var header = data.Slice(cursor, RecordSize);
            int frame = LittleEndian.I32(header, 0);
            int target = LittleEndian.I32(header, 4);
            int type = LittleEndian.I32(header, 8);
            uint count = LittleEndian.U32(header, 0x10);
            uint more = LittleEndian.U32(header, 0x14);
            cursor += RecordSize;

            if (!LittleEndian.InRange(data, cursor, (long)count * RecordSize))
                throw new InvalidDataException($"Key list {lists.Count} has {count} keys past the end of the file.");

            var keys = new MotionKey[count];
            for (int i = 0; i < keys.Length; i++)
            {
                var key = data.Slice(cursor + i * RecordSize, RecordSize);
                keys[i] = new MotionKey(
                    LittleEndian.U32(key, 0),
                    new Vector4(LittleEndian.F32(key, 0x10), LittleEndian.F32(key, 0x14), LittleEndian.F32(key, 0x18), LittleEndian.F32(key, 0x1C)));
            }
            cursor += (int)count * RecordSize;

            lists.Add(new MotionKeyList { Frame = frame, Target = target, Type = (MotionKeyType)type, Keys = keys });
            if (more == 0)
                break;
        }
        return lists;
    }

    /// <summary>
    /// Reads a bind-pose file (<c>.bbp</c>): one row-vector 4x4 matrix per frame, with no header.
    /// <c>_MOTION</c> copies it over the frames' base matrices, so it is the skeleton's bind pose
    /// in the space of the scene root.
    /// </summary>
    public static Matrix4x4[] ParseBindPose(ReadOnlySpan<byte> data)
    {
        var matrices = new Matrix4x4[data.Length / 0x40];
        for (int i = 0; i < matrices.Length; i++)
            matrices[i] = MdsFile.ReadMatrix(data, i * 0x40);
        return matrices;
    }
}
