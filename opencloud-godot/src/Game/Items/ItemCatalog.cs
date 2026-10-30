using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using OpenCloud.Core.IO;
using OpenCloud.Core.Scripts;

namespace OpenCloud.Game.Items;

/// <summary>Item types of the common item table (<c>ITEM_DATA</c>, gamedata.hpp).</summary>
public enum ItemDataType
{
    None = 0,
    MaxMelee = 1,
    MaxGun = 2,
    MonicaMelee = 3,
    MonicaMagic = 4,
    MaxClothes = 5,
    MaxHat = 6,
    MaxShoes = 7,
    MonicaClothes = 8,
    MonicaAccessory = 9,
    MonicaBoots = 10,
    RoboCore = 11,
    RoboBody = 12,
    RoboArm = 13,
    RoboLeg = 14,
    RoboEnergyPack = 15,
    Crystal = 16,
    Spectol = 17,
    Gem = 18,
    Coin = 19,
    GeoramaMaterial = 20,
    BadgeBox = 21,
    Recovery = 22,
    Throwing = 23,
    Powder = 24,
    Amulet = 25,
    DungeonKey = 26,
    KeyItem = 27,
    GiftBox = 28,
    Aquarium = 29,
    Fish = 30,
    Lure = 32,
    Etc = 33,
    MonsterDrop = 34,
    Boiled = 35,
}

/// <summary>Family of an owned item, which decides what data it carries (<c>USED_ITEM_TYPE</c>).</summary>
public enum ItemFamily
{
    None = 0,
    Item = 1,
    Attach = 2,
    Weapon = 3,
    Costume = 4,
    RoboPart = 5,
    Fish = 6,
    GiftBox = 7,
    Boiled = 8,
}

/// <summary>Bits of <see cref="ItemRecord.Attribute"/> (<c>ITEM_ATTRIBUTE</c>).</summary>
[Flags]
public enum ItemAttribute : uint
{
    None = 0,
    /// <summary>Only <see cref="ItemRecord.MaxNum"/> of the item can be held, counting every place.</summary>
    Unique = 0x40,
}

/// <summary>Item numbers the save logic treats specially (<c>ITEM_ID</c>, gamedata.hpp).</summary>
public static class ItemIds
{
    public const int Spectol = 0xB9;
    public const int MonsterDrop = 0x17F;
    /// <summary>Highest item number plus one that the item number table covers (<c>ITEM_NO_MAX</c>).</summary>
    public const int Limit = 0x200;
}

/// <summary>One row of the common item table (<c>CDataCommon</c>), as a <c>COM</c> command of <c>comdat.cfg</c> fills it.</summary>
public sealed record ItemRecord(
    int ItemNo,
    ItemDataType Type,
    int ListNo,
    bool ActiveSet,
    int StackNum,
    int MaxNum,
    int IconTextureNo,
    int IconNo,
    int MessageNo,
    string FileName,
    ItemAttribute Attribute)
{
    public ItemFamily Family => ItemCatalog.FamilyOf(Type);
}

/// <summary>
/// The common item table: per item number its type, how many fit in one inventory place
/// (<see cref="ItemRecord.StackNum"/>) and how many can be owned (<see cref="ItemRecord.MaxNum"/>).
/// The retail game builds it from <c>menu/cfg7/comdat.cfg</c> (<c>CGameData::LoadData</c>,
/// <c>_DATACOM</c>). Every peer of a co-op session needs the same table, because inventory
/// operations are replayed on each of them; <see cref="Fingerprint"/> lets peers compare tables.
/// </summary>
public sealed class ItemCatalog
{
    public const string CommonDataPath = "menu/cfg7/comdat.cfg";

    /// <summary>Tag table of <c>gamedata_tag</c>, for binary scripts.</summary>
    public static readonly string[] Tags =
    {
        "COMINIT", "COM", "WEPNUM", "WEP", "WEP_ST", "WEP_ST_L", "WEP_ST2", "WEP_ST2_L", "WEP_SPE",
        "WEP_BUILD", "ITEMINIT", "ITEM", "AT_INIT", "AT_ST", "AT_ST2", "AT_ST_SP", "ROBOINIT",
        "RB_PARTS", "GRDNUM", "GRD", "FISHINIT", "FISH", "MES_SYS", "MES_SYSSPE",
    };

    private readonly Dictionary<int, ItemRecord> _records = new();

    public int Count => _records.Count;
    public IEnumerable<ItemRecord> Records => _records.Values;

    private ulong? _fingerprint;

    /// <summary>Digest of every record, equal on peers that loaded the same table.</summary>
    public ulong Fingerprint => _fingerprint ??= ComputeFingerprint();

    public ItemRecord? Get(int itemNo) => _records.GetValueOrDefault(itemNo);

    public void Add(ItemRecord record)
    {
        // Weapons can be owned up to the item board size however many the table says.
        if (record.Family == ItemFamily.Weapon && record.MaxNum > 100)
            record = record with { MaxNum = 0x90 };
        _records[record.ItemNo] = record;
        _fingerprint = null;
    }

    public static ItemCatalog Load(VirtualFileSystem vfs)
    {
        var data = vfs.TryRead(CommonDataPath) ?? throw new System.IO.FileNotFoundException("item table not found", CommonDataPath);
        return Parse(data);
    }

    public static ItemCatalog Parse(ReadOnlySpan<byte> script)
    {
        var catalog = new ItemCatalog();
        foreach (var command in GameScript.Parse(script, Tags))
        {
            if (command.Tag != "COM")
                continue;
            catalog.Add(new ItemRecord(
                ItemNo: command.Arg(0).AsInt,
                Type: (ItemDataType)command.Arg(1).AsInt,
                ListNo: command.Arg(2).AsInt,
                ActiveSet: command.Arg(3).AsInt != 0,
                StackNum: command.Arg(4).AsInt,
                MaxNum: (ushort)command.Arg(5).AsInt,
                IconTextureNo: command.Arg(6).AsInt,
                IconNo: command.Arg(7).AsInt,
                MessageNo: command.Arg(8).AsInt,
                FileName: command.Arg(9).AsString ?? string.Empty,
                Attribute: (ItemAttribute)(uint)command.Arg(10).AsInt));
        }
        return catalog;
    }

    /// <summary>Family of an item type (<c>ConvertUsedItemType</c>).</summary>
    public static ItemFamily FamilyOf(ItemDataType type)
    {
        switch (type)
        {
            case ItemDataType.GiftBox: return ItemFamily.GiftBox;
            case ItemDataType.Fish: return ItemFamily.Fish;
            case ItemDataType.Boiled: return ItemFamily.Boiled;
        }
        int t = (int)type;
        if (t > (int)ItemDataType.None && t < (int)ItemDataType.MaxClothes)
            return ItemFamily.Weapon;
        if (t >= (int)ItemDataType.MaxClothes && t < (int)ItemDataType.RoboCore)
            return ItemFamily.Costume;
        if (t > (int)ItemDataType.RoboCore && t <= (int)ItemDataType.RoboEnergyPack)
            return ItemFamily.RoboPart;
        if ((t >= (int)ItemDataType.Crystal && t <= (int)ItemDataType.Coin) || type == ItemDataType.MonsterDrop)
            return ItemFamily.Attach;
        if (type == ItemDataType.RoboCore || t >= (int)ItemDataType.GeoramaMaterial)
            return ItemFamily.Item;
        return ItemFamily.None;
    }

    private ulong ComputeFingerprint()
    {
        var ordered = new List<ItemRecord>(_records.Values);
        ordered.Sort((a, b) => a.ItemNo.CompareTo(b.ItemNo));
        var writer = new System.IO.MemoryStream();
        using (var w = new System.IO.BinaryWriter(writer))
        {
            foreach (var r in ordered)
            {
                w.Write(r.ItemNo);
                w.Write((int)r.Type);
                w.Write(r.StackNum);
                w.Write(r.MaxNum);
                w.Write((uint)r.Attribute);
            }
        }
        return BitConverter.ToUInt64(SHA256.HashData(writer.ToArray()), 0);
    }
}
