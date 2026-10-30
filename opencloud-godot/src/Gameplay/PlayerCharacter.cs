using System;
using Godot;

namespace OpenCloud.Gameplay;

/// <summary>
/// A player walking in a town, controlled the way the town loop's <c>CharaControl</c> and
/// <c>EditMoveChara</c> control the player character, with the ground and wall tests of
/// <c>MoveCheck</c> done through Godot physics against the map's walk collision.
///
/// The controller runs at the game's 30 Hz: each game frame reads the pad, turns the player,
/// picks the motion (stand, walk, run, fall, land) and moves the body; the model is drawn
/// between the last two game frames so motion stays smooth at any refresh rate.
///
/// Players are not singletons: each one is owned by a network peer (its multiplayer
/// authority). Only the owner's instance runs the controller; the others take the owner's
/// state from <see cref="PlayerSync"/>.
/// </summary>
public partial class PlayerCharacter : CharacterBody3D
{
    public const float GameFrameRate = 30.0f;
    public const float GameFrame = 1.0f / GameFrameRate;

    /// <summary>Motion names of the town controller (Shift-JIS <c>KEY</c> names in the data).</summary>
    public static class Motions
    {
        public const string Stand = "立ち";
        public const string Walk = "歩き";
        public const string Run = "走り";
        public const string WaterRun = "水中走り";
        public const string Fall = "落下中";
        public const string Land = "着地";
    }

    /// <summary>Stick speed: units per game frame at full deflection.</summary>
    private const float MoveSpeed = 5.0f;

    /// <summary>Maps whose larger side is under this are walked at <see cref="SmallMapFactor"/>.</summary>
    private const float SmallMapSize = 800.0f;
    private const float SmallMapFactor = 0.7f;

    /// <summary>Most the facing turns towards the stick a game frame, in radians.</summary>
    private const float TurnStep = 0.3f;

    private const float Gravity = 0.6f;
    private const float MaxFallSpeed = 10.0f;

    /// <summary>A fall faster than this ends in the landing motion.</summary>
    private const float HardLandingSpeed = 5.0f;
    private const int LandingFrames = 20;

    /// <summary>Stick strength from which the player runs instead of walking.</summary>
    private const float RunStrength = 0.8f;

    /// <summary>Ground is searched from this high above the feet (the most the player steps up).</summary>
    private const float StepHeight = 10.0f;

    /// <summary>How far below the probe start the ground is searched.</summary>
    private const float FootDepth = 20.0f;

    /// <summary>Radius of the body against walls.</summary>
    public const float BodyRadius = 13.0f;

    /// <summary>Height of the wall cylinder above <see cref="StepHeight"/>.</summary>
    private const float BodyHeight = 40.0f;

    /// <summary>A player below this height is put back up at <see cref="RespawnHeight"/>.</summary>
    private const float KillHeight = -500.0f;
    private const float RespawnHeight = 500.0f;

    private enum ControlMode { Free, Landing }

    [Export(PropertyHint.Layers3DPhysics)] public uint GroundMask { get; set; } = 1;

    private Node3D? _model;
    private CharacterMotion _motion = new(null);
    private float _yaw;
    private Vector3 _velocity;
    private ControlMode _mode;
    private int _modeFrames;
    private int _fallFrames;
    private bool _landed;
    private double _accumulator;
    private Transform3D _previous;
    private Transform3D _current;

    /// <summary>Network peer that owns this player (1 is the host, or the only player offline).</summary>
    public long PeerId { get; private set; } = 1;

    /// <summary>Player slot: 0 is Max, 1 Monica, as the save's player roster numbers them.</summary>
    public int Slot { get; set; }

    /// <summary>The camera the stick is relative to; null moves along the world axes.</summary>
    public TownCamera? Camera { get; set; }

    /// <summary>Speed factor for small maps, from the map's size.</summary>
    public float MapSpeedFactor { get; private set; } = 1.0f;

    /// <summary>Supplies the pad each game frame; the local controller by default.</summary>
    public Func<PadState> ReadPad { get; set; } = PadInput.Read;

    /// <summary>Game frames run so far by the owner.</summary>
    public uint GameFrameCount { get; private set; }

    public CharacterMotion Motion => _motion;

    /// <summary>The drawn model (interpolated between game frames); the camera follows it.</summary>
    public Node3D Visual => _model ?? this;

    public float Yaw => _yaw;

    public bool IsLanded => _landed;

    public bool IsLocallyControlled => IsMultiplayerAuthority();

    /// <summary>Raised after every game frame the owner runs.</summary>
    public event Action<PlayerCharacter>? GameFrameRan;

    /// <summary>Makes a player owned by <paramref name="peerId"/>, named so every peer finds it at the same path.</summary>
    public static PlayerCharacter Create(long peerId, int slot)
    {
        var player = new PlayerCharacter { Name = NodeName(peerId), PeerId = peerId, Slot = slot };
        player.SetMultiplayerAuthority((int)peerId);
        return player;
    }

    public static string NodeName(long peerId) => $"Player{peerId}";

    public override void _Ready()
    {
        MotionMode = MotionModeEnum.Floating;
        CollisionLayer = 0;
        CollisionMask = GroundMask;
        // The wall cylinder starts above the step height so ledges up to it are walked over.
        AddChild(new CollisionShape3D
        {
            Name = "Body",
            Shape = new CylinderShape3D { Radius = BodyRadius, Height = BodyHeight },
            Position = new Vector3(0, StepHeight + BodyHeight / 2.0f, 0),
        });
        AddChild(new PlayerSync { Name = PlayerSync.NodeName });
        // Children added here take the owner too, so the owner's sync RPCs are accepted.
        SetMultiplayerAuthority((int)PeerId);
        _previous = _current = GlobalTransform;
    }

    /// <summary>Puts the model under the player; it is drawn interpolated between game frames.</summary>
    public void SetModel(Node3D model)
    {
        _model?.QueueFree();
        _model = model;
        model.TopLevel = true;
        AddChild(model);
        _motion = new CharacterMotion(FindAnimationPlayer(model));
        _motion.SetMotion(Motions.Stand, MotionFlags.Restart);
        _motion.Tick();
        model.GlobalTransform = GlobalTransform;
    }

    /// <summary>Places the player, as <c>SetPosition</c>/<c>SetRotation</c> do, and stops it.</summary>
    public void Teleport(Vector3 position, float yaw)
    {
        _yaw = yaw;
        _velocity = Vector3.Zero;
        GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), position);
        _previous = _current = GlobalTransform;
        if (_model != null)
            _model.GlobalTransform = GlobalTransform;
    }

    /// <summary>Speed factor from the map's extent, as <c>CharaControl</c> slows the player on small maps.</summary>
    public void SetMapBounds(Aabb bounds)
    {
        float extent = Mathf.Max(bounds.Size.X, bounds.Size.Z);
        MapSpeedFactor = extent < SmallMapSize ? SmallMapFactor : 1.0f;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!IsLocallyControlled)
            return;
        _accumulator += delta;
        // Never run more than a few game frames to catch up after a stall.
        if (_accumulator > GameFrame * 4)
            _accumulator = GameFrame * 4;
        while (_accumulator >= GameFrame)
        {
            _accumulator -= GameFrame;
            _previous = _current;
            RunGameFrame(ReadPad(), (float)delta);
            _current = GlobalTransform;
        }
    }

    public override void _Process(double delta)
    {
        if (_model == null)
            return;
        if (!IsLocallyControlled)
        {
            _model.GlobalTransform = GlobalTransform;
            return;
        }
        double physics = 1.0 / Engine.PhysicsTicksPerSecond;
        float weight = (float)Math.Clamp((_accumulator + Engine.GetPhysicsInterpolationFraction() * physics) / GameFrame, 0.0, 1.0);
        _model.GlobalTransform = _previous.InterpolateWith(_current, weight);
    }

    /// <summary>
    /// Applies the owner's state on another peer: position, facing and motion of one game frame.
    /// </summary>
    public void ApplyRemoteState(Vector3 position, float yaw, string motion, float step)
    {
        _yaw = yaw;
        GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), position);
        if (_model != null)
            _model.GlobalTransform = GlobalTransform;
        if (motion.Length > 0 && motion != _motion.Current)
            _motion.SetMotion(motion);
        if (!Mathf.IsEqualApprox(step, _motion.Step))
            _motion.SetStep(step);
        _motion.Tick();
    }

    /// <summary>One game frame of <c>CharaControl</c>.</summary>
    private void RunGameFrame(PadState pad, float physicsDelta)
    {
        GameFrameCount++;
        float angle = Camera?.Angle ?? 0.0f;
        float cos = Mathf.Cos(angle);
        float sin = Mathf.Sin(angle);
        float speedX = (pad.LeftX * cos + pad.LeftY * sin) * MoveSpeed * MapSpeedFactor;
        float speedZ = (-pad.LeftX * sin + pad.LeftY * cos) * MoveSpeed * MapSpeedFactor;
        if (_fallFrames > 0)
        {
            speedX = 0.0f;
            speedZ = 0.0f;
        }

        _velocity.X = speedX;
        _velocity.Z = speedZ;
        _velocity.Y -= Gravity;

        if (_mode == ControlMode.Landing)
        {
            _velocity.X = 0.0f;
            _velocity.Z = 0.0f;
            _modeFrames--;
            if (_modeFrames <= 0 || _motion.IsEnded)
                _mode = ControlMode.Free;
        }
        else if (speedX != 0.0f || speedZ != 0.0f)
        {
            float target = Mathf.Atan2(speedX, speedZ);
            float next = TurnTowards(_yaw, target, TurnStep);
            // The game truncates the error to whole radians before comparing it, so only turns
            // of two radians or more halve the speed.
            if (MathF.Truncate(MathF.Abs(target - next)) > 1.0f)
            {
                _velocity.X *= 0.5f;
                _velocity.Z *= 0.5f;
            }
            _yaw = next;
            float strength = Mathf.Sqrt(pad.LeftX * pad.LeftX + pad.LeftY * pad.LeftY);
            if (strength < RunStrength)
            {
                _motion.SetMotion(Motions.Walk);
                _motion.SetStep(0.1f + strength / RunStrength);
            }
            else
            {
                _motion.SetMotion(Motions.Run);
                _motion.SetStep(_motion.DefaultStep);
            }
        }
        else
        {
            _motion.SetMotion(Motions.Stand);
            _motion.SetStep(_motion.DefaultStep);
        }

        bool hardLanding = Move(physicsDelta);

        if (!_landed)
        {
            _fallFrames++;
            if (_fallFrames > 3)
            {
                _motion.SetMotion(Motions.Fall);
                _fallFrames = 3;
            }
        }
        else
        {
            _fallFrames = 0;
        }

        if (_mode == ControlMode.Free && hardLanding)
        {
            _mode = ControlMode.Landing;
            _modeFrames = LandingFrames;
            _motion.SetMotion(Motions.Land, MotionFlags.Restart | MotionFlags.Hold);
        }

        _motion.Tick();
        GameFrameRan?.Invoke(this);
    }

    /// <summary>
    /// <c>EditMoveChara</c> with <c>MoveCheck</c>: slides the wall cylinder along the walls,
    /// then looks for ground from <see cref="StepHeight"/> above the feet down
    /// <see cref="FootDepth"/>. Ground close enough below (or anything up to the step height)
    /// lands the player on it; otherwise the player falls. Returns true on a hard landing.
    /// </summary>
    private bool Move(float physicsDelta)
    {
        var start = GlobalPosition;
        var horizontal = new Vector3(_velocity.X, 0.0f, _velocity.Z);
        if (horizontal != Vector3.Zero && physicsDelta > 0.0f)
        {
            Velocity = horizontal / physicsDelta;
            MoveAndSlide();
            Velocity = Vector3.Zero;
        }

        var position = GlobalPosition;
        position.Y = start.Y;
        // The ground search starts where the fall takes the feet; rising, it must touch the feet.
        float margin = _velocity.Y > 0.1f ? 0.0f : 4.0f;
        float probe = position.Y + StepHeight + _velocity.Y;
        _landed = false;
        bool hardLanding = false;
        if (FindGround(new Vector3(position.X, probe, position.Z), out float ground) &&
            ground > probe + _velocity.Y - StepHeight - margin)
        {
            _landed = true;
            position.Y = ground;
        }
        else
        {
            position.Y += _velocity.Y;
        }

        if (_landed)
        {
            hardLanding = _velocity.Y < -HardLandingSpeed;
            _velocity.Y = 0.0f;
        }
        if (_velocity.Y < -MaxFallSpeed)
            _velocity.Y = -MaxFallSpeed;
        if (position.Y < KillHeight)
            position.Y = RespawnHeight;

        GlobalTransform = new Transform3D(new Basis(Vector3.Up, _yaw), position);
        return hardLanding;
    }

    /// <summary><c>GetFootPoly</c>: the highest walkable surface under <paramref name="from"/> within <see cref="FootDepth"/>.</summary>
    private bool FindGround(Vector3 from, out float height)
    {
        height = 0.0f;
        var space = GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(from, from - new Vector3(0, FootDepth, 0), GroundMask);
        query.HitBackFaces = true;
        query.HitFromInside = true;
        var hit = space.IntersectRay(query);
        if (hit.Count == 0)
            return false;
        // Near-vertical faces are walls, not ground.
        var normal = (Vector3)hit["normal"];
        if (Mathf.Abs(normal.Y) < 0.05f)
            return false;
        height = ((Vector3)hit["position"]).Y;
        return true;
    }

    /// <summary>
    /// <c>mgAngleInterpolate</c> in step mode: turns by at most <paramref name="step"/> towards
    /// <paramref name="target"/> the short way, landing on it when closer.
    /// </summary>
    public static float TurnTowards(float from, float target, float step)
    {
        float gap = Mathf.Wrap(target - from, -Mathf.Pi, Mathf.Pi);
        if (Mathf.Abs(gap) < step)
            return target;
        return Mathf.Wrap(from + Mathf.Sign(gap) * step, -Mathf.Pi, Mathf.Pi);
    }

    /// <summary>The rig's player; parts hung on its frames (the cap) keep their own.</summary>
    private static AnimationPlayer? FindAnimationPlayer(Node node)
    {
        if (node is AnimationPlayer player)
            return player;
        foreach (var child in node.GetChildren())
        {
            if (child is BoneAttachment3D)
                continue;
            if (FindAnimationPlayer(child) is { } found)
                return found;
        }
        return null;
    }
}
