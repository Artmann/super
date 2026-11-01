using System.Numerics;
using System.Text;

namespace Super;

/// <summary>Told whenever the input snapshot rolls over to a new tick.</summary>
internal interface IInputListener
{
    void Rolled();
}

/// <summary>
/// What the player did, frozen for one tick. The window and headless runs feed it
/// between ticks; Update reads it. Draw and Ui can't, so the picture stays a function
/// of state and a seed replays the same way.
/// </summary>
public sealed class Input
{
    internal const int KeyCount = (int)Key.Menu + 1;
    internal const int MouseButtonCount = 8;
    internal const int GamepadButtonCount = 15;
    internal const int GamepadAxisCount = 6;
    private const string NotReadable = "Input can only be read in Update. Draw and Ui describe state, so they can't look at input.";

    private readonly ButtonSet _keys = new(KeyCount);
    private readonly ButtonSet _mouseButtons = new(MouseButtonCount);
    private readonly ButtonSet _gamepadButtons = new(GamepadButtonCount);
    private readonly float[] _pendingAxes = RestingAxes();
    private readonly float[] _axes = RestingAxes();
    private readonly List<IInputListener> _listeners = [];
    private readonly StringBuilder _typing = new();

    private Vector2 _pendingPosition;
    private Vector2 _pendingDelta;
    private float _pendingScroll;
    private bool _hasPosition;
    private bool _pendingConnected;

    /// <summary>How far a stick must move before it counts, as a fraction of its travel.</summary>
    public float DeadZone { get; set; } = 0.2f;

    /// <summary>Set in Update to hide the cursor and lock it to the window, for looking around with the mouse.</summary>
    public bool CaptureMouse { get; set; }

    /// <summary>Where the cursor is, in the same pixels the interface lays itself out in.</summary>
    public Vector2 MousePosition { get => Read(field); private set; }

    /// <summary>How far the cursor moved this tick.</summary>
    public Vector2 MouseDelta { get => Read(field); private set; }

    /// <summary>Wheel notches turned this tick, positive away from the player.</summary>
    public float Scroll { get => Read(field); private set; }

    /// <summary>The characters typed this tick, for text fields.</summary>
    public string Typed { get => Read(field); private set; } = string.Empty;

    public bool GamepadConnected { get => Read(field); private set; }

    /// <summary>Whether reads are allowed right now. The host turns it on for Update only.</summary>
    internal bool Readable { get; set; }

    /// <summary>How many ticks the snapshot has rolled over.</summary>
    internal int RollCount { get; private set; }

    /// <summary>Whether the key is held during this tick.</summary>
    public bool Down(Key key) => Read(_keys.Down((int)key));

    /// <summary>Whether the key went down since the last tick.</summary>
    public bool Pressed(Key key) => Read(_keys.Pressed((int)key));

    /// <summary>Whether the key went up since the last tick.</summary>
    public bool Released(Key key) => Read(_keys.Released((int)key));

    public bool Down(MouseButton button) => Read(_mouseButtons.Down((int)button));

    public bool Pressed(MouseButton button) => Read(_mouseButtons.Pressed((int)button));

    public bool Released(MouseButton button) => Read(_mouseButtons.Released((int)button));

    public bool Down(GamepadButton button) => Read(_gamepadButtons.Down((int)button));

    public bool Pressed(GamepadButton button) => Read(_gamepadButtons.Pressed((int)button));

    public bool Released(GamepadButton button) => Read(_gamepadButtons.Released((int)button));

    /// <summary>A stick's position, from -1 to 1 on each axis with forward positive, after the dead zone.</summary>
    public Vector2 Stick(GamepadStick stick)
    {
        CheckReadable();
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
        CheckReadable();
        return (_axes[4 + (int)trigger] + 1f) * 0.5f;
    }

    internal void CheckReadable()
    {
        if (!Readable)
        {
            throw new InvalidOperationException(NotReadable);
        }
    }

    internal void Listen(IInputListener listener) => _listeners.Add(listener);

    internal void KeyDown(Key key) => _keys.Press((int)key);

    internal void KeyUp(Key key) => _keys.Release((int)key);

    internal void MouseButtonDown(MouseButton button) => _mouseButtons.Press((int)button);

    internal void MouseButtonUp(MouseButton button) => _mouseButtons.Release((int)button);

    internal void MouseMoved(Vector2 position)
    {
        if (_hasPosition)
        {
            _pendingDelta += position - _pendingPosition;
        }

        _pendingPosition = position;
        _hasPosition = true;
    }

    internal void Scrolled(float notches) => _pendingScroll += notches;

    internal void TypedCharacter(char character) => _typing.Append(character);

    /// <summary>
    /// The gamepad as it is now. Axes come in GLFW's order and ranges: left x and y,
    /// right x and y, then the triggers from -1 to 1. Buttons are in <see cref="GamepadButton"/> order.
    /// </summary>
    internal void Gamepad(bool connected, ReadOnlySpan<float> axes, ReadOnlySpan<bool> buttons)
    {
        _pendingConnected = connected;
        for (int i = 0; i < GamepadAxisCount; i++)
        {
            float centred = i >= 4 ? -1f : 0f;
            _pendingAxes[i] = connected ? axes[i] : centred;
        }

        for (int i = 0; i < GamepadButtonCount; i++)
        {
            if (connected && buttons[i])
            {
                _gamepadButtons.Press(i);
            }
            else
            {
                _gamepadButtons.Release(i);
            }
        }
    }

    /// <summary>Turns everything that happened since the last tick into this tick's snapshot.</summary>
    internal void Roll()
    {
        _keys.Roll();
        _mouseButtons.Roll();
        _gamepadButtons.Roll();
        _pendingAxes.CopyTo(_axes, 0);
        GamepadConnected = _pendingConnected;

        MousePosition = _pendingPosition;
        MouseDelta = _pendingDelta;
        Scroll = _pendingScroll;
        _pendingDelta = Vector2.Zero;
        _pendingScroll = 0f;

        Typed = _typing.Length == 0 ? string.Empty : _typing.ToString();
        _typing.Clear();

        RollCount++;
        foreach (var listener in _listeners)
        {
            listener.Rolled();
        }
    }

    private T Read<T>(T value)
    {
        CheckReadable();
        return value;
    }

    /// <summary>Sticks centred and triggers at rest, which GLFW reports as -1.</summary>
    private static float[] RestingAxes() => [0f, 0f, 0f, 0f, -1f, -1f];

    /// <summary>A set of buttons with the same press, hold and release rules.</summary>
    private sealed class ButtonSet(int count)
    {
        private readonly State[] _states = new State[count];

        public bool Down(int index) => _states[index].Down;

        public bool Pressed(int index) => _states[index].Pressed;

        public bool Released(int index) => _states[index].Released;

        /// <summary>A down event. Repeats while already down are ignored.</summary>
        public void Press(int index)
        {
            ref var state = ref _states[index];
            if (state.Physical)
            {
                return;
            }

            state.Physical = true;
            state.WentDown = true;
        }

        public void Release(int index)
        {
            ref var state = ref _states[index];
            if (!state.Physical)
            {
                return;
            }

            state.Physical = false;
            state.WentUp = true;
        }

        public void Roll()
        {
            foreach (ref var state in _states.AsSpan())
            {
                state.Pressed = state.WentDown;
                // A tap owed a release from the last tick still gets it, even if the button went down again.
                if (state.Physical)
                {
                    state.Down = true;
                    state.Released = state.WentUp || state.PendingRelease;
                    state.PendingRelease = false;
                }
                else if (state.WentDown)
                {
                    // A tap inside one tick: held for this tick, let go on the next.
                    state.Down = true;
                    state.Released = state.PendingRelease;
                    state.PendingRelease = true;
                }
                else
                {
                    state.Down = false;
                    state.Released = state.WentUp || state.PendingRelease;
                    state.PendingRelease = false;
                }

                state.WentDown = false;
                state.WentUp = false;
            }
        }

        private struct State
        {
            public bool Physical;
            public bool Down;
            public bool Pressed;
            public bool Released;
            public bool PendingRelease;
            public bool WentDown;
            public bool WentUp;
        }
    }
}
