using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Linq;
using System.Text.Json.Nodes;
using OpenCloud.Core.IO;
using static OpenCloud.Core.JsonValues;

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
/// <c>_DATACOM</c>); the converter writes it as <c>menu/cfg7/comdat.items.json</c>. Every peer of
/// a co-op session needs the same table, because inventory
/// operations are replayed on each of them; <see cref="Fingerprint"/> lets peers compare tables.
/// </summary>
public sealed partial class ItemCatalog
{
    public const string Format = "opencloud.items";
    public const int Version = 1;
    public const string DocumentPath = "menu/cfg7/comdat.items.json";

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
        var data = vfs.TryRead(DocumentPath) ?? throw new System.IO.FileNotFoundException("item table not found", DocumentPath);
        return FromJson(Parse(data, DocumentPath), DocumentPath);
    }

    public JsonObject ToJson()
    {
        var json = Header(Format, Version);
        json["items"] = new JsonArray(_records.Values.OrderBy(r => r.ItemNo).Select(r => (JsonNode?)new JsonObject
        {
            ["no"] = r.ItemNo,
            ["type"] = (int)r.Type,
            ["list_no"] = r.ListNo,
            ["active_set"] = r.ActiveSet,
            ["stack"] = r.StackNum,
            ["max"] = r.MaxNum,
            ["icon_texture"] = r.IconTextureNo,
            ["icon"] = r.IconNo,
            ["message"] = r.MessageNo,
            ["file"] = r.FileName,
            ["attribute"] = (uint)r.Attribute,
        }).ToArray());
        return json;
    }

    public static ItemCatalog FromJson(JsonObject json, string path)
    {
        CheckFormat(json, Format, Version, path);
        var catalog = new ItemCatalog();
        foreach (var item in Objects(json, "items"))
        {
            catalog.Add(new ItemRecord(
                ItemNo: Int(item, "no"),
                Type: (ItemDataType)Int(item, "type"),
                ListNo: Int(item, "list_no"),
                ActiveSet: Bool(item, "active_set"),
                StackNum: Int(item, "stack"),
                MaxNum: Int(item, "max"),
                IconTextureNo: Int(item, "icon_texture"),
                IconNo: Int(item, "icon"),
                MessageNo: Int(item, "message"),
                FileName: Str(item, "file") ?? string.Empty,
                Attribute: (ItemAttribute)(uint)Int(item, "attribute")));
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
