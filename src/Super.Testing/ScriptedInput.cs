using System.Numerics;

namespace Super.Testing;

/// <summary>
/// Input a test plays into a headless game. Everything queued lands on the next tick,
/// through the same path the window uses, so a tap or a hold behaves exactly as it
/// would for a player. Actions can be played directly too, so rebinding doesn't
/// break tests.
/// </summary>
public sealed class ScriptedInput
{
    private readonly Input _input;
    private readonly Func<Type, object> _resolve;
    private readonly InputScript _tape = new();
    private readonly float[] _axes = NewAxes();
    private readonly bool[] _buttons = new bool[Input.GamepadButtonCount];

    internal ScriptedInput(Input input, Func<Type, object> resolve)
    {
        _input = input;
        _resolve = resolve;
    }

    /// <summary>Sticks centred and triggers released, which a pad reports as -1.</summary>
    private static float[] NewAxes()
    {
        float[] axes = new float[Input.GamepadAxisCount];
        axes[4] = -1f;
        axes[5] = -1f;
        return axes;
    }

    /// <summary>A tap: down and up before the next tick, so it's pressed on that tick and released on the one after.</summary>
    public void Press(Key key)
    {
        Hold(key);
        Release(key);
    }

    /// <summary>Holds the key down until <see cref="Release(Key)"/>.</summary>
    public void Hold(Key key) => Queue(input => input.KeyDown(key));

    public void Release(Key key) => Queue(input => input.KeyUp(key));

    public void Press(MouseButton button)
    {
        Hold(button);
        Release(button);
    }

    public void Hold(MouseButton button) => Queue(input => input.MouseButtonDown(button));

    public void Release(MouseButton button) => Queue(input => input.MouseButtonUp(button));

    /// <summary>Takes the cursor off the window.</summary>
    public void LeaveWindow() => Queue(input => input.MouseLeftWindow());

    public void Press(GamepadButton button)
    {
        Hold(button);
        Release(button);
    }

    public void Hold(GamepadButton button) => Queue(input =>
    {
        _buttons[(int)button] = true;
        input.Gamepad(true, _axes, _buttons);
    });

    public void Release(GamepadButton button) => Queue(input =>
    {
        _buttons[(int)button] = false;
        input.Gamepad(true, _axes, _buttons);
    });

    /// <summary>Puts the cursor here, in the pixels the interface lays itself out in.</summary>
    public void MoveMouse(Vector2 position) => Queue(input => input.MouseMoved(position));

    /// <summary>Clicks a button, after moving the cursor if a position is given.</summary>
    public void Click(MouseButton button, Vector2? at = null)
    {
        if (at is { } position)
        {
            MoveMouse(position);
        }

        Press(button);
    }

    public void Scroll(float notches) => Queue(input => input.Scrolled(notches));

    public void Type(string text) => Queue(input =>
    {
        foreach (char character in text)
        {
            input.TypedCharacter(character);
        }
    });

    /// <summary>Holds a stick here until set again. Forward is positive y, and the dead zone applies as for a real pad.</summary>
    public void Stick(GamepadStick stick, Vector2 value) => Queue(input =>
    {
        int first = (int)stick * 2;
        _axes[first] = value.X;
        _axes[first + 1] = -value.Y;
        input.Gamepad(true, _axes, _buttons);
    });

    /// <summary>Holds a trigger at this pull, from 0 to 1, until set again.</summary>
    public void Trigger(GamepadTrigger trigger, float value) => Queue(input =>
    {
        _axes[4 + (int)trigger] = value * 2f - 1f;
        input.Gamepad(true, _axes, _buttons);
    });

    /// <summary>Performs the action for exactly one tick, whatever it's bound to.</summary>
    public void Press<TControl>(TControl control)
        where TControl : struct, Enum
    {
        var actions = Actions<TControl>();
        Queue(_ => actions.Override(control, Vector2.Zero, button: true));
        Queue(_ => actions.ClearOverride(control), ticksFromNow: 1);
    }

    /// <summary>Keeps the action going until <see cref="Release{TControl}"/>.</summary>
    public void Hold<TControl>(TControl control)
        where TControl : struct, Enum
    {
        var actions = Actions<TControl>();
        Queue(_ => actions.Override(control, Vector2.Zero, button: true));
    }

    /// <summary>Keeps the action at this vector until <see cref="Release{TControl}"/>.</summary>
    public void Hold<TControl>(TControl control, Vector2 vector)
        where TControl : struct, Enum
    {
        var actions = Actions<TControl>();
        Queue(_ => actions.Override(control, vector, button: false));
    }

    public void Release<TControl>(TControl control)
        where TControl : struct, Enum
    {
        var actions = Actions<TControl>();
        Queue(_ => actions.ClearOverride(control));
    }

    /// <summary>Feeds everything due on this tick into the game's input, just before the tick rolls.</summary>
    internal void Play(long tick) => _tape.Play(tick, _input);

    private void Queue(Action<Input> apply, long ticksFromNow = 0) => _tape.Then(apply, ticksFromNow);

    private Actions<TControl> Actions<TControl>()
        where TControl : struct, Enum =>
        (Actions<TControl>)_resolve(typeof(Actions<TControl>));
}
