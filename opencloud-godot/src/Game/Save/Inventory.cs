using System;
using System.Collections.Generic;
using System.IO;
using OpenCloud.Game.Items;

namespace OpenCloud.Game.Save;

/// <summary>
/// The party's shared item board (<c>CUserDataManager::used_data</c>): 150 places, the board
/// proper followed by overflow places that hold what did not fit. The board has 138 places, or
/// 144 once story flag <see cref="SaveBitFlag.ItemBoardExpanded"/> is set, which leaves 12 or 6
/// overflow places (<c>GetItemBoardMaxNum</c>, <c>GetItemBoardOverNum</c>).
/// </summary>
public sealed class Inventory
{
    public const int SlotCount = 150;
    public const int BoardSize = 0x8A;
    public const int ExpandedBoardSize = 0x90;

    private readonly OwnedItem[] _slots = new OwnedItem[SlotCount];

    public Inventory()
    {
        for (int i = 0; i < SlotCount; i++)
            _slots[i] = new OwnedItem();
    }

    /// <summary>Whether the board has its larger size; the owner keeps it in step with the story flag.</summary>
    public bool Expanded { get; set; }

    public int MainSize => Expanded ? ExpandedBoardSize : BoardSize;
    public int OverflowSize => SlotCount - MainSize;

    public IReadOnlyList<OwnedItem> Slots => _slots;
    public OwnedItem this[int index] => _slots[index];

    public void Clear()
    {
        foreach (var slot in _slots)
            slot.Clear();
    }

    /// <summary>First empty place of the board proper, or -1 (<c>SearchSpaceUsedData</c>).</summary>
    public int FindEmpty()
    {
        for (int i = 0; i < MainSize; i++)
        {
            if (_slots[i].IsEmpty)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Gives a count of an item to the party: as many as fit on the board, the rest in overflow
    /// places (<c>CUserDataManager::GetItem</c>). <paramref name="others"/> are the places outside
    /// the board that count towards the owned limit, such as the characters' equipment. Returns
    /// how many went onto the board proper.
    /// </summary>
    public int Add(ItemCatalog catalog, int itemNo, int count, IEnumerable<OwnedItem> others)
    {
        var record = catalog.Get(itemNo);
        int fit = AddToBoard(catalog, record, itemNo, count, others);
        int over = count - fit;

        // Unique items never spill over, and report one received (retail returns 1 here).
        if (record != null && (record.Attribute & ItemAttribute.Unique) != 0 && record.MaxNum <= CountOf(itemNo, others))
            return 1;

        if (over > 0)
            AddToOverflow(catalog, itemNo, over);
        return fit;
    }

    /// <summary>Board-only part of <see cref="Add"/> (<c>GetItemNotOver</c>), without its story side effects.</summary>
    private int AddToBoard(ItemCatalog catalog, ItemRecord? record, int itemNo, int count, IEnumerable<OwnedItem> others)
    {
        if (record == null)
            return 0;
        int owned = CountOf(itemNo, others);
        if (record.MaxNum <= owned)
            return 0;

        int added = 0;
        for (int n = 0; n < count; n++)
        {
            int target = -1;
            for (int i = 0; i < MainSize; i++)
            {
                var slot = _slots[i];
                if (slot.ItemNo == itemNo && slot.CanStack && slot.StackRemain(catalog) > 0)
                {
                    target = i;
                    break;
                }
            }
            if (target < 0)
                target = FindEmpty();
            if (target < 0 || !_slots[target].Put(catalog, itemNo))
                continue;

            added++;
            if (++owned >= record.MaxNum)
                break;
        }
        return added;
    }

    /// <summary>Puts a count of an item into the overflow places (<c>GetOverItem</c>).</summary>
    private void AddToOverflow(ItemCatalog catalog, int itemNo, int count)
    {
        if (itemNo <= 0 || count <= 0 || catalog.Get(itemNo) == null)
            return;

        for (int placed = 0; placed < count; placed++)
        {
            OwnedItem? target = null;
            for (int i = MainSize; i < SlotCount && target == null; i++)
            {
                var slot = _slots[i];
                if (slot.ItemNo == itemNo && slot.CanStack && slot.StackRemain(catalog) > 0)
                    target = slot;
                else if (slot.IsEmpty)
                    target = slot;
            }
            if (target == null)
                return;
            target.Put(catalog, itemNo);
        }
    }

    /// <summary>
    /// Takes a count of an item away, from the last inventory place backwards and then from
    /// <paramref name="others"/> (the characters' active items), and gives how many were removed
    /// (<c>CUserDataManager::DeleteItem</c>).
    /// </summary>
    public int Remove(ItemCatalog catalog, int itemNo, int count, IEnumerable<OwnedItem> others)
    {
        if (itemNo <= 0 || count <= 0)
            return 0;
        int left = count;
        for (int i = SlotCount - 1; i >= 0 && left > 0; i--)
        {
            if (_slots[i].ItemNo == itemNo)
                left -= _slots[i].Remove(catalog, left);
        }
        foreach (var slot in others)
        {
            if (left <= 0)
                break;
            if (slot.ItemNo == itemNo)
                left -= slot.Remove(catalog, left);
        }
        return count - left;
    }

    /// <summary>How many of an item the party owns, on the board and in <paramref name="others"/> (<c>GetNumSameItem</c>).</summary>
    public int CountOf(int itemNo, IEnumerable<OwnedItem> others)
    {
        int total = 0;
        foreach (var slot in _slots)
        {
            if (slot.ItemNo == itemNo)
                total += slot.Num;
        }
        foreach (var slot in others)
        {
            if (slot.ItemNo == itemNo)
                total += slot.Num;
        }
        return total;
    }

    /// <summary>Swaps the contents of two places, as the item menu moves items.</summary>
    public bool Swap(int a, int b)
    {
        if ((uint)a >= SlotCount || (uint)b >= SlotCount)
            return false;
        (_slots[a], _slots[b]) = (_slots[b], _slots[a]);
        return true;
    }

    public void CopyFrom(Inventory other)
    {
        Expanded = other.Expanded;
        for (int i = 0; i < SlotCount; i++)
            _slots[i].CopyFrom(other._slots[i]);
    }

    internal void Write(BinaryWriter w)
    {
        w.Write(Expanded);
        foreach (var slot in _slots)
            slot.Write(w);
    }

    internal void Read(BinaryReader r)
    {
        Expanded = r.ReadBoolean();
        foreach (var slot in _slots)
            slot.Read(r);
    }
}
