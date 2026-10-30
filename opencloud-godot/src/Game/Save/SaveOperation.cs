using System;
using System.IO;
using System.Security.Cryptography;

namespace OpenCloud.Game.Save;

/// <summary>Kinds of change to the shared save state.</summary>
public enum SaveOpKind : byte
{
    None = 0,
    /// <summary>A = flag number, B = on.</summary>
    SetBitFlag,
    /// <summary>A = counter number, B = value.</summary>
    SetShortFlag,
    /// <summary>A = map number, B = flag number, C = on.</summary>
    SetMapFlag,
    /// <summary>A = amount, negative to spend.</summary>
    AddMoney,
    /// <summary>A = amount.</summary>
    AddMedals,
    /// <summary>A = item number, B = count.</summary>
    AddItem,
    /// <summary>A = item number, B = count.</summary>
    RemoveItem,
    /// <summary>A, B = inventory places.</summary>
    SwapItems,
    /// <summary>A = Georama part, B = count.</summary>
    SetBuildParts,
    /// <summary>A = Georama part, B = change.</summary>
    AddBuildParts,
    /// <summary>A = day, B = hour of the day as float bits.</summary>
    SetClock,
    /// <summary>A = story point.</summary>
    SetGameProgress,
    /// <summary>A = stopped.</summary>
    SetTimeStop,
    /// <summary>A = party bits.</summary>
    SetPartyMembers,
    /// <summary>A = low word, B = high word.</summary>
    SetCostumeBits,
}

/// <summary>
/// One change to <see cref="GlobalSaveState"/>, small enough to send as a message. The host
/// orders operations and every peer replays them with <see cref="GlobalSaveState.Apply"/>.
/// </summary>
public readonly record struct SaveOperation(SaveOpKind Kind, int A, int B = 0, int C = 0)
{
    public static SaveOperation SetBitFlag(int bit, bool on) => new(SaveOpKind.SetBitFlag, bit, on ? 1 : 0);
    public static SaveOperation SetShortFlag(int index, short value) => new(SaveOpKind.SetShortFlag, index, value);
    public static SaveOperation SetMapFlag(int map, int flag, bool on) => new(SaveOpKind.SetMapFlag, map, flag, on ? 1 : 0);
    public static SaveOperation AddMoney(int amount) => new(SaveOpKind.AddMoney, amount);
    public static SaveOperation AddMedals(int amount) => new(SaveOpKind.AddMedals, amount);
    public static SaveOperation AddItem(int itemNo, int count = 1) => new(SaveOpKind.AddItem, itemNo, count);
    public static SaveOperation RemoveItem(int itemNo, int count = 1) => new(SaveOpKind.RemoveItem, itemNo, count);
    public static SaveOperation SwapItems(int a, int b) => new(SaveOpKind.SwapItems, a, b);
    public static SaveOperation SetBuildParts(int index, int count) => new(SaveOpKind.SetBuildParts, index, count);
    public static SaveOperation AddBuildParts(int index, int delta) => new(SaveOpKind.AddBuildParts, index, delta);
    public static SaveOperation SetClock(int day, float hours) => new(SaveOpKind.SetClock, day, BitConverter.SingleToInt32Bits(hours));
    public static SaveOperation SetGameProgress(int progress) => new(SaveOpKind.SetGameProgress, progress);
    public static SaveOperation SetTimeStop(bool stopped) => new(SaveOpKind.SetTimeStop, stopped ? 1 : 0);
    public static SaveOperation SetPartyMembers(int bits) => new(SaveOpKind.SetPartyMembers, bits);
    public static SaveOperation SetCostumeBits(ulong bits) => new(SaveOpKind.SetCostumeBits, (int)(uint)bits, (int)(uint)(bits >> 32));
}

/// <summary>Byte image and digest of a <see cref="GlobalSaveState"/>, for snapshots and consistency checks.</summary>
public static class SaveStateCodec
{
    private const uint Magic = 0x5347434F; // "OCGS"
    private const int Version = 1;

    public static byte[] Serialize(GlobalSaveState state)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream))
        {
            w.Write(Magic);
            w.Write(Version);
            state.Write(w);
        }
        return stream.ToArray();
    }

    public static void Deserialize(GlobalSaveState state, byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data, writable: false));
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("not a save state image");
        int version = r.ReadInt32();
        if (version != Version)
            throw new InvalidDataException($"save state version {version}, expected {Version}");
        state.Read(r);
    }

    /// <summary>
    /// 64-bit digest of the state. The play time is left out: it counts frames on each machine
    /// and is only brought in line by snapshots.
    /// </summary>
    public static ulong Hash(GlobalSaveState state)
    {
        long playTime = state.PlayTime;
        state.PlayTime = 0;
        try
        {
            return BitConverter.ToUInt64(SHA256.HashData(Serialize(state)), 0);
        }
        finally
        {
            state.PlayTime = playTime;
        }
    }
}
