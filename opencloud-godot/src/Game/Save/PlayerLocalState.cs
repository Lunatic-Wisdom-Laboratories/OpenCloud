using System.Collections;
using System.Collections.Generic;
using Godot;

namespace OpenCloud.Game.Save;

/// <summary>Sizes of the dungeon mini-map (<c>SV_CONFIG_MAP</c>).</summary>
public enum MiniMapSize
{
    Off = 0,
    Small = 1,
    Large = 2,
}

/// <summary>
/// One player's game options (<c>SV_CONFIG_OPTION</c>). Each player keeps their own: they
/// change how that player's screen, pad and camera behave and are never synchronised.
/// </summary>
public sealed class PlayerOptions
{
    public bool RememberCursor = true;
    public bool Vibration = true;
    public bool InstantMessages;
    public bool MonoSound;
    /// <summary>Time of day passes one and a half times as fast.</summary>
    public bool FastTime;
    public MiniMapSize MiniMap = MiniMapSize.Small;
    public int EnemyHpDisplay;
    public bool HideDamage;
    public int MonsterNameDisplay;
    public int AngerCounterDisplay;
    public bool DepthOfFieldOff;
    public bool CaptionsOff;
    /// <summary>The first-person camera's vertical axis is inverted, as it is by default.</summary>
    public bool InvertFirstPersonY = true;
    public bool NormalCameraRotation;
}

/// <summary>
/// What belongs to one player rather than to the shared world: who they are on the network,
/// which character they control, where they are and their own options. A session holds one per
/// connected player in a <see cref="PlayerRoster"/>; nothing assumes a single player.
/// </summary>
public sealed class PlayerLocalState
{
    public PlayerLocalState(long peerId, int slot, string name)
    {
        PeerId = peerId;
        Slot = slot;
        Name = name;
        Character = CharacterForSlot(slot);
    }

    /// <summary>Network peer id; 1 is the host, and the only player of an offline game.</summary>
    public long PeerId { get; }
    /// <summary>Order of joining, from 0; picks the default character.</summary>
    public int Slot { get; }
    public string Name { get; set; }
    public PlayableCharacter Character { get; set; }

    /// <summary>Main map the player is on, or -1.</summary>
    public int MapNo { get; set; } = -1;
    /// <summary>Interior the player is in, or -1.</summary>
    public int SubMapNo { get; set; } = -1;
    public int PrevMapNo { get; set; } = -1;
    public int PrevSubMapNo { get; set; } = -1;
    public int AreaNo { get; set; }
    public Vector3 Position { get; set; }
    public float Yaw { get; set; }

    public PlayerOptions Options { get; } = new();

    /// <summary>Max for the first player, Monica for the second, no character for later ones.</summary>
    public static PlayableCharacter CharacterForSlot(int slot) => slot switch
    {
        0 => PlayableCharacter.Max,
        1 => PlayableCharacter.Monica,
        _ => PlayableCharacter.None,
    };
}

/// <summary>The players of a session, by peer id.</summary>
public sealed class PlayerRoster : IEnumerable<PlayerLocalState>
{
    private readonly SortedDictionary<long, PlayerLocalState> _players = new();

    public int Count => _players.Count;

    public PlayerLocalState? Get(long peerId) => _players.GetValueOrDefault(peerId);

    public bool Contains(long peerId) => _players.ContainsKey(peerId);

    public void Add(PlayerLocalState player) => _players[player.PeerId] = player;

    public bool Remove(long peerId) => _players.Remove(peerId);

    public void Clear() => _players.Clear();

    /// <summary>Lowest slot no player holds.</summary>
    public int FreeSlot()
    {
        var used = new HashSet<int>();
        foreach (var player in _players.Values)
            used.Add(player.Slot);
        int slot = 0;
        while (used.Contains(slot))
            slot++;
        return slot;
    }

    /// <summary>The player controlling a character, or null.</summary>
    public PlayerLocalState? ControllerOf(PlayableCharacter character)
    {
        foreach (var player in _players.Values)
        {
            if (player.Character == character)
                return player;
        }
        return null;
    }

    public IEnumerator<PlayerLocalState> GetEnumerator() => _players.Values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
