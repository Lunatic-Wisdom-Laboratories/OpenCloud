using System.Collections.Generic;
using Godot;

namespace OpenCloud.Gameplay;

/// <summary>
/// Sends a player's state to the other peers once per game frame (30 Hz) and, on those peers,
/// draws the player a little in the past, interpolating between the states received.
///
/// The state goes through the scene tree's <see cref="MultiplayerApi"/> with unreliable
/// ordered RPCs on <see cref="Channel"/>, so it works with whatever peer the network layer
/// sets up (ENet for co-op) and never queues behind the reliable save traffic. With no
/// network peer nothing is sent.
/// </summary>
public partial class PlayerSync : Node
{
    public const string NodeName = "Sync";

    /// <summary>Transfer channel of the position traffic; channel 1 carries the reliable save data.</summary>
    public const int Channel = 2;

    /// <summary>How far behind the newest state a remote player is drawn, in game frames.</summary>
    [Export] public float InterpolationDelay { get; set; } = 3.0f;

    private readonly record struct Snapshot(uint Frame, Vector3 Position, float Yaw, string Motion, float Step);

    private readonly List<Snapshot> _snapshots = new();
    private PlayerCharacter _player = null!;
    private uint _lastFrame;
    private double _clock;
    private bool _clockSet;

    public override void _Ready()
    {
        _player = GetParent<PlayerCharacter>();
        _player.GameFrameRan += Send;
    }

    public override void _ExitTree()
    {
        if (_player != null)
            _player.GameFrameRan -= Send;
    }

    private bool Online =>
        Multiplayer.HasMultiplayerPeer() &&
        Multiplayer.MultiplayerPeer is not OfflineMultiplayerPeer &&
        Multiplayer.MultiplayerPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    private void Send(PlayerCharacter player)
    {
        if (!Online)
            return;
        Rpc(MethodName.ReceiveState, player.GameFrameCount, player.GlobalPosition, player.Yaw,
            player.Motion.Current ?? string.Empty, player.Motion.Step);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered, TransferChannel = Channel)]
    private void ReceiveState(uint frame, Vector3 position, float yaw, string motion, float step)
    {
        // Ordered delivery drops stale packets, but a restarted owner counts from zero again.
        if (frame <= _lastFrame && _lastFrame - frame < 300)
            return;
        _lastFrame = frame;
        _snapshots.Add(new Snapshot(frame, position, yaw, motion, step));
        if (_snapshots.Count > 32)
            _snapshots.RemoveAt(0);
        if (!_clockSet || frame < _clock)
        {
            _clock = frame - InterpolationDelay;
            _clockSet = true;
        }
    }

    public override void _Process(double delta)
    {
        if (_player.IsLocallyControlled || _snapshots.Count == 0)
            return;

        _clock += delta * PlayerCharacter.GameFrameRate;
        var newest = _snapshots[^1];
        // Drift back towards the wanted delay when packets come early or late.
        double wanted = newest.Frame - InterpolationDelay;
        _clock += (wanted - _clock) * 0.05;

        int index = _snapshots.FindLastIndex(s => s.Frame <= _clock);
        if (index < 0)
        {
            Apply(_snapshots[0], _snapshots[0], 0.0f);
            return;
        }
        if (index >= _snapshots.Count - 1)
        {
            Apply(newest, newest, 0.0f);
            return;
        }
        var from = _snapshots[index];
        var to = _snapshots[index + 1];
        float weight = (float)((_clock - from.Frame) / (to.Frame - from.Frame));
        Apply(from, to, Mathf.Clamp(weight, 0.0f, 1.0f));
        if (index > 0)
            _snapshots.RemoveRange(0, index);
    }

    private void Apply(Snapshot from, Snapshot to, float weight)
    {
        var position = from.Position.Lerp(to.Position, weight);
        float yaw = Mathf.LerpAngle(from.Yaw, to.Yaw, weight);
        var state = weight < 0.5f ? from : to;
        _player.ApplyRemoteState(position, yaw, state.Motion, state.Step);
    }
}
