using Godot;

namespace OpenCloud.Gameplay;

/// <summary>One game frame of controller input, in the game's conventions.</summary>
/// <remarks>
/// Sticks are in [-1, 1] after the game's calibration; Y is negative when the stick is pushed
/// up (away from the player), as on the original controller. Buttons are held states.
/// </remarks>
public readonly record struct PadState(
    float LeftX,
    float LeftY,
    float RightX,
    float RightY,
    bool TurnLeft,
    bool TurnRight,
    bool CameraBehind,
    bool Confirm)
{
    public static readonly PadState Neutral = default;
}

/// <summary>
/// Reads the local player's controller and keyboard into a <see cref="PadState"/>. The input
/// actions are registered at run time so the project file holds no bindings of its own.
/// </summary>
public static class PadInput
{
    public const string MoveLeft = "pad_move_left";
    public const string MoveRight = "pad_move_right";
    public const string MoveUp = "pad_move_up";
    public const string MoveDown = "pad_move_down";
    public const string LookLeft = "pad_look_left";
    public const string LookRight = "pad_look_right";
    public const string LookUp = "pad_look_up";
    public const string LookDown = "pad_look_down";
    public const string TurnLeft = "pad_l1";
    public const string TurnRight = "pad_r1";
    public const string CameraBehind = "pad_l2";
    public const string Confirm = "pad_confirm";
    public const string Walk = "pad_walk";

    /// <summary>Raw stick offset (of 128) below which an axis reads zero.</summary>
    private const float DeadZone = 50.0f;

    /// <summary>Raw stick travel (of 128) from the dead zone edge to full deflection.</summary>
    private const float Travel = 78.0f;

    private static bool _registered;

    /// <summary>Adds the pad actions to the <see cref="InputMap"/> unless they exist already.</summary>
    public static void EnsureActions()
    {
        if (_registered)
            return;
        _registered = true;

        // Stick axes keep their full range here; the game's dead zone is applied in Read.
        Add(MoveLeft, Key.A, Key.Left, JoyAxis.LeftX, -1);
        Add(MoveRight, Key.D, Key.Right, JoyAxis.LeftX, 1);
        Add(MoveUp, Key.W, Key.Up, JoyAxis.LeftY, -1);
        Add(MoveDown, Key.S, Key.Down, JoyAxis.LeftY, 1);
        Add(LookLeft, Key.J, null, JoyAxis.RightX, -1);
        Add(LookRight, Key.L, null, JoyAxis.RightX, 1);
        Add(LookUp, Key.I, null, JoyAxis.RightY, -1);
        Add(LookDown, Key.K, null, JoyAxis.RightY, 1);
        Add(TurnLeft, Key.Q, null, JoyButton.LeftShoulder);
        Add(TurnRight, Key.E, null, JoyButton.RightShoulder);
        Add(CameraBehind, Key.C, null, null, axis: JoyAxis.TriggerLeft);
        Add(Confirm, Key.Enter, Key.Space, JoyButton.A);
        Add(Walk, Key.Shift, null, null);
    }

    /// <summary>The local controller now. Shift halves the keyboard stick so walking is reachable.</summary>
    public static PadState Read()
    {
        EnsureActions();
        float walk = Input.IsActionPressed(Walk) ? 0.5f : 1.0f;
        return new PadState(
            Axis(MoveLeft, MoveRight) * walk,
            Axis(MoveUp, MoveDown) * walk,
            Axis(LookLeft, LookRight),
            Axis(LookUp, LookDown),
            Input.IsActionPressed(TurnLeft),
            Input.IsActionPressed(TurnRight),
            Input.IsActionPressed(CameraBehind),
            Input.IsActionPressed(Confirm));
    }

    /// <summary>
    /// The game's stick calibration on a [-1, 1] axis: offsets under 50 of 128 read zero, and
    /// the remaining 78 units of travel are stretched to the full range.
    /// </summary>
    public static float Calibrate(float value)
    {
        float raw = Mathf.Abs(value) * 128.0f;
        if (raw < DeadZone)
            return 0.0f;
        return Mathf.Sign(value) * Mathf.Min((raw - DeadZone) / Travel, 1.0f);
    }

    private static float Axis(string negative, string positive) =>
        Calibrate(Input.GetActionStrength(positive) - Input.GetActionStrength(negative));

    private static void Add(string action, Key key, Key? alternate, JoyAxis stick, int direction)
    {
        if (!Register(action, key, alternate))
            return;
        InputMap.ActionAddEvent(action, new InputEventJoypadMotion { Axis = stick, AxisValue = direction });
    }

    private static void Add(string action, Key key, Key? alternate, JoyButton? button, JoyAxis? axis = null)
    {
        if (!Register(action, key, alternate))
            return;
        if (button is { } joyButton)
            InputMap.ActionAddEvent(action, new InputEventJoypadButton { ButtonIndex = joyButton });
        if (axis is { } trigger)
            InputMap.ActionAddEvent(action, new InputEventJoypadMotion { Axis = trigger, AxisValue = 1.0f });
    }

    private static bool Register(string action, Key key, Key? alternate)
    {
        if (InputMap.HasAction(action))
            return false;
        InputMap.AddAction(action, 0.0f);
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
        if (alternate is { } second)
            InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = second });
        return true;
    }
}
