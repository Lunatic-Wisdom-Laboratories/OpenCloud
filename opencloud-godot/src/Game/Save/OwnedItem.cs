using System.IO;
using OpenCloud.Game.Items;

namespace OpenCloud.Game.Save;

/// <summary>A gauge with a capacity and a current amount, for health, durability and absorption (<c>COMMON_GAGE</c>).</summary>
public struct Gauge
{
    public float Max;
    public float Now;

    public Gauge(float max, float now)
    {
        Max = max;
        Now = now;
    }

    public readonly bool IsFull => Now >= Max;

    /// <summary>How full the gauge is, from 0 to 1, or 0 without capacity.</summary>
    public readonly float Rate => Max > 0 ? Now / Max : 0;

    /// <summary>Adds an amount, keeping the gauge between empty and full.</summary>
    public void Add(float amount) => Now = System.Math.Clamp(Now + amount, 0, Max);

    internal readonly void Write(BinaryWriter w)
    {
        w.Write(Max);
        w.Write(Now);
    }

    internal static Gauge Read(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle());
}

/// <summary>State a weapon carries beyond its item number (<c>WEAPON_USED</c>).</summary>
public sealed class WeaponState
{
    public Gauge Durability;
    public Gauge Absorption;
    public int Level;
    public readonly short[] Status = new short[2];
    public readonly short[] Attribute = new short[8];
    public uint Special;
    public int FusionPoint;

    public WeaponState Clone()
    {
        var copy = (WeaponState)MemberwiseClone();
        System.Array.Copy(Status, copy.Status, Status.Length);
        System.Array.Copy(Attribute, copy.Attribute, Attribute.Length);
        return copy;
    }

    internal void Write(BinaryWriter w)
    {
        Durability.Write(w);
        Absorption.Write(w);
        w.Write(Level);
        foreach (var s in Status)
            w.Write(s);
        foreach (var a in Attribute)
            w.Write(a);
        w.Write(Special);
        w.Write(FusionPoint);
    }

    internal static WeaponState Read(BinaryReader r)
    {
        var weapon = new WeaponState
        {
            Durability = Gauge.Read(r),
            Absorption = Gauge.Read(r),
            Level = r.ReadInt32(),
        };
        for (int i = 0; i < weapon.Status.Length; i++)
            weapon.Status[i] = r.ReadInt16();
        for (int i = 0; i < weapon.Attribute.Length; i++)
            weapon.Attribute[i] = r.ReadInt16();
        weapon.Special = r.ReadUInt32();
        weapon.FusionPoint = r.ReadInt32();
        return weapon;
    }
}

/// <summary>
/// One owned item: an inventory place, an equipment slot or an active item slot
/// (<c>CGameDataUsed</c>). An empty place has <see cref="ItemNo"/> 0 or less.
/// </summary>
public sealed class OwnedItem
{
    public ItemFamily Family;
    public int ItemNo;
    public ItemDataType ItemType;
    /// <summary>Count held, for the families that stack; the others always count 1.</summary>
    public int Count;
    /// <summary>Name given by the player, or null for the item's own name.</summary>
    public string? CustomName;
    public WeaponState? Weapon;

    public bool IsEmpty => ItemNo <= 0;

    public void Clear()
    {
        Family = ItemFamily.None;
        ItemNo = 0;
        ItemType = ItemDataType.None;
        Count = 0;
        CustomName = null;
        Weapon = null;
    }

    /// <summary>Whether several of the item can share this place (<c>CheckTypeEnableStack</c>).</summary>
    public bool CanStack => Family switch
    {
        ItemFamily.Item => true,
        ItemFamily.Attach => ItemNo != ItemIds.Spectol && ItemNo != ItemIds.MonsterDrop,
        _ => false,
    };

    /// <summary>Count of the item the place holds (<c>GetNum</c>).</summary>
    public int Num
    {
        get
        {
            if (IsEmpty)
                return 0;
            return Family switch
            {
                ItemFamily.Item or ItemFamily.Costume => Count,
                ItemFamily.Attach when ItemType is ItemDataType.Spectol or ItemDataType.MonsterDrop => 1,
                ItemFamily.Attach => Count,
                _ => 1,
            };
        }
    }

    /// <summary>How many more of the item the place can take (<c>CheckStackRemain</c>).</summary>
    public int StackRemain(ItemCatalog catalog)
    {
        if (!CanStack)
            return 0;
        var record = catalog.Get(ItemNo);
        return record != null ? record.StackNum - Num : 0;
    }

    /// <summary>
    /// Puts one of an item into the place: one more on a matching stack, otherwise a new item
    /// that replaces the place's contents (<c>CopyGameData</c>). Returns false for an item the
    /// table does not know.
    /// </summary>
    public bool Put(ItemCatalog catalog, int itemNo)
    {
        var record = catalog.Get(itemNo);
        if (record == null)
            return false;

        if (ItemNo == itemNo && CanStack)
        {
            // Items stop at the stack size, attachments at the owned limit.
            int cap = Family == ItemFamily.Item ? record.StackNum : record.MaxNum;
            if (Count < cap)
                Count++;
            return true;
        }

        Clear();
        Family = record.Family;
        ItemNo = itemNo;
        ItemType = record.Type;
        Count = 1;
        if (Family == ItemFamily.Weapon)
            Weapon = new WeaponState();
        return true;
    }

    /// <summary>
    /// Takes up to <paramref name="count"/> of the item away, emptying the place when none is
    /// left, and gives how many were removed (<c>DeleteNum</c>).
    /// </summary>
    public int Remove(ItemCatalog catalog, int count)
    {
        if (count <= 0 || IsEmpty)
            return 0;
        int before = Num;
        if (Family is ItemFamily.Item or ItemFamily.Attach)
        {
            int max = catalog.Get(ItemNo)?.MaxNum ?? before;
            Count = System.Math.Clamp(Count - count, 0, max);
            int after = Count;
            if (after <= 0)
                Clear();
            return before - after;
        }
        Clear();
        return before;
    }

    public OwnedItem Clone()
    {
        var copy = (OwnedItem)MemberwiseClone();
        copy.Weapon = Weapon?.Clone();
        return copy;
    }

    public void CopyFrom(OwnedItem other)
    {
        Family = other.Family;
        ItemNo = other.ItemNo;
        ItemType = other.ItemType;
        Count = other.Count;
        CustomName = other.CustomName;
        Weapon = other.Weapon?.Clone();
    }

    internal void Write(BinaryWriter w)
    {
        w.Write((byte)Family);
        w.Write((short)ItemNo);
        w.Write((byte)ItemType);
        w.Write(Count);
        w.Write(CustomName != null);
        if (CustomName != null)
            w.Write(CustomName);
        w.Write(Weapon != null);
        Weapon?.Write(w);
    }

    internal void Read(BinaryReader r)
    {
        Family = (ItemFamily)r.ReadByte();
        ItemNo = r.ReadInt16();
        ItemType = (ItemDataType)r.ReadByte();
        Count = r.ReadInt32();
        CustomName = r.ReadBoolean() ? r.ReadString() : null;
        Weapon = r.ReadBoolean() ? WeaponState.Read(r) : null;
    }
}
