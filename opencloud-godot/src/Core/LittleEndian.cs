using System;
using System.Buffers.Binary;

namespace OpenCloud.Core;

/// <summary>
/// Bounds-checked little-endian reads over retail data. Every PS2 file format the engine parses
/// is little-endian, so these helpers keep the parsers free of manual byte shuffling.
/// </summary>
public static class LittleEndian
{
    public static ushort U16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));

    public static short I16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2));

    public static uint U32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    public static int I32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));

    public static ulong U64(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));

    public static float F32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4));

    /// <summary>Returns true when <paramref name="size"/> bytes starting at <paramref name="offset"/> lie inside the data.</summary>
    public static bool InRange(ReadOnlySpan<byte> data, long offset, long size) =>
        offset >= 0 && size >= 0 && offset <= data.Length && size <= data.Length - offset;
}
