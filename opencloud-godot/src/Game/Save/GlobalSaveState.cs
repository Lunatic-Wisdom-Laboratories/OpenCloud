using System;
using System.Collections.Generic;
using System.IO;
using OpenCloud.Game.Items;

namespace OpenCloud.Game.Save;

/// <summary>Story flags with a known meaning (<c>SAVE_BIT_FLAG</c>).</summary>
public static class SaveBitFlag
{
    public const int LureRod = 0x30;
    public const int EarlyGameDone = 0x36;
    public const int ItemBoardExpanded = 0xFE;
    public const int SphedaUnlocked = 0x13D;
    public const int FishingContestUnlocked = 0x158;
    public const int FinnyFrenzyUnlocked = 0x1A8;
    public const int Chapter8 = 0x2E0;
    public const int CostumeUnlock = 0x31F;
}

/// <summary>
/// The world state every player of a session shares, the part of the game's save record
/// (<c>CSaveData</c> with its <c>CUserDataManager</c>) that is not tied to one player: story
/// flags and counters, map flags, the clock, Georama part counts, money, medals, the shared item
/// board and the status of Max and Monica.
/// <para>
/// In a networked session only the host changes it directly; peers change it by submitting
/// <see cref="SaveOperation"/>s, which the host applies with <see cref="Apply"/> and replays to
/// every peer in the same order. Apply is deterministic, so peers that start from the same
/// snapshot and replay the same operations hold the same state.
/// </para>
/// </summary>
public sealed class GlobalSaveState
{
    public const int BitFlagCount = 0x800;
    public const int ShortFlagCount = 0x80;
    public const int MapCount = 0x100;
    public const int MapFlagCount = 128;
    public const int BuildPartsCount = 0x100;
    public const int BuildPartsMax = 9999;
    public const int MoneyMax = 999999;
    public const int MedalMax = 999;

    private readonly short[] _shortFlags = new short[ShortFlagCount];
    private readonly short[] _buildParts = new short[BuildPartsCount];

    public GlobalSaveState(ItemCatalog catalog)
    {
        Catalog = catalog;
        MapFlags = new FlagSet[MapCount];
        for (int i = 0; i < MapCount; i++)
            MapFlags[i] = new FlagSet(MapFlagCount);
        Characters = new[] { new CharacterRecord(), new CharacterRecord() };
    }

    /// <summary>Item table that inventory operations consult.</summary>
    public ItemCatalog Catalog { get; }

    public FlagSet BitFlags { get; } = new(BitFlagCount);
    public FlagSet[] MapFlags { get; }
    public Inventory Inventory { get; } = new();
    /// <summary>Max and Monica, by <see cref="PlayableCharacter"/>.</summary>
    public CharacterRecord[] Characters { get; }

    /// <summary>Play time in frames at 60 Hz.</summary>
    public long PlayTime { get; set; }
    /// <summary>Point the story has reached.</summary>
    public int GameProgress { get; private set; }
    /// <summary>Days that have passed.</summary>
    public int Day { get; private set; }
    /// <summary>Time of day, in hours.</summary>
    public float TimeOfDay { get; private set; }
    /// <summary>Whether the time of day stands still in town.</summary>
    public bool TimeStopped { get; private set; }

    public int Money { get; private set; }
    public int Medals { get; private set; }
    /// <summary>Characters in the party, bits by <see cref="PlayableCharacter"/>.</summary>
    public int PartyMembers { get; private set; }
    /// <summary>Costumes collected, one bit each.</summary>
    public ulong CostumeBits { get; private set; }

    /// <summary>Raised after an operation changed the state.</summary>
    public event Action<SaveOperation>? Changed;

    public bool GetBitFlag(int bit) => BitFlags[bit];
    public short GetShortFlag(int index) => (uint)index < ShortFlagCount ? _shortFlags[index] : (short)0;
    public bool GetMapFlag(int map, int flag) => (uint)map < MapCount && MapFlags[map][flag];
    public int GetBuildParts(int index) => (uint)index < BuildPartsCount ? _buildParts[index] : 0;

    /// <summary>The places outside the board whose items count as owned: active items and equipment.</summary>
    public IEnumerable<OwnedItem> CharacterItems()
    {
        foreach (var character in Characters)
        {
            foreach (var item in character.ActiveItems)
                yield return item;
            foreach (var item in character.Equipment)
                yield return item;
        }
    }

    /// <summary>The places items are taken from after the board: the characters' active items.</summary>
    private IEnumerable<OwnedItem> ActiveItems()
    {
        foreach (var character in Characters)
        {
            foreach (var item in character.ActiveItems)
                yield return item;
        }
    }

    public int CountItem(int itemNo) => Inventory.CountOf(itemNo, CharacterItems());

    /// <summary>Clears everything to the state of a new game (<c>CSaveData::Initialize</c>).</summary>
    public void Reset()
    {
        BitFlags.Clear();
        Array.Clear(_shortFlags);
        foreach (var map in MapFlags)
            map.Clear();
        Array.Clear(_buildParts);
        Inventory.Clear();
        Inventory.Expanded = false;
        foreach (var character in Characters)
            character.Clear();
        PlayTime = 0;
        GameProgress = 0;
        Day = 0;
        TimeOfDay = 0;
        TimeStopped = false;
        Money = 0;
        Medals = 0;
        PartyMembers = 0;
        CostumeBits = 0;
    }

    /// <summary>Whether the operation is well formed and would act on something that exists.</summary>
    public bool Validate(SaveOperation op) => op.Kind switch
    {
        SaveOpKind.SetBitFlag => BitFlags.InRange(op.A),
        SaveOpKind.SetShortFlag => (uint)op.A < ShortFlagCount,
        SaveOpKind.SetMapFlag => (uint)op.A < MapCount && (uint)op.B < MapFlagCount,
        SaveOpKind.AddMoney or SaveOpKind.AddMedals => true,
        SaveOpKind.AddItem or SaveOpKind.RemoveItem => Catalog.Get(op.A) != null && op.B > 0,
        SaveOpKind.SwapItems => (uint)op.A < Inventory.SlotCount && (uint)op.B < Inventory.SlotCount,
        SaveOpKind.SetBuildParts or SaveOpKind.AddBuildParts => (uint)op.A < BuildPartsCount,
        SaveOpKind.SetClock => op.A >= 0,
        SaveOpKind.SetGameProgress => true,
        SaveOpKind.SetTimeStop => true,
        SaveOpKind.SetPartyMembers => true,
        SaveOpKind.SetCostumeBits => true,
        _ => false,
    };

    /// <summary>
    /// Applies one operation and gives its result: the previous value for setters, the new
    /// value for adders, the count moved for items. Invalid operations change nothing and give 0.
    /// </summary>
    public int Apply(SaveOperation op)
    {
        if (!Validate(op))
            return 0;
        int result = op.Kind switch
        {
            SaveOpKind.SetBitFlag => SetBitFlag(op.A, op.B != 0) ? 1 : 0,
            SaveOpKind.SetShortFlag => SetShortFlag(op.A, (short)op.B),
            SaveOpKind.SetMapFlag => MapFlags[op.A].Set(op.B, op.C != 0) ? 1 : 0,
            SaveOpKind.AddMoney => Money = Math.Clamp(Money + op.A, 0, MoneyMax),
            SaveOpKind.AddMedals => Medals = Math.Clamp(Medals + op.A, 0, MedalMax),
            SaveOpKind.AddItem => Inventory.Add(Catalog, op.A, op.B, CharacterItems()),
            SaveOpKind.RemoveItem => Inventory.Remove(Catalog, op.A, op.B, ActiveItems()),
            SaveOpKind.SwapItems => Inventory.Swap(op.A, op.B) ? 1 : 0,
            SaveOpKind.SetBuildParts => _buildParts[op.A] = (short)Math.Clamp(op.B, 0, BuildPartsMax),
            SaveOpKind.AddBuildParts => _buildParts[op.A] = (short)Math.Clamp(_buildParts[op.A] + op.B, 0, BuildPartsMax),
            SaveOpKind.SetClock => SetClock(op.A, BitConverter.Int32BitsToSingle(op.B)),
            SaveOpKind.SetGameProgress => GameProgress = op.A,
            SaveOpKind.SetTimeStop => (TimeStopped = op.A != 0) ? 1 : 0,
            SaveOpKind.SetPartyMembers => PartyMembers = op.A,
            SaveOpKind.SetCostumeBits => SetCostumeBits(op),
            _ => 0,
        };
        Changed?.Invoke(op);
        return result;
    }

    private bool SetBitFlag(int bit, bool on)
    {
        bool was = BitFlags.Set(bit, on);
        if (bit == SaveBitFlag.ItemBoardExpanded)
            Inventory.Expanded = on;
        return was;
    }

    private short SetShortFlag(int index, short value)
    {
        short old = _shortFlags[index];
        _shortFlags[index] = value;
        return old;
    }

    private int SetClock(int day, float hours)
    {
        Day = day;
        TimeOfDay = float.IsFinite(hours) ? Math.Clamp(hours, 0, 24) : 0;
        return day;
    }

    private int SetCostumeBits(SaveOperation op)
    {
        CostumeBits = (uint)op.A | ((ulong)(uint)op.B << 32);
        return op.A;
    }

    public void CopyFrom(GlobalSaveState other)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            other.Write(w);
        stream.Position = 0;
        using var r = new BinaryReader(stream);
        Read(r);
    }

    /// <summary>Writes the state in a fixed little-endian layout, the same on every peer.</summary>
    public void Write(BinaryWriter w)
    {
        BitFlags.Write(w);
        foreach (var value in _shortFlags)
            w.Write(value);
        foreach (var map in MapFlags)
            map.Write(w);
        foreach (var value in _buildParts)
            w.Write(value);
        Inventory.Write(w);
        foreach (var character in Characters)
            character.Write(w);
        w.Write(PlayTime);
        w.Write(GameProgress);
        w.Write(Day);
        w.Write(TimeOfDay);
        w.Write(TimeStopped);
        w.Write(Money);
        w.Write(Medals);
        w.Write(PartyMembers);
        w.Write(CostumeBits);
    }

    public void Read(BinaryReader r)
    {
        BitFlags.Read(r);
        for (int i = 0; i < ShortFlagCount; i++)
            _shortFlags[i] = r.ReadInt16();
        foreach (var map in MapFlags)
            map.Read(r);
        for (int i = 0; i < BuildPartsCount; i++)
            _buildParts[i] = r.ReadInt16();
        Inventory.Read(r);
        foreach (var character in Characters)
            character.Read(r);
        PlayTime = r.ReadInt64();
        GameProgress = r.ReadInt32();
        Day = r.ReadInt32();
        TimeOfDay = r.ReadSingle();
        TimeStopped = r.ReadBoolean();
        Money = r.ReadInt32();
        Medals = r.ReadInt32();
        PartyMembers = r.ReadInt32();
        CostumeBits = r.ReadUInt64();
    }
}
