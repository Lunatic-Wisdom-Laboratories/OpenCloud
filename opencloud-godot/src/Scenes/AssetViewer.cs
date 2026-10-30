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
/// markers, R reload, Tab map viewer, F1 toggle help. Animations (motions of a character pack or glTF clips)
/// are listed on the right: click one or use Up/Down, Space pauses, Left/Right step one motion
/// frame while paused, -/= change the speed, T toggles the game's per-action KEY step.
/// Camera: see <see cref="FreeCamera"/>.
/// </summary>
public partial class AssetViewer : Node3D
{
    /// <summary>Game data to mount; empty uses <see cref="GameDataPaths"/>.</summary>
    [Export] public string[] DataPaths { get; set; } = Array.Empty<string>();

    /// <summary>Model to show first; empty shows Max.</summary>
    [Export] public string InitialModel { get; set; } = string.Empty;

    /// <summary>
    /// Max's model, in the order tried: the field player model <c>SetupMainUnit</c> loads
    /// (<c>mainchr/c01_base2</c> to <c>c01_base5</c>, by weapon), then the edit-mode character
    /// (<c>chara/c01_base</c>, Max in town with the fishing rod), as converted by the converter.
    /// Equipment such as the cap is authored for the field rig.
    /// </summary>
    private static readonly string[] MaxCandidates =
    {
        "mainchr/c01_base2.glb",
        "mainchr/c01_base.glb",
        "chara/c01_base.glb",
    };

    /// <summary>
    /// Max's base rigs hold proxy triangles; <c>SetupMainUnit</c> dresses them with his equipped
    /// clothes and then his footwear (both through <c>LoadSkin</c>). The viewer uses his starting
    /// items (comdat file names <c>fuku1</c>, <c>sh001</c>), with the next pair as fallbacks.
    /// </summary>
    private static readonly string[][] MaxCostumeCandidates =
    {
        new[] { "mainchr/c01fuku1.glb" },
        new[] { "mainchr/c01sh001.glb", "mainchr/c01sh002.glb" },
    };

    /// <summary>Headwear is its own character, hung on Max's "hat" frame (<c>SetRef</c>).</summary>
    private static readonly string[] MaxHatCandidates = { "mainchr/c01ha001.glb" };

    private VirtualFileSystem? _vfs;
    private ModelLoader? _loader;
    private readonly List<string> _models = new();
    private readonly List<string> _errors = new();
    private int _current = -1;
    private string _status = string.Empty;
    private bool _showBones;
    private bool _showHelp = true;

    private Node3D _modelRoot = null!;
    private FreeCamera _camera = null!;
    private Label _hud = null!;
    private ItemList _animationList = null!;

    /// <summary>Animations of the loaded models, by full name ("library/clip").</summary>
    private readonly List<string> _animations = new();
    private readonly List<AnimationPlayer> _players = new();
    private int _animation = -1;
    private float _speed = 1.0f;
    private (Skeleton3D Skeleton, int Bone, string Name)? _watchedBone;

    // Placement test for the hat (H): which turn of the part matches the game.
    private Node3D? _testPart;
    private Basis _testBoneBasis = Basis.Identity;
    private Basis _testPartRoot = Basis.Identity;
    private int _testMode;
    private static readonly string[] TestModes =
    {
        "as loaded", "yaw +90 (Max up)", "yaw 180 (Max up)", "yaw -90 (Max up)", "without the part's root rest",
    };

    /// <summary>Meta on the attachment that holds a part hung on a bone (SetRef).</summary>
    private const string PartMeta = "opencloud_part";
    private bool _useKeyStep = true;
    private bool _paused;

    public override void _Ready()
    {
        _modelRoot = GetNode<Node3D>("ModelRoot");
        _camera = GetNode<FreeCamera>("FreeCamera");
        _hud = GetNode<Label>("Hud/Info");
        _animationList = GetNode<ItemList>("Hud/Animations");
        _animationList.ItemSelected += index => PlayAnimation((int)index);

        var paths = GameDataPaths.Resolve(DataPaths);
        _vfs = GameDataPaths.MountAll(paths, _errors);
        _loader = new ModelLoader(_vfs);

        if (_vfs.Sources.Count == 0)
        {
            _status = "No game data mounted.\n" +
                      "Convert your disc once with opencloud-converter, then run with:  -- --data=<converted folder or its data.zip>\n" +
                      $"or set {GameDataPaths.EnvironmentVariable}, or add data/paths to {GameDataPaths.ConfigPath}.";
            UpdateHud();
            return;
        }

        // Remember working command-line paths for the next run.
        if (_errors.Count == 0 && paths.Count > 0)
            GameDataPaths.Save(paths);

        _models.AddRange(_vfs.EnumerateFiles()
            .Where(path => ModelLoader.ModelExtensions.Contains(VfsPath.GetExtension(path)))
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
            case Key.Tab:
                ViewerSwitch.Next(this, ViewerSwitch.MapViewer);
                return;
            case Key.F1:
                _showHelp = !_showHelp;
                UpdateHud();
                break;
            case Key.Down:
                StepAnimation(1);
                break;
            case Key.Up:
                StepAnimation(-1);
                break;
            case Key.Space:
                _paused = !_paused;
                ApplySpeed();
                break;
            case Key.Right when _paused:
                SeekFrames(1);
                break;
            case Key.Left when _paused:
                SeekFrames(-1);
                break;
            case Key.Minus:
                _speed = Math.Max(_speed / 2.0f, 1.0f / 16.0f);
                ApplySpeed();
                break;
            case Key.Equal:
                _speed = Math.Min(_speed * 2.0f, 16.0f);
                ApplySpeed();
                break;
            case Key.T:
                _useKeyStep = !_useKeyStep;
                ApplySpeed();
                break;
            case Key.Key0:
                ShowRestPose();
                break;
            case Key.H:
                CyclePartTest();
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    /// <summary>
    /// Collects the animation players of the loaded models. A model with a skeleton but no
    /// motions of its own (an outfit shown over Max's rig) shares the first player's libraries,
    /// so its bones follow the same clips wherever their names match.
    /// </summary>
    private void RefreshAnimations()
    {
        _players.Clear();
        _animations.Clear();
        _animation = -1;
        // Attached parts (the hat) play their own motion, as the game does, and are left out.
        _players.AddRange(FindAll<AnimationPlayer>(_modelRoot).Where(p => !InPart(p)));

        if (_players.Count > 0)
        {
            var source = _players[0];
            foreach (var skeleton in FindAll<Skeleton3D>(_modelRoot))
            {
                if (InPart(skeleton) || skeleton.GetParent() is not Node3D model || !model.IsVisibleInTree() ||
                    model.GetNodeOrNull<AnimationPlayer>(ModelMeta.AnimationPlayerName) != null ||
                    _players.Any(p => p.GetParent() == model || p.GetParent()?.IsAncestorOf(skeleton) == true))
                    continue;

                var shared = new AnimationPlayer { Name = ModelMeta.AnimationPlayerName };
                foreach (var library in source.GetAnimationLibraryList())
                    shared.AddAnimationLibrary(library, source.GetAnimationLibrary(library));
                model.AddChild(shared);
                _players.Add(shared);
            }
            // Character clips keep their KEY order; others (glTF) stay in name order.
            _animations.AddRange(source.GetAnimationList()
                .Where(a => a != "RESET")
                .Select((name, order) => (name, order))
                .OrderBy(a => a.name.Split('/')[0], StringComparer.Ordinal)
                .ThenBy(a => source.GetAnimation(a.name) is { } clip && clip.HasMeta(ModelMeta.KeyIndex) ? (int)clip.GetMeta(ModelMeta.KeyIndex) : a.order)
                .Select(a => a.name));
        }

        _animationList.Clear();
        foreach (var name in _animations)
            _animationList.AddItem(DisplayName(_players[0], name));
        _animationList.Visible = _animations.Count > 0;

        if (_animations.Count > 0)
            PlayAnimation(0);
    }

    private static string DisplayName(AnimationPlayer player, string name)
    {
        var animation = player.GetAnimation(name);
        string frames = $"{Mathf.RoundToInt(animation.Length * ModelMeta.FramesPerSecond)}f";
        return animation.HasMeta("step") ? $"{name}  ({frames}, step {(float)animation.GetMeta("step"):0.###})" : $"{name}  ({frames})";
    }

    public override void _Process(double delta)
    {
        // Keep the frame counter live while a clip plays.
        if (_animation >= 0 && !_paused)
            UpdateHud();
    }

    /// <summary>Stops every clip and shows the skeletons in their authored rest pose.</summary>
    private void ShowRestPose()
    {
        foreach (var player in _players)
            player.Stop();
        foreach (var skeleton in FindAll<Skeleton3D>(_modelRoot).Where(s => !InPart(s)))
            skeleton.ResetBonePoses();
        _animation = -1;
        _animationList.DeselectAll();
        UpdateHud();
    }

    private void StepAnimation(int direction)
    {
        if (_animations.Count == 0)
            return;
        PlayAnimation((_animation + direction + _animations.Count) % _animations.Count);
    }

    private void PlayAnimation(int index)
    {
        if (index < 0 || index >= _animations.Count)
            return;
        _animation = index;
        string name = _animations[index];
        foreach (var player in _players)
        {
            if (!player.HasAnimation(name))
                continue;
            // Bones a clip does not key keep their authored pose, not the last clip's.
            foreach (var skeleton in FindAll<Skeleton3D>(player.GetParent()))
                skeleton.ResetBonePoses();
            player.Play(name);
            player.Seek(0, true);
        }
        if (_animationList.IsAnythingSelected() is false || !_animationList.IsSelected(index))
            _animationList.Select(index);
        _animationList.EnsureCurrentIsVisible();
        ApplySpeed();
    }

    /// <summary>
    /// Plays at the KEY's step (motion frames per game frame) times the viewer speed, or paused.
    /// </summary>
    private void ApplySpeed()
    {
        foreach (var player in _players)
        {
            float step = 1.0f;
            if (_useKeyStep && player.CurrentAnimation is { Length: > 0 } current && player.HasAnimation(current))
            {
                var animation = player.GetAnimation(current);
                if (animation.HasMeta("step"))
                    step = Math.Max((float)animation.GetMeta("step"), 0.0f);
            }
            player.SpeedScale = _paused ? 0.0f : step * _speed;
        }
        UpdateHud();
    }

    private void SeekFrames(int frames)
    {
        foreach (var player in _players)
        {
            if (player.CurrentAnimation is not { Length: > 0 })
                continue;
            double length = player.CurrentAnimationLength;
            double time = player.CurrentAnimationPosition + frames / ModelMeta.FramesPerSecond;
            player.Seek(length > 0 ? Mathf.PosMod(time, length) : 0, true);
        }
        UpdateHud();
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
            if (path.Contains("base", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var candidates in MaxCostumeCandidates)
                {
                    var costume = _loader!.FindFirst(candidates);
                    if (costume != null && !Dress(costume))
                        AddModel(costume);
                }
                if (_loader!.FindFirst(MaxHatCandidates) is { } hat)
                    AttachToBone(hat, "hat");
                RefreshAnimations();
            }
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
        RefreshAnimations();
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
            if (_loader.LastDocument is { } document)
            {
                report.Append($"\n  {document.Kind} from {document.Source}");
                foreach (var part in document.Models)
                    report.Append($"\n  {part.Node}: {part.Frames.Length} frames, {part.Animations.Count} actions{(part.DeformFrames.Length > 0 ? ", skinned frames: " + string.Join(" ", part.DeformFrames) : string.Empty)}");
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

    /// <summary>
    /// Puts a costume pack on the loaded character the way the game equips one (LoadSkin);
    /// false when no retail character model is loaded or the pack is not a costume.
    /// </summary>
    private bool Dress(string costume)
    {
        if (_loader == null)
            return false;
        var model = FindAll<Node3D>(_modelRoot).FirstOrDefault(n => n.HasMeta(ModelMeta.DeformFrames));
        if (model == null)
            return false;
        try
        {
            int frames = _loader.ApplySkin(model, costume);
            _status += $"\n{costume}: costume on {frames} frames";
            return frames > 0;
        }
        catch (Exception exception) when (exception is System.IO.InvalidDataException or ArgumentException)
        {
            _status += $"\n{costume}: {exception.Message}";
            return false;
        }
    }

    /// <summary>Loads a model and hangs it on a bone of the loaded character.</summary>
    private void AttachToBone(string path, string bone)
    {
        // Frame names may carry attributes after "--".
        int BoneIndex(Skeleton3D s) => Enumerable.Range(0, s.GetBoneCount())
            .FirstOrDefault(i => NodeNames.StripAttributes(s.GetBoneName(i)) == bone, -1);
        var skeleton = FindAll<Skeleton3D>(_modelRoot).FirstOrDefault(s => s.IsVisibleInTree() && BoneIndex(s) >= 0);
        if (_loader == null || skeleton == null)
            return;
        try
        {
            var attachment = new BoneAttachment3D { Name = NodeNames.Safe("ref_" + bone) }.WithMeta(PartMeta, true);
            _watchedBone = (skeleton, BoneIndex(skeleton), bone);
            skeleton.AddChild(attachment);
            attachment.BoneName = skeleton.GetBoneName(BoneIndex(skeleton));
            var part = _loader.Load(path);
            attachment.AddChild(part);
            _testPart = part as Node3D;
            _testMode = 0;
            _testBoneBasis = skeleton.GetBoneGlobalRest(BoneIndex(skeleton)).Basis.Orthonormalized();
            _testPartRoot = FindAll<Skeleton3D>(part).FirstOrDefault() is { } ps && ps.GetBoneCount() > 0
                ? ps.GetBoneRest(0).Basis.Orthonormalized()
                : Basis.Identity;
            _status += $"\n{path}: on {bone}";
            _status += PlayPartMotion(part);

            // Diagnostics for placement: the bone's rest and bind (bbp) matrices, and the part's root rest.
            int index = BoneIndex(skeleton);
            var rest = skeleton.GetBoneGlobalRest(index);
            _status += $"\n  {bone} rest {Describe(rest)}";
            if (skeleton.GetParent()?.GetMeta(ModelMeta.BaseMatrices, default(Variant)).Obj is Godot.Collections.Array bases && index < bases.Count)
                _status += $"\n  {bone} bbp  {Describe(bases[index].AsTransform3D())}";
            // The chain from Max's root to the bone, then every frame of the part with its local rest.
            var chain = new List<int>();
            for (int b = index; b >= 0; b = skeleton.GetBoneParent(b))
                chain.Insert(0, b);
            foreach (int b in chain)
                _status += $"\n  max {skeleton.GetBoneName(b)}: {Describe(skeleton.GetBoneRest(b))}";
            foreach (var partSkeleton in FindAll<Skeleton3D>(part))
            {
                for (int b = 0; b < partSkeleton.GetBoneCount(); b++)
                {
                    bool mesh = partSkeleton.GetChildren().OfType<BoneAttachment3D>().Any(a => a.BoneName == partSkeleton.GetBoneName(b));
                    _status += $"\n  part {partSkeleton.GetBoneName(b)} (parent {partSkeleton.GetBoneParent(b)}{(mesh ? ", mesh" : string.Empty)}): {Describe(partSkeleton.GetBoneRest(b))}";
                }
            }
        }
        catch (Exception exception)
        {
            _status += $"\n{path}: {exception.Message}";
        }
    }

    /// <summary>
    /// A part plays its own first KEY from its start, as <c>LoadChrFile</c> does once a pack is
    /// read: its motion, not its model's rest, poses its frames.
    /// </summary>
    private static string PlayPartMotion(Node part)
    {
        var report = new StringBuilder();
        foreach (var player in FindAll<AnimationPlayer>(part))
        {
            var first = player.GetAnimationLibraryList()
                .OrderBy(l => l.ToString(), StringComparer.Ordinal)
                .SelectMany(l => player.GetAnimationLibrary(l).GetAnimationList()
                    .Select(a => (Name: l.ToString().Length == 0 ? a.ToString() : $"{l}/{a}", Clip: player.GetAnimationLibrary(l).GetAnimation(a))))
                .OrderBy(a => a.Name.Split('/')[0], StringComparer.Ordinal)
                .ThenBy(a => a.Clip.HasMeta(ModelMeta.KeyIndex) ? (int)a.Clip.GetMeta(ModelMeta.KeyIndex) : 0)
                .Select(a => a.Name)
                .FirstOrDefault();
            if (first == null)
                continue;
            player.Play(first);
            player.Seek(0, true);
            report.Append($"\n  part motion {first}");
            foreach (var skeleton in FindAll<Skeleton3D>(player.GetParent()))
            {
                for (int b = 0; b < skeleton.GetBoneCount(); b++)
                    report.Append($"\n  part {skeleton.GetBoneName(b)} posed {Describe(skeleton.GetBonePose(b))}");
            }
        }
        return report.Length > 0 ? report.ToString() : "\n  part has no motion";
    }

    /// <summary>H: turns the hung part through the placements under test and names the current one.</summary>
    private void CyclePartTest()
    {
        if (_testPart == null || !IsInstanceValid(_testPart))
            return;
        _testMode = (_testMode + 1) % TestModes.Length;
        // A yaw about Max's up axis, written in the bone's space.
        Basis Yaw(float degrees) => _testBoneBasis.Inverse() * new Basis(Vector3.Up, Mathf.DegToRad(degrees)) * _testBoneBasis;
        _testPart.Transform = new Transform3D(_testMode switch
        {
            1 => Yaw(90),
            2 => Yaw(180),
            3 => Yaw(-90),
            4 => _testPartRoot.Inverse(),
            _ => Basis.Identity,
        }, Vector3.Zero);
        UpdateHud();
    }

    private static bool InPart(Node node)
    {
        for (var n = node; n != null; n = n.GetParent())
        {
            if (n.HasMeta(PartMeta))
                return true;
        }
        return false;
    }

    private static string Describe(Transform3D t)
    {
        var euler = t.Basis.GetEuler() * (180.0f / Mathf.Pi);
        return $"pos ({t.Origin.X:0.###}, {t.Origin.Y:0.###}, {t.Origin.Z:0.###}) rot ({euler.X:0.#}, {euler.Y:0.#}, {euler.Z:0.#}) scale ({t.Basis.Scale.X:0.###}, {t.Basis.Scale.Y:0.###}, {t.Basis.Scale.Z:0.###}) det {t.Basis.Determinant():0.###}";
    }

    private void ClearModel()
    {
        _watchedBone = null;
        _players.Clear();
        _animations.Clear();
        _animation = -1;
        _animationList.Clear();
        _animationList.Visible = false;
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
        if (_animation >= 0 && _players.Count > 0)
        {
            var player = _players[0];
            int frame = Mathf.RoundToInt(player.CurrentAnimationPosition * ModelMeta.FramesPerSecond);
            text.AppendLine($"Animation {_animation + 1}/{_animations.Count}: {_animations[_animation]}  frame {frame}" +
                            $"  speed x{_speed:0.###}{(_useKeyStep ? " * KEY step" : string.Empty)}{(_paused ? "  [paused]" : string.Empty)}");
        }
        else if (_animations.Count > 0)
        {
            text.AppendLine("Rest pose (no animation)");
        }
        if (_watchedBone is var (watched, index, name) && IsInstanceValid(watched) && index >= 0)
            text.AppendLine($"  {name} now {Describe(watched.GetBoneGlobalPose(index))}");
        if (_testPart != null && IsInstanceValid(_testPart))
            text.AppendLine($"  H hat test {_testMode + 1}/{TestModes.Length}: {TestModes[_testMode]}");
        if (_showHelp)
        {
            text.AppendLine();
            text.AppendLine("PgDn/PgUp browse  Home Max  F frame  B bones  R reload  Tab map viewer  F1 help");
            text.AppendLine("Up/Down animation  Space pause  Left/Right frame step  -/= speed  T KEY step  0 rest pose");
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
