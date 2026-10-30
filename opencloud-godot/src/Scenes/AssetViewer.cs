using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using OpenCloud.Assets.Models;
using OpenCloud.Core;
using OpenCloud.Core.IO;
using OpenCloud.Rendering;

namespace OpenCloud.Scenes;

/// <summary>
/// Debug asset viewer, the engine's counterpart of the retail character/map viewer modes
/// (<c>charaviewlp</c>, <c>mapviewlp</c>, empty stubs in the shipped game). It mounts the user's
/// data, loads Max by default and lets any model of the data be browsed with a free camera.
///
/// Keys: PageDown/PageUp next/previous model, Home back to Max, F reframe, B toggle bone
/// markers, R reload, F1 toggle help. Camera: see <see cref="FreeCamera"/>.
/// </summary>
public partial class AssetViewer : Node3D
{
    /// <summary>Game data to mount; empty uses <see cref="GameDataPaths"/>.</summary>
    [Export] public string[] DataPaths { get; set; } = Array.Empty<string>();

    /// <summary>Model to show first; empty shows Max.</summary>
    [Export] public string InitialModel { get; set; } = string.Empty;

    /// <summary>
    /// Max's model, in the order tried: the edit-mode character in retail PACK form and as
    /// converted glTF, then the player models <c>SetupMainUnit</c> loads.
    /// </summary>
    private static readonly string[] MaxCandidates =
    {
        "chara/c01_base.chr",
        "chara/c01_base.gltf",
        "mainchr/c01_base2.chr",
        "mainchr/c01_base2.gltf",
        "mainchr/c01_base.chr",
        "mainchr/c01_base.gltf",
    };

    /// <summary>
    /// Max's base rigs hold proxy triangles; his textured outfit is a separate model that the game
    /// equips, shown on top of the rig when present.
    /// </summary>
    private static readonly string[] MaxOutfitCandidates =
    {
        "mainchr/c01fuku1.chr",
        "mainchr/c01fuku1.gltf",
    };

    private VirtualFileSystem? _vfs;
    private GameModelLoader? _loader;
    private readonly List<string> _models = new();
    private readonly List<string> _errors = new();
    private int _current = -1;
    private string _status = string.Empty;
    private bool _showBones;
    private bool _showHelp = true;

    private Node3D _modelRoot = null!;
    private FreeCamera _camera = null!;
    private Label _hud = null!;

    public override void _Ready()
    {
        _modelRoot = GetNode<Node3D>("ModelRoot");
        _camera = GetNode<FreeCamera>("FreeCamera");
        _hud = GetNode<Label>("Hud/Info");

        var paths = GameDataPaths.Resolve(DataPaths);
        _vfs = GameDataPaths.MountAll(paths, _errors);
        _loader = new GameModelLoader(_vfs);

        if (_vfs.Sources.Count == 0)
        {
            _status = "No game data mounted.\n" +
                      "Run with:  -- --data=<path to data.zip, the disc .iso, DATA.HD4 or an extracted folder>\n" +
                      $"or set {GameDataPaths.EnvironmentVariable}, or add data/paths to {GameDataPaths.ConfigPath}.";
            UpdateHud();
            return;
        }

        // Remember working command-line paths for the next run.
        if (_errors.Count == 0 && paths.Count > 0)
            GameDataPaths.Save(paths);

        _models.AddRange(_vfs.EnumerateFiles()
            .Where(path => GameModelLoader.ModelExtensions.Contains(VfsPath.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(InitialModel))
            Show(InitialModel);
        else
            ShowMax();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        switch (key.Keycode)
        {
            case Key.Pagedown:
                Step(1);
                break;
            case Key.Pageup:
                Step(-1);
                break;
            case Key.Home:
                ShowMax();
                break;
            case Key.R when _current >= 0:
                Show(_models[_current]);
                break;
            case Key.F:
                FrameModel();
                break;
            case Key.B:
                _showBones = !_showBones;
                ApplyBoneMarkers();
                break;
            case Key.F1:
                _showHelp = !_showHelp;
                UpdateHud();
                break;
        }
    }

    private void Step(int direction)
    {
        if (_models.Count == 0)
            return;
        int next = _current < 0 ? (direction > 0 ? 0 : _models.Count - 1) : (_current + direction + _models.Count) % _models.Count;
        Show(_models[next]);
    }

    private void ShowMax()
    {
        var path = _loader?.FindFirst(MaxCandidates);
        if (path == null)
        {
            _status = "Max's model was not found. Tried: " + string.Join(", ", MaxCandidates) +
                      $"\n{_models.Count} models available; use PageDown/PageUp to browse.";
            ClearModel();
            UpdateHud();
            return;
        }

        if (Show(path))
        {
            var outfit = _loader!.FindFirst(MaxOutfitCandidates);
            if (outfit != null && path.Contains("base", StringComparison.OrdinalIgnoreCase))
                AddModel(outfit);
            FrameModel();
            UpdateHud();
        }
    }

    private bool Show(string path)
    {
        ClearModel();
        _current = _models.FindIndex(m => VfsPath.Comparer.Equals(m, VfsPath.Normalize(path)));
        _status = string.Empty;
        bool loaded = AddModel(path);
        FrameModel();
        UpdateHud();
        return loaded;
    }

    private bool AddModel(string path)
    {
        if (_loader == null)
            return false;

        try
        {
            ulong start = Time.GetTicksMsec();
            var node = _loader.Load(path);
            _modelRoot.AddChild(node);
            ulong elapsed = Time.GetTicksMsec() - start;

            var report = new StringBuilder();
            report.Append($"{path}  ({elapsed} ms)");
            if (VfsPath.GetExtension(path) is not ("gltf" or "glb"))
            {
                var r = _loader.Mds.LastReport;
                report.Append($"\n  frames {r.Objects}, meshes {r.Meshes}, triangles {r.Triangles}");
                if (r.SkippedPrimitives > 0)
                    report.Append($", skipped point/line primitives {r.SkippedPrimitives}");
                if (r.MissingTextures.Count > 0)
                    report.Append($"\n  missing textures: {string.Join(", ", r.MissingTextures.Take(8))}{(r.MissingTextures.Count > 8 ? "..." : string.Empty)}");
            }
            _status = _status.Length == 0 ? report.ToString() : _status + "\n" + report;
            ApplyBoneMarkers();
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError($"Failed to load {path}: {exception}");
            _status += (_status.Length == 0 ? string.Empty : "\n") + $"{path}: {exception.Message}";
            return false;
        }
    }

    private void ClearModel()
    {
        foreach (var child in _modelRoot.GetChildren())
        {
            _modelRoot.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>Frames the camera once the skeletons have placed their attachments.</summary>
    private async void FrameModel()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Aabb? bounds = null;
        foreach (var mesh in FindAll<MeshInstance3D>(_modelRoot))
        {
            if (!mesh.Visible || mesh.Mesh == null)
                continue;
            var box = mesh.GlobalTransform * mesh.GetAabb();
            bounds = bounds?.Merge(box) ?? box;
        }
        _camera.Frame(bounds ?? new Aabb(new Vector3(-1, -1, -1), new Vector3(2, 2, 2)));
    }

    private void ApplyBoneMarkers()
    {
        foreach (var skeleton in FindAll<Skeleton3D>(_modelRoot))
        {
            var existing = skeleton.GetNodeOrNull<Node3D>("BoneMarkers");
            existing?.QueueFree();
            if (!_showBones)
                continue;

            var markers = new Node3D { Name = "BoneMarkers", TopLevel = true };
            skeleton.AddChild(markers);
            var sphere = new SphereMesh { Radius = 0.02f, Height = 0.04f, RadialSegments = 6, Rings = 3 };
            var material = new StandardMaterial3D
            {
                AlbedoColor = new Color(1.0f, 0.8f, 0.1f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                NoDepthTest = true,
            };
            for (int i = 0; i < skeleton.GetBoneCount(); i++)
            {
                markers.AddChild(new MeshInstance3D
                {
                    Mesh = sphere,
                    MaterialOverride = material,
                    Transform = new Transform3D(Basis.Identity, (skeleton.GlobalTransform * skeleton.GetBoneGlobalRest(i)).Origin),
                });
            }
        }
    }

    private void UpdateHud()
    {
        var text = new StringBuilder();
        text.AppendLine("OpenCloud asset viewer");
        if (_vfs != null)
        {
            foreach (var source in _vfs.Sources)
                text.AppendLine($"  {source.Description}");
        }
        foreach (var error in _errors)
            text.AppendLine($"  error: {error}");
        if (_models.Count > 0)
            text.AppendLine($"Model {(_current >= 0 ? _current + 1 : 0)}/{_models.Count}");
        if (_status.Length > 0)
            text.AppendLine(_status);
        if (_showHelp)
        {
            text.AppendLine();
            text.AppendLine("PgDn/PgUp browse  Home Max  F frame  B bones  R reload  F1 help");
            text.AppendLine("RMB look  WASD move  Q/E down/up  Shift fast  Wheel speed");
        }
        _hud.Text = text.ToString();
    }

    private static IEnumerable<T> FindAll<T>(Node root) where T : Node
    {
        foreach (var child in root.GetChildren())
        {
            if (child is T match)
                yield return match;
            foreach (var nested in FindAll<T>(child))
                yield return nested;
        }
    }

    public override void _ExitTree()
    {
        _vfs?.Dispose();
    }
}
