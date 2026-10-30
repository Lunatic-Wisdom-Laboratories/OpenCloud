using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using OpenCloud.Assets.Maps;
using OpenCloud.Assets.Models;
using OpenCloud.Core;
using OpenCloud.Core.IO;
using OpenCloud.Gameplay;
using OpenCloud.Rendering;

namespace OpenCloud.Scenes;

/// <summary>
/// Walking in a town, the engine's counterpart of the town loop in walk mode (<c>EditInit</c>,
/// <c>EditLoop</c>): loads the map, puts the player at the map's <c>CHARA_POS</c>, and runs the
/// player controller and the trailing town camera.
///
/// Players are spawned per network peer with <see cref="SpawnPlayer"/>. Offline the only peer
/// is 1 and the local player is spawned on load; in co-op the network layer calls
/// <see cref="SpawnPlayer"/> and <see cref="RemovePlayer"/> as players join and leave, and each
/// player's <see cref="PlayerSync"/> keeps the others' positions at 30 Hz.
///
/// Run with <c>-- --data=&lt;path&gt; --map=&lt;name&gt;</c>. <c>--selftest=&lt;frames&gt;</c> walks the
/// player forward headless for that many game frames, prints where it ended and quits.
///
/// Until the co-op session layer drives it, <c>--net-host=&lt;port&gt;</c> and
/// <c>--net-join=&lt;address&gt;:&lt;port&gt;</c> open a bare two-player ENet session for testing
/// the position sync, and <c>--net-report=&lt;seconds&gt;</c> prints every player's position
/// after that long and quits.
/// </summary>
public partial class TownScene : Node3D
{
    [Export] public string[] DataPaths { get; set; } = Array.Empty<string>();

    /// <summary>Map to load (name like <c>t01</c> or a <c>.map</c> path); empty loads the first map.</summary>
    [Export] public string InitialMap { get; set; } = string.Empty;

    /// <summary>Game clock of the map, in hours.</summary>
    [Export] public float Clock { get; set; } = 12.0f;

    private readonly List<string> _errors = new();
    private readonly List<string> _notes = new();
    private VirtualFileSystem? _vfs;
    private ModelLoader? _models;
    private LoadedMap? _map;
    private string _status = string.Empty;
    private bool _showHelp = true;
    private int _selfTestFrames;
    private string? _hostPort;
    private string? _joinAddress;
    private double _reportAfter;

    private Node3D _mapRoot = null!;
    private Node3D _players = null!;
    private TownCamera _camera = null!;
    private MapLightingRig _lighting = null!;
    private WorldEnvironment _environment = null!;
    private DirectionalLight3D _fallbackLight = null!;
    private Label _hud = null!;

    /// <summary>The player this peer controls, once spawned.</summary>
    public PlayerCharacter? LocalPlayer { get; private set; }

    public override void _Ready()
    {
        _mapRoot = GetNode<Node3D>("MapRoot");
        _players = GetNode<Node3D>("Players");
        _camera = GetNode<TownCamera>("TownCamera");
        _lighting = GetNode<MapLightingRig>("MapLighting");
        _environment = GetNode<WorldEnvironment>("WorldEnvironment");
        _fallbackLight = GetNode<DirectionalLight3D>("FallbackLight");
        _hud = GetNode<Label>("Hud/Info");
        // The camera follows the drawn model, so it moves after the players have been drawn.
        ProcessPriority = 10;
        PadInput.EnsureActions();

        string mapName = InitialMap;
        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith("--map=", StringComparison.Ordinal))
                mapName = argument["--map=".Length..];
            else if (argument.StartsWith("--selftest=", StringComparison.Ordinal))
                int.TryParse(argument["--selftest=".Length..], out _selfTestFrames);
            else if (argument.StartsWith("--net-host=", StringComparison.Ordinal))
                _hostPort = argument["--net-host=".Length..];
            else if (argument.StartsWith("--net-join=", StringComparison.Ordinal))
                _joinAddress = argument["--net-join=".Length..];
            else if (argument.StartsWith("--net-report=", StringComparison.Ordinal))
                double.TryParse(argument["--net-report=".Length..], System.Globalization.CultureInfo.InvariantCulture, out _reportAfter);
        }

        mapName = ViewerSwitch.TakeMap(mapName);

        var paths = GameDataPaths.Resolve(DataPaths);
        _vfs = GameDataPaths.MountAll(paths, _errors);
        _models = new ModelLoader(_vfs);
        if (_vfs.Sources.Count == 0)
        {
            _status = "No game data mounted. Run with -- --data=<path to the disc .iso, DATA.HD4 or an extracted folder>.";
            Finish();
            return;
        }

        LoadMap(mapName);
        if (_map != null)
        {
            if (_joinAddress != null)
                JoinTestSession(_joinAddress);
            else
            {
                SpawnPlayer(Multiplayer.GetUniqueId(), 0);
                if (_hostPort != null)
                    HostTestSession(_hostPort);
            }
        }
        Finish();
    }

    /// <summary>A bare ENet host for two players; joining peers play the second slot.</summary>
    private void HostTestSession(string port)
    {
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateServer(int.Parse(port), 1);
        if (error != Error.Ok)
        {
            _status = $"Could not host on port {port}: {error}";
            return;
        }
        Multiplayer.MultiplayerPeer = peer;
        Multiplayer.PeerConnected += id => SpawnPlayer(id, 1);
        Multiplayer.PeerDisconnected += RemovePlayer;
        _status += $"  [hosting on {port}]";
    }

    /// <summary>Joins a test host: the host's player is slot 0, this peer's slot 1.</summary>
    private void JoinTestSession(string address)
    {
        int colon = address.LastIndexOf(':');
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateClient(address[..colon], int.Parse(address[(colon + 1)..]));
        if (error != Error.Ok)
        {
            _status = $"Could not join {address}: {error}";
            return;
        }
        Multiplayer.MultiplayerPeer = peer;
        Multiplayer.ConnectedToServer += () =>
        {
            SpawnPlayer(1, 0);
            SpawnPlayer(Multiplayer.GetUniqueId(), 1);
        };
        Multiplayer.ServerDisconnected += () =>
        {
            // Back offline: the local player keeps walking as the only peer.
            RemovePlayer(1);
            Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
            LocalPlayer?.SetMultiplayerAuthority(Multiplayer.GetUniqueId());
        };
        _status += $"  [joining {address}]";
    }

    private void Finish()
    {
        UpdateHud();
        if (_selfTestFrames > 0 && LocalPlayer == null && _joinAddress == null)
        {
            GD.PrintErr($"town selftest: {_status}");
            GetTree().Quit(1);
        }
    }

    private void LoadMap(string name)
    {
        var maps = MapLoader.EnumerateMaps(_vfs!).ToList();
        string? path = string.IsNullOrEmpty(name)
            ? maps.FirstOrDefault()
            : maps.FirstOrDefault(m => VfsPath.Comparer.Equals(m, MapLoader.MapPath(name)));
        if (path == null)
        {
            _status = string.IsNullOrEmpty(name) ? "The mounted data has no maps." : $"Map '{name}' not found.";
            return;
        }

        try
        {
            var loader = new MapLoader(_vfs!, _models!);
            _map = loader.Load(path, Clock);
            _mapRoot.AddChild(_map.Root);
            // Debug geometry stays hidden while walking.
            foreach (var group in new[] { MapLoader.CollisionGroup, MapLoader.FuncPointGroup })
            {
                foreach (var node in GetTree().GetNodesInGroup(group))
                {
                    if (node is Node3D spatial)
                        spatial.Visible = false;
                }
            }
            ApplyLighting();
            _status = path;
        }
        catch (Exception exception)
        {
            _status = $"Failed to load {path}: {exception.Message}";
            GD.PushError($"{path}: {exception}");
        }
    }

    private void ApplyLighting()
    {
        var environment = (Godot.Environment)_environment.Environment.Duplicate();
        var set = _map?.Lighting;
        _lighting.Apply(set, environment);
        _environment.Environment = environment;
        _fallbackLight.Visible = set == null;
    }

    /// <summary>
    /// Adds the player owned by <paramref name="peerId"/> at the map's start, if it is not there
    /// yet. <paramref name="slot"/> picks the character (0 Max, 1 Monica). Call it on every
    /// peer, for every player, when the player joins.
    /// </summary>
    public PlayerCharacter? SpawnPlayer(long peerId, int slot)
    {
        if (_map == null || _models == null)
            return null;
        if (_players.GetNodeOrNull<PlayerCharacter>(PlayerCharacter.NodeName(peerId)) is { } existing)
            return existing;

        var player = PlayerCharacter.Create(peerId, slot);
        _players.AddChild(player);
        var model = PlayerModels.Load(_models, PlayerModels.ForSlot(slot), _notes);
        if (model != null)
            player.SetModel(model);
        player.SetMapBounds(_map.ComputeBounds());

        // A second player starts beside the first rather than inside it.
        var start = _map.Info.CharaPosition;
        var position = new Vector3(start.X, start.Y, start.Z) + new Vector3(slot * PlayerCharacter.BodyRadius * 3.0f, 0, 0);
        player.Teleport(position, 0.0f);

        if (player.IsLocallyControlled && LocalPlayer == null)
        {
            LocalPlayer = player;
            player.Camera = _camera;
            _camera.Follow = player.Visual;
            _camera.Reset(position);
            if (_selfTestFrames > 0)
                StartSelfTest(player);
        }
        UpdateHud();
        return player;
    }

    /// <summary>Removes a player that left.</summary>
    public void RemovePlayer(long peerId)
    {
        if (_players.GetNodeOrNull<PlayerCharacter>(PlayerCharacter.NodeName(peerId)) is not { } player)
            return;
        if (player == LocalPlayer)
        {
            LocalPlayer = null;
            _camera.Follow = null;
        }
        player.QueueFree();
    }

    public override void _Process(double delta)
    {
        if (_reportAfter > 0.0)
        {
            _reportAfter -= delta;
            if (_reportAfter <= 0.0)
            {
                foreach (var player in _players.GetChildren().OfType<PlayerCharacter>())
                    GD.Print($"town net report: player {player.PeerId} slot {player.Slot} {(player.IsLocallyControlled ? "local" : "remote")} at {player.GlobalPosition}, motion {player.Motion.Current ?? "-"}");
                GetTree().Quit();
            }
        }
        if (LocalPlayer != null)
        {
            var pad = _selfTestFrames > 0 ? PadState.Neutral : PadInput.Read();
            _camera.Control(pad, LocalPlayer.Yaw, (float)delta);
            if (_showHelp || Engine.GetFramesDrawn() % 15 == 0)
                UpdateHud();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        switch (key.Keycode)
        {
            case Key.F1:
                _showHelp = !_showHelp;
                UpdateHud();
                break;
            case Key.R when LocalPlayer != null && _map != null:
                var start = _map.Info.CharaPosition;
                LocalPlayer.Teleport(new Vector3(start.X, start.Y, start.Z), 0.0f);
                _camera.Reset(LocalPlayer.GlobalPosition);
                break;
            case Key.Tab:
                ViewerSwitch.Next(this, ViewerSwitch.AssetViewer);
                return;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    /// <summary>Holds the stick forward for the requested game frames, then reports and quits.</summary>
    private void StartSelfTest(PlayerCharacter player)
    {
        var start = player.GlobalPosition;
        player.ReadPad = () => new PadState(0.0f, -1.0f, 0, 0, false, false, false, false);
        player.GameFrameRan += p =>
        {
            if (p.GameFrameCount < _selfTestFrames)
                return;
            p.ReadPad = () => PadState.Neutral;
            var end = p.GlobalPosition;
            float travelled = new Vector2(end.X - start.X, end.Z - start.Z).Length();
            GD.Print($"town selftest: {p.GameFrameCount} frames, from {start} to {end}, travelled {travelled:0.0}, " +
                     $"landed {p.IsLanded}, motion {p.Motion.Current ?? "-"} (step {p.Motion.Step:0.##})");
            foreach (var note in _notes)
                GD.Print($"  {note}");
            bool ok = travelled > 1.0f && p.IsLanded;
            GetTree().Quit(ok ? 0 : 1);
        };
    }

    private void UpdateHud()
    {
        var text = new StringBuilder();
        text.AppendLine("OpenCloud town");
        foreach (var error in _errors)
            text.AppendLine($"! {error}");
        text.AppendLine(_status);
        if (LocalPlayer is { } player)
        {
            var p = player.GlobalPosition;
            text.AppendLine($"player {player.PeerId}: ({p.X:0}, {p.Y:0}, {p.Z:0}) {(player.IsLanded ? "on ground" : "in air")}, motion {player.Motion.Current ?? "-"} (step {player.Motion.Step:0.###}), camera angle {Mathf.RadToDeg(_camera.Angle):0}°");
        }
        int others = _players.GetChildCount() - (LocalPlayer != null ? 1 : 0);
        if (others > 0)
            text.AppendLine($"other players: {others}");
        if (_showHelp)
        {
            foreach (var note in _notes)
                text.AppendLine(note);
            text.AppendLine();
            text.AppendLine("WASD/arrows or left stick move (Shift walks) · Q/E or L1/R1 and J/L or right stick turn the camera · I/K raise/lower");
            text.AppendLine("C or L2 camera behind · R back to start · Tab asset viewer · F1 help");
        }
        _hud.Text = text.ToString();
    }
}
