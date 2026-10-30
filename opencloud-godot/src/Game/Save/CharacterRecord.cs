using System.IO;

namespace OpenCloud.Game.Save;

/// <summary>Characters the user data keeps a status for (<c>USER_CHARA</c>).</summary>
public enum PlayableCharacter
{
    None = -1,
    Max = 0,
    Monica = 1,
    Ridepod = 2,
    Monster = 3,
}

/// <summary>Equipment slots of <see cref="CharacterRecord.Equipment"/> (<c>CHARA_EQUIP_SLOT</c>).</summary>
public enum EquipSlot
{
    /// <summary>Max's wrench, hammer or fishing rod; Monica's sword.</summary>
    Melee = 0,
    /// <summary>Max's gun; Monica's armband.</summary>
    Sub = 1,
    Head = 2,
    Feet = 3,
    Clothes = 4,
}

/// <summary>
/// Status of Max or Monica: health, conditions, active items and equipment (<c>CHARA_DATA</c>).
/// The record belongs to the shared world, not to a player: a player controls a character
/// through <see cref="PlayerLocalState.Character"/>.
/// </summary>
public sealed class CharacterRecord
{
    public const int ActiveItemCount = 3;
    public const int EquipCount = 5;

    public Gauge Hp;
    /// <summary>Conditions, <c>CHARA_STATUS_ATTR</c> bits.</summary>
    public ushort StatusAttr;
    public ushort Defence;
    /// <summary>Time left of the power, slow, stop and stone conditions.</summary>
    public readonly short[] StatusTime = new short[4];
    public readonly OwnedItem[] ActiveItems = NewItems(ActiveItemCount);
    public readonly OwnedItem[] Equipment = NewItems(EquipCount);

    public OwnedItem this[EquipSlot slot] => Equipment[(int)slot];

    public void Clear()
    {
        Hp = default;
        StatusAttr = 0;
        Defence = 0;
        System.Array.Clear(StatusTime);
        foreach (var item in ActiveItems)
            item.Clear();
        foreach (var item in Equipment)
            item.Clear();
    }

    public void CopyFrom(CharacterRecord other)
    {
        Hp = other.Hp;
        StatusAttr = other.StatusAttr;
        Defence = other.Defence;
        System.Array.Copy(other.StatusTime, StatusTime, StatusTime.Length);
        for (int i = 0; i < ActiveItems.Length; i++)
            ActiveItems[i].CopyFrom(other.ActiveItems[i]);
        for (int i = 0; i < Equipment.Length; i++)
            Equipment[i].CopyFrom(other.Equipment[i]);
    }

    internal void Write(BinaryWriter w)
    {
        Hp.Write(w);
        w.Write(StatusAttr);
        w.Write(Defence);
        foreach (var t in StatusTime)
            w.Write(t);
        foreach (var item in ActiveItems)
            item.Write(w);
        foreach (var item in Equipment)
            item.Write(w);
    }

    internal void Read(BinaryReader r)
    {
        Hp = Gauge.Read(r);
        StatusAttr = r.ReadUInt16();
        Defence = r.ReadUInt16();
        for (int i = 0; i < StatusTime.Length; i++)
            StatusTime[i] = r.ReadInt16();
        foreach (var item in ActiveItems)
            item.Read(r);
        foreach (var item in Equipment)
            item.Read(r);
    }

    private static OwnedItem[] NewItems(int count)
    {
        var items = new OwnedItem[count];
        for (int i = 0; i < count; i++)
            items[i] = new OwnedItem();
        return items;
    }
}
