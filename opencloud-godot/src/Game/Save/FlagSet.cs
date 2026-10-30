using System;
using System.IO;

namespace OpenCloud.Game.Save;

/// <summary>
/// A fixed number of one-bit flags packed 32 to a word, as the story flags
/// (<c>CSaveData::bit_flag</c>) and each map's flags (<c>CMapFlagData</c>) store them.
/// Numbers out of range read as clear and are never written.
/// </summary>
public sealed class FlagSet
{
    private readonly uint[] _words;

    public FlagSet(int count)
    {
        Count = count;
        _words = new uint[(count + 31) / 32];
    }

    public int Count { get; }

    public bool InRange(int index) => index >= 0 && index < Count;

    public bool this[int index]
    {
        get => InRange(index) && (_words[index / 32] & (1u << (index % 32))) != 0;
        set => Set(index, value);
    }

    /// <summary>Sets or clears a flag and gives whether it was set before.</summary>
    public bool Set(int index, bool on)
    {
        if (!InRange(index))
            return false;
        uint mask = 1u << (index % 32);
        ref uint word = ref _words[index / 32];
        bool was = (word & mask) != 0;
        word = on ? word | mask : word & ~mask;
        return was;
    }

    public void Clear() => Array.Clear(_words);

    public void CopyFrom(FlagSet other)
    {
        if (other.Count != Count)
            throw new ArgumentException("flag sets differ in size", nameof(other));
        Array.Copy(other._words, _words, _words.Length);
    }

    internal void Write(BinaryWriter w)
    {
        foreach (var word in _words)
            w.Write(word);
    }

    internal void Read(BinaryReader r)
    {
        for (int i = 0; i < _words.Length; i++)
            _words[i] = r.ReadUInt32();
    }
}
