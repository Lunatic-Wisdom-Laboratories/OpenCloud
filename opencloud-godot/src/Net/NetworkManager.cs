using System;
using Godot;
using OpenCloud.Game.Save;

namespace OpenCloud.Net;

/// <summary>
/// Runs a co-op session over ENet. The host owns the shared <see cref="GlobalSaveState"/>
/// (inventory, money, flags...): peers submit changes through <see cref="Submit"/>, the host
/// checks and applies them and replays them to everyone in order (<see cref="SaveSync"/>).
/// Every <see cref="HashInterval"/> seconds the host sends its state digest; a peer whose
/// digest differs asks for a full snapshot.
/// <para>
/// Players are admitted with a handshake that checks the protocol version and the item table
/// fingerprint, then appear in <see cref="Roster"/> on every peer. The first player controls
/// Max and the second Monica. The manager sets its peer on its own <see cref="Node.Multiplayer"/>
/// API, the scene tree's unless a branch was given its own, so gameplay nodes can send their own
/// RPCs over the same connection (on channels other than <see cref="SaveChannel"/>).
/// </para>
/// </summary>
public partial class NetworkManager : Node
{
    public const int ProtocolVersion = 1;
    public const int DefaultPort = 7777;
    /// <summary>ENet channel the save traffic uses.</summary>
    public const int SaveChannel = 1;

    [Signal] public delegate void PlayerJoinedEventHandler(long peerId, int slot, int character);
    [Signal] public delegate void PlayerLeftEventHandler(long peerId);
    /// <summary>The session is up: hosting, accepted by a host, or offline.</summary>
    [Signal] public delegate void SessionStartedEventHandler();
    [Signal] public delegate void SessionEndedEventHandler(string reason);
    /// <summary>This peer's state differed from the host's and a snapshot was requested.</summary>
    [Signal] public delegate void DesyncDetectedEventHandler(long hostRevision);
    /// <summary>A snapshot from the host replaced this peer's state.</summary>
    [Signal] public delegate void SnapshotAppliedEventHandler(long revision);

    [Export] public int MaxPlayers { get; set; } = 2;
    [Export] public double HashInterval { get; set; } = 5.0;
    [Export] public string PlayerName { get; set; } = "Player";

    private double _hashTimer;
    private SaveSync? _sync;

    public PlayerRoster Roster { get; } = new();
    public SaveSync Sync => _sync ?? throw new InvalidOperationException("NetworkManager has no save state; call Initialize first");
    public GlobalSaveState State => Sync.State;

    public bool IsOnline => Multiplayer.MultiplayerPeer is ENetMultiplayerPeer;
    public bool IsHost => !IsOnline || Multiplayer.IsServer();
    public bool IsInSession { get; private set; }
    public long LocalPeerId => IsOnline ? Multiplayer.GetUniqueId() : 1;
    public PlayerLocalState? LocalPlayer => Roster.Get(LocalPeerId);

    /// <summary>Attaches the shared state. Call once before hosting, joining or playing offline.</summary>
    public void Initialize(GlobalSaveState state)
    {
        _sync = new SaveSync(state)
        {
            SendRequest = op => RpcId(1, MethodName.RequestOperation, (int)op.Kind, op.A, op.B, op.C),
            Publish = (revision, op) =>
            {
                if (IsOnline)
                    Rpc(MethodName.ReceiveOperation, revision, (int)op.Kind, op.A, op.B, op.C);
            },
            SendSnapshotRequest = () => RpcId(1, MethodName.RequestSnapshot),
        };
        _sync.DesyncDetected += revision => EmitSignal(SignalName.DesyncDetected, revision);
    }

    public override void _Ready()
    {
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ConnectionFailed += () => EndSession("connection failed");
        Multiplayer.ServerDisconnected += () => EndSession("host closed the session");
    }

    public override void _Process(double delta)
    {
        if (!IsInSession || !IsOnline || !Multiplayer.IsServer())
            return;
        _hashTimer += delta;
        if (_hashTimer < HashInterval)
            return;
        _hashTimer = 0;
        if (Roster.Count > 1)
            Rpc(MethodName.ReceiveHash, Sync.Revision, unchecked((long)Sync.Hash()));
    }

    /// <summary>Asks for a change to the shared state. Gives the result on the host, null on a peer.</summary>
    public int? Submit(SaveOperation op) => Sync.Submit(op);

    /// <summary>A single-player game: this machine is the authority and the only player.</summary>
    public void StartOffline()
    {
        Leave();
        Sync.IsAuthority = true;
        Sync.ResetRevision();
        AddLocalPlayer(1, 0);
    }

    public Error Host(int port = DefaultPort)
    {
        Leave();
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateServer(port, Math.Max(1, MaxPlayers - 1));
        if (error != Error.Ok)
            return error;
        Multiplayer.MultiplayerPeer = peer;
        Sync.IsAuthority = true;
        Sync.ResetRevision();
        _hashTimer = 0;
        AddLocalPlayer(1, 0);
        return Error.Ok;
    }

    /// <summary>Connects to a host. The session starts once the host accepts this player.</summary>
    public Error Join(string address, int port = DefaultPort)
    {
        Leave();
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateClient(address, port);
        if (error != Error.Ok)
            return error;
        Multiplayer.MultiplayerPeer = peer;
        Sync.IsAuthority = false;
        Sync.ResetRevision();
        return Error.Ok;
    }

    public void Leave()
    {
        if (IsInSession || IsOnline)
            EndSession("left");
    }

    private void AddLocalPlayer(long peerId, int slot)
    {
        AddPlayer(new PlayerLocalState(peerId, slot, PlayerName));
        IsInSession = true;
        EmitSignal(SignalName.SessionStarted);
    }

    private void AddPlayer(PlayerLocalState player)
    {
        Roster.Add(player);
        EmitSignal(SignalName.PlayerJoined, player.PeerId, player.Slot, (int)player.Character);
    }

    private void EndSession(string reason)
    {
        if (Multiplayer.MultiplayerPeer is ENetMultiplayerPeer peer)
            peer.Close();
        Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
        foreach (var player in Roster)
            EmitSignal(SignalName.PlayerLeft, player.PeerId);
        Roster.Clear();
        IsInSession = false;
        if (_sync != null)
            _sync.IsAuthority = true;
        EmitSignal(SignalName.SessionEnded, reason);
    }

    private void OnConnectedToServer()
    {
        RpcId(1, MethodName.RequestJoin, PlayerName, ProtocolVersion, unchecked((long)State.Catalog.Fingerprint));
    }

    private void OnPeerDisconnected(long peerId)
    {
        if (!Multiplayer.IsServer() || !Roster.Remove(peerId))
            return;
        EmitSignal(SignalName.PlayerLeft, peerId);
        Rpc(MethodName.RemovePlayer, peerId);
    }

    // ---- handshake -------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = SaveChannel)]
    private void RequestJoin(string name, int protocol, long catalogFingerprint)
    {
        if (!Multiplayer.IsServer())
            return;
        long sender = Multiplayer.GetRemoteSenderId();
        string? refusal = null;
        if (protocol != ProtocolVersion)
            refusal = $"protocol {protocol}, host runs {ProtocolVersion}";
        else if (unchecked((ulong)catalogFingerprint) != State.Catalog.Fingerprint)
            refusal = "the item table differs from the host's; both players need the same game data";
        else if (Roster.Count >= MaxPlayers)
            refusal = "the session is full";
        else if (Roster.Contains(sender))
            refusal = "already joined";

        if (refusal != null)
        {
            RpcId(sender, MethodName.JoinRefused, refusal);
            (Multiplayer.MultiplayerPeer as ENetMultiplayerPeer)?.GetPeer((int)sender)?.PeerDisconnectLater();
            return;
        }

        var player = new PlayerLocalState(sender, Roster.FreeSlot(), SanitizeName(name));

        // The newcomer learns who is here, then the state, then itself; channel order keeps that order.
        foreach (var other in Roster)
            RpcId(sender, MethodName.AddRemotePlayer, other.PeerId, other.Slot, (int)other.Character, other.Name);
        var (revision, snapshot) = Sync.TakeSnapshot();
        RpcId(sender, MethodName.JoinAccepted, player.Slot, (int)player.Character, revision, snapshot);

        foreach (var other in Roster)
        {
            if (other.PeerId != 1)
                RpcId(other.PeerId, MethodName.AddRemotePlayer, player.PeerId, player.Slot, (int)player.Character, player.Name);
        }
        AddPlayer(player);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = SaveChannel)]
    private void JoinAccepted(int slot, int character, long revision, byte[] snapshot)
    {
        Sync.HandleSnapshot(revision, snapshot);
        var player = new PlayerLocalState(LocalPeerId, slot, PlayerName) { Character = (PlayableCharacter)character };
        AddPlayer(player);
        IsInSession = true;
        EmitSignal(SignalName.SessionStarted);
        EmitSignal(SignalName.SnapshotApplied, revision);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = SaveChannel)]
    private void JoinRefused(string reason)
    {
        GD.PrintErr($"join refused: {reason}");
        CallDeferred(MethodName.EndSession, $"refused: {reason}");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = SaveChannel)]
    private void AddRemotePlayer(long peerId, int slot, int character, string name)
    {
        if (peerId == LocalPeerId)
            return;
        AddPlayer(new PlayerLocalState(peerId, slot, name) { Character = (PlayableCharacter)character });
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = SaveChannel)]
    private void RemovePlayer(long peerId)
    {
        if (Roster.Remove(peerId))
            EmitSignal(SignalName.PlayerLeft, peerId);
    }

    // ---- save state ------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = SaveChannel)]
    private void RequestOperation(int kind, int a, int b, int c)
    {
        long sender = Multiplayer.GetRemoteSenderId();
        if (!Multiplayer.IsServer() || !Roster.Contains(sender))
            return;
        if (!Sync.HandleRequest(sender, new SaveOperation((SaveOpKind)kind, a, b, c)))
            GD.PrintErr($"refused save operation {(SaveOpKind)kind}({a}, {b}, {c}) from peer {sender}");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = SaveChannel)]
    private void ReceiveOperation(long revision, int kind, int a, int b, int c)
    {
        Sync.HandlePublished(revision, new SaveOperation((SaveOpKind)kind, a, b, c));
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = SaveChannel)]
    private void ReceiveHash(long revision, long hash)
    {
        Sync.HandleHash(revision, unchecked((ulong)hash));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = SaveChannel)]
    private void RequestSnapshot()
    {
        long sender = Multiplayer.GetRemoteSenderId();
        if (!Multiplayer.IsServer() || !Roster.Contains(sender))
            return;
        var (revision, snapshot) = Sync.TakeSnapshot();
        RpcId(sender, MethodName.ReceiveSnapshot, revision, snapshot);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = SaveChannel)]
    private void ReceiveSnapshot(long revision, byte[] snapshot)
    {
        Sync.HandleSnapshot(revision, snapshot);
        EmitSignal(SignalName.SnapshotApplied, revision);
    }

    private static string SanitizeName(string name)
    {
        name = name.Trim();
        if (name.Length > 24)
            name = name[..24];
        return name.Length > 0 ? name : "Player";
    }
}
