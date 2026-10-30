using Godot;

namespace OpenCloud.Gameplay;

/// <summary>
/// The town's walking camera, as <c>CCameraControl::MoveCamera</c> moves it with the
/// parameters <c>EditInit</c> and <c>EditCameraControl</c> give it. The eye trails the player:
/// it keeps between <see cref="MinDistance"/> and <see cref="MaxDistance"/> on the ground plane
/// from the look-at point (the player plus 30 up), the controller turns it about that point and
/// raises or lowers it, and L2 swings it behind the player. Every quantity is per game frame
/// (30 Hz) and is scaled to the real frame time.
/// </summary>
public partial class TownCamera : Camera3D
{
    public const float GameFrameRate = 30.0f;

    /// <summary>Look-at offset above the followed position.</summary>
    [Export] public float FollowHeight { get; set; } = 30.0f;
    [Export] public float MinDistance { get; set; } = 100.0f;
    [Export] public float MaxDistance { get; set; } = 160.0f;

    /// <summary>Height added to the eye at the least and at the most distance (town values).</summary>
    [Export] public float NearHeight { get; set; } = 10.0f;
    [Export] public float FarHeight { get; set; } = 10.0f;

    [Export] public float MinHeight { get; set; } = -15.0f;
    [Export] public float MaxHeight { get; set; } = 40.0f;
    [Export] public float RestMinHeight { get; set; } = -15.0f;
    [Export] public float RestMaxHeight { get; set; } = 20.0f;

    /// <summary>Height the eye keeps above the ground under it (town value).</summary>
    [Export] public float GroundSpace { get; set; } = 30.0f;

    /// <summary>Game frames the eye and the look-at point take to cover their gap (speed 4, 3).</summary>
    [Export] public float EyeSpeed { get; set; } = 4.0f;
    [Export] public float TargetSpeed { get; set; } = 3.0f;

    /// <summary>Turn per game frame at full stick, in radians.</summary>
    [Export] public float TurnRate { get; set; } = 0.05f;

    /// <summary>Reverses the turn direction, like the game's camera option.</summary>
    [Export] public bool ReverseRotation { get; set; }

    /// <summary>Physics layers the eye is kept out of (the map's camera collision).</summary>
    [Export(PropertyHint.Layers3DPhysics)] public uint CollisionMask { get; set; } = 2;

    /// <summary>Layers the eye stays above (the walkable ground).</summary>
    [Export(PropertyHint.Layers3DPhysics)] public uint GroundMask { get; set; } = 1;

    private Vector3 _eye;
    private Vector3 _target;
    private Vector3 _nextEye;
    private Vector3 _nextTarget;
    private float _height = 20.0f;
    private float? _swingTo;

    /// <summary>The node followed; its global position is the player's feet.</summary>
    public Node3D? Follow { get; set; }

    /// <summary>
    /// Angle of the eye about the look-at point, as <c>GetAngle</c> returns it: the eye sits
    /// at (sin, cos) times its distance from the point. Player movement is relative to it.
    /// </summary>
    public float Angle
    {
        get
        {
            var offset = _target - _eye;
            return Mathf.Atan2(-offset.X, -offset.Z);
        }
    }

    /// <summary>Puts the eye behind <paramref name="position"/> at once, as <c>EditInit</c> places it.</summary>
    public void Reset(Vector3 position, float angle = 0.0f, float distance = 130.0f, float height = 20.0f)
    {
        _height = height;
        _nextTarget = position + Vector3.Up * FollowHeight;
        _nextEye = _nextTarget + new Vector3(Mathf.Sin(angle) * distance, height, Mathf.Cos(angle) * distance);
        _target = _nextTarget;
        _eye = _nextEye;
        _swingTo = null;
        LookAtFromPosition(_eye, _target, Vector3.Up);
    }

    /// <summary>Applies one frame of controller input; <paramref name="playerYaw"/> is the player's facing.</summary>
    public void Control(PadState pad, float playerYaw, float delta)
    {
        if (Follow == null)
            return;
        float frames = delta * GameFrameRate;
        _nextTarget = Follow.GlobalPosition + Vector3.Up * FollowHeight;

        var toTarget = _nextTarget - _nextEye;
        toTarget.Y = 0.0f;
        float distance = toTarget.Length();
        var direction = distance > 0.0f ? toTarget / distance : Vector3.Back;
        if (distance <= 0.0f)
            distance = 1.0f;

        var correction = Vector3.Zero;
        if (distance < MinDistance)
            correction = direction * (distance - MinDistance);
        if (distance > MaxDistance)
            correction = direction * (distance - MaxDistance);

        // A turn is faster when the eye is closer than the least distance.
        float turn = -TurnRate * pad.RightX;
        if (pad.TurnLeft)
            turn = TurnRate;
        if (pad.TurnRight)
            turn = -TurnRate;
        if (ReverseRotation)
            turn = -turn;
        turn = turn * MinDistance / Mathf.Min(distance, MinDistance) * frames;
        if (turn != 0.0f)
        {
            _swingTo = null;
            var offset = _nextEye - _nextTarget;
            _nextEye = _nextTarget + offset.Rotated(Vector3.Up, turn);
        }

        float rise = -2.0f * pad.RightY * frames;
        _height = Mathf.Clamp(_height + rise, MinHeight, MaxHeight);
        if (rise == 0.0f)
        {
            float rest = 1.0f - Mathf.Pow(1.0f - 1.0f / 20.0f, frames);
            if (_height < RestMinHeight)
                _height += (RestMinHeight - _height) * rest;
            if (_height > RestMaxHeight)
                _height += (RestMaxHeight - _height) * rest;
        }

        float ratio = (distance - MinDistance) / (MaxDistance - MinDistance);
        correction.Y = NearHeight + ratio * (FarHeight - NearHeight);
        _nextEye.Y = _nextTarget.Y + _height;
        _nextEye += correction;

        if (pad.CameraBehind)
            _swingTo = Mathf.Wrap(playerYaw - Mathf.Pi, -Mathf.Pi, Mathf.Pi);
        if (_swingTo is { } swing)
        {
            // Turns by up to one radian a game frame onto the angle behind the player.
            float current = EyeAngle(_nextEye, _nextTarget);
            float gap = Mathf.Wrap(swing - current, -Mathf.Pi, Mathf.Pi);
            float stepped = Mathf.Abs(gap) <= frames ? swing : current + Mathf.Sign(gap) * frames;
            SetEyeAngle(stepped);
            if (Mathf.Abs(Mathf.Wrap(swing - stepped, -Mathf.Pi, Mathf.Pi)) < 0.1f)
                _swingTo = null;
        }

        KeepOutOfWalls();
        Step(frames);
    }

    private static float EyeAngle(Vector3 eye, Vector3 target) => Mathf.Atan2(eye.X - target.X, eye.Z - target.Z);

    private void SetEyeAngle(float angle)
    {
        var offset = _nextEye - _nextTarget;
        float distance = new Vector2(offset.X, offset.Z).Length();
        _nextEye = _nextTarget + new Vector3(Mathf.Sin(angle) * distance, offset.Y, Mathf.Cos(angle) * distance);
    }

    /// <summary>Pulls the eye in front of camera walls and keeps it above the ground.</summary>
    private void KeepOutOfWalls()
    {
        var space = GetWorld3D()?.DirectSpaceState;
        if (space == null)
            return;
        if (CollisionMask != 0)
        {
            var query = PhysicsRayQueryParameters3D.Create(_nextTarget, _nextEye, CollisionMask);
            var hit = space.IntersectRay(query);
            if (hit.Count > 0)
            {
                var point = (Vector3)hit["position"];
                _nextEye = point + (_nextTarget - point).Normalized() * 2.0f;
            }
        }
        if (GroundMask != 0)
        {
            var from = _nextEye + Vector3.Up * 200.0f;
            var query = PhysicsRayQueryParameters3D.Create(from, _nextEye - Vector3.Up * GroundSpace, GroundMask);
            var hit = space.IntersectRay(query);
            if (hit.Count > 0)
            {
                float ground = ((Vector3)hit["position"]).Y;
                if (ground <= _nextTarget.Y && _nextEye.Y < ground + GroundSpace)
                    _nextEye.Y = ground + GroundSpace;
            }
        }
    }

    /// <summary><c>mgCCamera::Step</c>: eye and look-at point cover 1/speed of their gap a game frame.</summary>
    private void Step(float frames)
    {
        float eye = 1.0f - Mathf.Pow(1.0f - 1.0f / Mathf.Max(EyeSpeed, 1.0f), frames);
        float target = 1.0f - Mathf.Pow(1.0f - 1.0f / Mathf.Max(TargetSpeed, 1.0f), frames);
        _eye = _eye.Lerp(_nextEye, eye);
        _target = _target.Lerp(_nextTarget, target);
        if (!_eye.IsEqualApprox(_target))
            LookAtFromPosition(_eye, _target, Vector3.Up);
    }
}
