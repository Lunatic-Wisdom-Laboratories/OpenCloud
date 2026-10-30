using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using OpenCloud.Assets.Maps;
using OpenCloud.Assets.Models;
using OpenCloud.Core;
using OpenCloud.Core.IO;
using OpenCloud.Rendering;

namespace OpenCloud.Scenes;

/// <summary>
/// Debug map viewer, the engine's counterpart of the retail map viewer mode (<c>mapviewlp</c>,
/// an empty stub in the shipped game). It mounts the user's data, lists every map script and
/// shows the chosen map with its lighting, sky and time of day, with a free camera.
///
/// Keys: PageDown/PageUp next/previous map (or click the list), F reframe, R reload, C collision,
/// P function points, H pieces the script hides, L map lighting on/off, [ ] game clock -/+ 1 h,
/// Tab asset viewer, F1 help. Start on a map with <c>-- --map=s01</c> (a name or a path).
/// </summary>
public partial class MapViewer : Node3D
{
    [Export] public string[] DataPaths { get; set; } = Array.Empty<string>();

    /// <summary>Map to show first (name like <c>s01</c> or a <c>.map</c> path); empty shows the first map.</summary>
    [Export] public string InitialMap { get; set; } = string.Empty;

    private VirtualFileSystem? _vfs;
    private MapLoader? _loader;
    private readonly List<string> _maps = new();
    private readonly List<string> _errors = new();
    private int _current = -1;
    private LoadedMap? _map;
    private string _status = string.Empty;
    private bool _showHelp = true;
    private bool _showCollision;
    private bool _showPoints;
    private bool _mapLighting = true;
    private float _clock = 12.0f;

    private Node3D _mapRoot = null!;
    private FreeCamera _camera = null!;
    private MapLightingRig _lighting = null!;
    private DirectionalLight3D _viewerLight = null!;
    private WorldEnvironment _environment = null!;
    private Godot.Environment _viewerEnvironment = null!;
    private Label _hud = null!;
    private ItemList _mapList = null!;

    public override void _Ready()
    {
        _mapRoot = GetNode<Node3D>("MapRoot");
        _camera = GetNode<FreeCamera>("FreeCamera");
        _lighting = GetNode<MapLightingRig>("MapLighting");
        _viewerLight = GetNode<DirectionalLight3D>("ViewerLight");
        _environment = GetNode<WorldEnvironment>("WorldEnvironment");
        _viewerEnvironment = (Godot.Environment)_environment.Environment.Duplicate();
        _hud = GetNode<Label>("Hud/Info");
        _mapList = GetNode<ItemList>("Hud/Maps");
        _mapList.ItemSelected += index => Show((int)index);

        var paths = GameDataPaths.Resolve(DataPaths);
        _vfs = GameDataPaths.MountAll(paths, _errors);
        _loader = new MapLoader(_vfs, new MdsModelLoader(_vfs));

        if (_vfs.Sources.Count == 0)
        {
            _status = "No game data mounted.\n" +
                      "Run with:  -- --data=<path to the disc .iso, DATA.HD4 or an extracted folder>\n" +
                      $"or set {GameDataPaths.EnvironmentVariable}, or add data/paths to {GameDataPaths.ConfigPath}.";
            UpdateHud();
            return;
        }
        if (_errors.Count == 0 && paths.Count > 0)
            GameDataPaths.Save(paths);

        _maps.AddRange(MapLoader.EnumerateMaps(_vfs));
        foreach (var map in _maps)
            _mapList.AddItem(map["map/".Length..^".map".Length]);
        _mapList.Visible = _maps.Count > 0;
        if (_maps.Count == 0)
        {
            _status = "The mounted data has no map/*.map scripts (map viewing needs the retail data: the .iso, DATA.HD4 or an extracted folder).";
            UpdateHud();
            return;
        }

        string initial = InitialMap;
        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith("--map=", StringComparison.Ordinal))
                initial = argument["--map=".Length..];
        }
        int index = 0;
        if (!string.IsNullOrEmpty(initial))
        {
            string wanted = MapLoader.MapPath(initial);
            index = Math.Max(0, _maps.FindIndex(m => VfsPath.Comparer.Equals(m, wanted)));
        }
        Show(index);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        switch (key.Keycode)
        {
            case Key.Pagedown when _maps.Count > 0:
                Show((_current + 1) % _maps.Count);
                break;
            case Key.Pageup when _maps.Count > 0:
                Show((_current - 1 + _maps.Count) % _maps.Count);
                break;
            case Key.R when _current >= 0:
                Show(_current);
                break;
            case Key.F:
                FrameMap();
                break;
            case Key.C:
                _showCollision = !_showCollision;
                SetGroupVisible(MapLoader.CollisionGroup, _showCollision);
                UpdateHud();
                break;
            case Key.P:
                _showPoints = !_showPoints;
                SetGroupVisible(MapLoader.FuncPointGroup, _showPoints);
                UpdateHud();
                break;
            case Key.H when _map != null:
                _map.SetShowHidden(!_map.ShowHidden);
                UpdateHud();
                break;
            case Key.L:
                _mapLighting = !_mapLighting;
                ApplyLighting();
                break;
            case Key.Bracketleft:
                SetClock(_clock - 1.0f);
                break;
            case Key.Bracketright:
                SetClock(_clock + 1.0f);
                break;
            case Key.Tab:
                GetTree().ChangeSceneToFile("res://src/Scenes/AssetViewer.tscn");
                break;
            case Key.F1:
                _showHelp = !_showHelp;
                UpdateHud();
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    private void Show(int index)
    {
        if (_loader == null || index < 0 || index >= _maps.Count)
            return;

        foreach (var child in _mapRoot.GetChildren())
        {
            _mapRoot.RemoveChild(child);
            child.QueueFree();
        }
        _map = null;
        _current = index;
        if (_mapList.IsAnythingSelected() is false || !_mapList.IsSelected(index))
            _mapList.Select(index);
        _mapList.EnsureCurrentIsVisible();

        var started = DateTime.UtcNow;
        try
        {
            _map = _loader.Load(_maps[index], _clock);
            _mapRoot.AddChild(_map.Root);
            SetGroupVisible(MapLoader.CollisionGroup, _showCollision);
            SetGroupVisible(MapLoader.FuncPointGroup, _showPoints);
            _status = $"loaded in {(DateTime.UtcNow - started).TotalSeconds:0.00}s";
            ApplyLighting();
            FrameMap();
        }
        catch (Exception exception)
        {
            _status = $"Failed to load {_maps[index]}:\n{exception.Message}";
            GD.PushError($"{_maps[index]}: {exception}");
        }
        UpdateHud();
    }

    private void SetClock(float clock)
    {
        _clock = (clock % 24.0f + 24.0f) % 24.0f;
        _map?.SetClock(_clock);
        ApplyLighting();
    }

    /// <summary>Map lighting, or the viewer's neutral light and sky when it is off or missing.</summary>
    private void ApplyLighting()
    {
        var set = _mapLighting ? _map?.Lighting : null;
        if (set == null)
        {
            _environment.Environment = _viewerEnvironment;
            _lighting.Apply(null, _viewerEnvironment);
            _viewerLight.Visible = true;
        }
        else
        {
            var environment = (Godot.Environment)_viewerEnvironment.Duplicate();
            _lighting.Apply(set, environment);
            _environment.Environment = environment;
            _viewerLight.Visible = false;
        }
        if (_map?.Sky != null)
            _map.Sky.Visible = _mapLighting;
        UpdateHud();
    }

    private void FrameMap()
    {
        if (_map == null)
            return;
        var bounds = _map.ComputeBounds();
        _camera.Frame(bounds);
        // Maps are large; keep the sky domes inside the far plane.
        _camera.Far = Mathf.Max(_camera.Far, 100000.0f);
        var start = _map.Info.CharaPosition;
        if (start != System.Numerics.Vector3.Zero)
        {
            var position = new Vector3(start.X, start.Y, start.Z);
            _camera.LookAtFromPosition(position + new Vector3(0, 60, 120), position, Vector3.Up);
            _camera._Ready();
        }
    }

    private void SetGroupVisible(string group, bool visible)
    {
        foreach (var node in GetTree().GetNodesInGroup(group))
        {
            if (node is Node3D spatial)
                spatial.Visible = visible;
        }
    }

    private void UpdateHud()
    {
        var text = new StringBuilder();
        text.AppendLine("OpenCloud map viewer");
        foreach (var error in _errors)
            text.AppendLine($"! {error}");
        if (_vfs != null && _vfs.Sources.Count > 0)
            text.AppendLine($"data: {string.Join(", ", _vfs.Sources.Select(s => s.Description))}");

        if (_map != null)
        {
            var info = _map.Info;
            var report = _map.Report;
            var lighting = _map.Lighting;
            text.AppendLine($"[{_current + 1}/{_maps.Count}] {_map.Path}  {_status}");
            text.AppendLine($"parts {_map.Layout.Parts.Count}, placed {report.Placements}, pieces {report.PlacedPieces}, meshes {report.MeshInstances}, characters {report.Characters}, collision tris {report.CollisionTriangles}, points {report.FuncPoints}");
            text.AppendLine($"model lists {report.ModelLists} ({report.Models} models), textures {report.Textures}, sky {(_map.Sky != null ? "yes" : "no")}");
            text.AppendLine($"clock {_clock:00.0}h -> map time {_map.NowTime:00.0}h ({MapInfo.TimeBand(_map.NowTime)}), time flag {(info.TimeEnable ? "on" : "off")}, lighting set {(lighting != null ? lighting.Index.ToString() : "-")}{(_mapLighting ? "" : " (off)")}");
            if (report.MissingModels.Count > 0)
                text.AppendLine($"missing models ({report.MissingModels.Count}): {string.Join(", ", report.MissingModels.Take(8))}{(report.MissingModels.Count > 8 ? ", ..." : "")}");
            if (report.MissingParts.Count > 0)
                text.AppendLine($"missing part definitions: {string.Join(", ", report.MissingParts.Take(8))}");
            foreach (var note in report.Notes.Take(6))
                text.AppendLine($"note: {note}");
        }
        else if (_status.Length > 0)
        {
            text.AppendLine(_status);
        }

        if (_showHelp)
        {
            text.AppendLine();
            text.AppendLine("PgDn/PgUp map · F frame · R reload · C collision" + (_showCollision ? " [on]" : "") +
                            " · P points" + (_showPoints ? " [on]" : "") + " · H hidden pieces" + (_map?.ShowHidden == true ? " [on]" : "") +
                            " · L lighting · [ ] clock · Tab asset viewer · F1 help");
            text.AppendLine("right mouse look · WASD move · Q/E down/up · Shift fast · wheel speed");
        }
        _hud.Text = text.ToString();
    }
}
