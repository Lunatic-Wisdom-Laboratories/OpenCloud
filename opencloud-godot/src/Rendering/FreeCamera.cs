using Godot;

namespace OpenCloud.Rendering;

/// <summary>
/// Free-flying debug camera for viewers: hold the right mouse button to look around, WASD to
/// move, Q/E to go down/up, Shift to move faster and the mouse wheel to change speed.
/// </summary>
public partial class FreeCamera : Camera3D
{
    [Export] public float Speed { get; set; } = 40.0f;
    [Export] public float FastMultiplier { get; set; } = 4.0f;
    [Export] public float MouseSensitivity { get; set; } = 0.003f;

    private float _yaw;
    private float _pitch;
    private bool _looking;

    public override void _Ready()
    {
        var euler = GlobalTransform.Basis.GetEuler();
        _pitch = euler.X;
        _yaw = euler.Y;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } button:
                _looking = button.Pressed;
                Input.MouseMode = _looking ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                Speed *= 1.2f;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                Speed = Mathf.Max(Speed / 1.2f, 0.01f);
                break;
            case InputEventMouseMotion motion when _looking:
                _yaw -= motion.Relative.X * MouseSensitivity;
                _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, -Mathf.Pi * 0.49f, Mathf.Pi * 0.49f);
                ApplyRotation();
                break;
        }
    }

    public override void _Process(double delta)
    {
        var direction = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) direction -= Basis.Z;
        if (Input.IsKeyPressed(Key.S)) direction += Basis.Z;
        if (Input.IsKeyPressed(Key.A)) direction -= Basis.X;
        if (Input.IsKeyPressed(Key.D)) direction += Basis.X;
        if (Input.IsKeyPressed(Key.E)) direction += Vector3.Up;
        if (Input.IsKeyPressed(Key.Q)) direction -= Vector3.Up;
        if (direction == Vector3.Zero)
            return;

        float speed = Speed * (Input.IsKeyPressed(Key.Shift) ? FastMultiplier : 1.0f);
        GlobalPosition += direction.Normalized() * speed * (float)delta;
    }

    /// <summary>Places the camera so that the box fills the view, looking at it from the front.</summary>
    public void Frame(Aabb box)
    {
        float radius = Mathf.Max(box.Size.Length() * 0.5f, 0.1f);
        float distance = radius / Mathf.Sin(Mathf.DegToRad(Fov * 0.5f));
        var center = box.GetCenter();
        // View from the +Z side, slightly above the centre.
        LookAtFromPosition(center + new Vector3(0, radius * 0.25f, distance), center, Vector3.Up);
        Speed = Mathf.Max(radius, 0.5f);
        Near = Mathf.Max(radius * 0.001f, 0.01f);
        Far = Mathf.Max(distance * 50.0f, 1000.0f);
        _Ready();
    }

    private void ApplyRotation()
    {
        Basis = Basis.FromEuler(new Vector3(_pitch, _yaw, 0));
    }
}
