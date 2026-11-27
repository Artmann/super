using System.Numerics;

namespace Super;

/// <summary>
/// The gamepad's sticks and triggers as Update sees them, frozen for one tick. Reach it
/// through <c>input.Gamepad</c>. Its buttons are read like keys, with
/// <c>input.Down(GamepadButton.South)</c>.
/// </summary>
public sealed class GamepadInput
{
    internal const int ButtonCount = 15;
    internal const int AxisCount = 6;

    private readonly InputTick _tick;
    private readonly float[] _pendingAxes = RestingAxes();
    private readonly float[] _axes = RestingAxes();
    private bool _pendingConnected;
    private string? _pendingName;

    internal GamepadInput(InputTick tick)
    {
        _tick = tick;
    }

    public bool Connected { get => _tick.Read(field); private set; }

    /// <summary>What the pad calls itself, such as "Xbox Controller", or null when none is connected or it gave no name.</summary>
    public string? Name { get => _tick.Read(field); private set; }

    /// <summary>Whose pad it is, from its <see cref="Name"/>, which says what its buttons are called.</summary>
    public GamepadFamily Family { get => _tick.Read(field); private set; } = GamepadFamily.Other;

    /// <summary>The family of the pad as polled so far, ahead of the next tick, for the interface to draw prompts with.</summary>
    internal GamepadFamily PendingFamily { get; private set; } = GamepadFamily.Other;

    /// <summary>How far a stick must move before it counts, as a fraction of its travel.</summary>
    public float DeadZone { get; set; } = 0.2f;

    internal ButtonSet Buttons { get; } = new(ButtonCount);

    /// <summary>When set, the next tick sees the left stick centred, because the interface is steering with it.</summary>
    internal bool LeftStickTaken { get; set; }

    /// <summary>The left stick as polled since the last tick, before the dead zone, with forward positive.</summary>
    internal Vector2 PendingLeftStick => new(_pendingAxes[0], -_pendingAxes[1]);

    /// <summary>A stick's position, from -1 to 1 on each axis with forward positive, after the dead zone.</summary>
    public Vector2 Stick(GamepadStick stick)
    {
        _tick.Check();
        int first = (int)stick * 2;
        var raw = new Vector2(_axes[first], -_axes[first + 1]);
        float length = raw.Length();
        if (length <= DeadZone)
        {
            return Vector2.Zero;
        }

        float scaled = Math.Min((length - DeadZone) / (1f - DeadZone), 1f);
        return raw * (scaled / length);
    }

    /// <summary>How far a trigger is pulled, from 0 to 1.</summary>
    public float Trigger(GamepadTrigger trigger)
    {
        _tick.Check();
        return (_axes[4 + (int)trigger] + 1f) * 0.5f;
    }

    /// <summary>
    /// The gamepad as it is now. Axes come in GLFW's order and ranges: left x and y,
    /// right x and y, then the triggers from -1 to 1. Buttons are in <see cref="GamepadButton"/> order.
    /// A button going down, or a stick or trigger past halfway, makes the gamepad the device in use.
    /// </summary>
    internal void Polled(bool connected, ReadOnlySpan<float> axes, ReadOnlySpan<bool> buttons, string? name = null)
    {
        _pendingConnected = connected;
        if (_pendingName != name || !connected)
        {
            _pendingName = connected ? name : null;
            PendingFamily = Devices.FamilyOf(_pendingName);
        }

        for (int i = 0; i < AxisCount; i++)
        {
            float centred = i >= 4 ? -1f : 0f;
            _pendingAxes[i] = connected ? axes[i] : centred;
        }

        if (connected && Devices.Pushed(axes))
        {
            _tick.PendingDevice = InputDevice.Gamepad;
        }

        PollButtons(connected, buttons);
    }

    private void PollButtons(bool connected, ReadOnlySpan<bool> buttons)
    {
        for (int i = 0; i < ButtonCount; i++)
        {
            if (connected && buttons[i])
            {
                if (!Buttons.Physical(i))
                {
                    _tick.PendingDevice = InputDevice.Gamepad;
                }

                Buttons.Press(i);
            }
            else
            {
                Buttons.Release(i);
            }
        }
    }

    /// <summary>Turns what was polled since the last tick into this tick's snapshot.</summary>
    internal void Roll()
    {
        Buttons.Roll();
        _pendingAxes.CopyTo(_axes, 0);
        if (LeftStickTaken)
        {
            _axes[0] = 0f;
            _axes[1] = 0f;
        }

        Connected = _pendingConnected;
        Name = _pendingName;
        Family = PendingFamily;
    }

    /// <summary>Sticks centred and triggers at rest, which GLFW reports as -1.</summary>
    private static float[] RestingAxes() => [0f, 0f, 0f, 0f, -1f, -1f];
}
