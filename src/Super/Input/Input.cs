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
/// of state and a seed replays the same way. Keys and every button are read here; the
/// cursor and wheel are on <see cref="Mouse"/>, and the sticks and triggers on <see cref="Gamepad"/>.
/// </summary>
public sealed class Input
{
    internal const int KeyCount = (int)Key.Menu + 1;
    internal const int MouseButtonCount = MouseFeed.ButtonCount;
    internal const int GamepadButtonCount = GamepadInput.ButtonCount;
    internal const int GamepadAxisCount = GamepadInput.AxisCount;

    private readonly InputTick _tick = new();
    private readonly ButtonSet _keys = new(KeyCount);
    private readonly List<IInputListener> _listeners = [];
    private readonly StringBuilder _typing = new();

    public Input()
    {
        Mouse = new MouseInput(_tick);
        Gamepad = new GamepadInput(_tick);
    }

    /// <summary>The cursor, the wheel, and how the system shows the cursor.</summary>
    public MouseInput Mouse { get; }

    /// <summary>The gamepad's sticks and triggers, and whether one is connected.</summary>
    public GamepadInput Gamepad { get; }

    /// <summary>The characters typed this tick, for text fields.</summary>
    public string Typed { get => _tick.Read(field); private set; } = string.Empty;

    /// <summary>
    /// The size of the screen in the same pixels as the cursor and the interface, for placing interface
    /// over things in the world. The host sets it each tick.
    /// </summary>
    public Vector2 ScreenSize { get => _tick.ScreenSize; internal set => _tick.ScreenSize = value; }

    /// <summary>Whether reads are allowed right now. The host turns it on for Update only.</summary>
    internal bool Readable { get => _tick.Readable; set => _tick.Readable = value; }

    /// <summary>Whether the key is held during this tick.</summary>
    public bool Down(Key key) => _tick.Read(_keys.Down((int)key));

    /// <summary>Whether the key went down since the last tick.</summary>
    public bool Pressed(Key key) => _tick.Read(_keys.Pressed((int)key));

    /// <summary>Whether the key went up since the last tick.</summary>
    public bool Released(Key key) => _tick.Read(_keys.Released((int)key));

    public bool Down(MouseButton button) => _tick.Read(Mouse.Feed.Buttons.Down((int)button));

    public bool Pressed(MouseButton button) => _tick.Read(Mouse.Feed.Buttons.Pressed((int)button));

    public bool Released(MouseButton button) => _tick.Read(Mouse.Feed.Buttons.Released((int)button));

    public bool Down(GamepadButton button) => _tick.Read(Gamepad.Buttons.Down((int)button));

    public bool Pressed(GamepadButton button) => _tick.Read(Gamepad.Buttons.Pressed((int)button));

    public bool Released(GamepadButton button) => _tick.Read(Gamepad.Buttons.Released((int)button));

    internal void CheckReadable() => _tick.Check();

    internal void Listen(IInputListener listener) => _listeners.Add(listener);

    /// <summary>Whether the key is down right now, between ticks, for the interface to read as a press happens.</summary>
    internal bool Held(Key key) => _keys.Physical((int)key);

    internal void KeyDown(Key key) => _keys.Press((int)key);

    internal void KeyUp(Key key) => _keys.Release((int)key);

    internal void TypedCharacter(char character) => _typing.Append(character);

    /// <summary>Turns everything that happened since the last tick into this tick's snapshot.</summary>
    internal void Roll()
    {
        _keys.Roll();
        Mouse.Roll();
        Gamepad.Roll();

        Typed = _typing.Length == 0 ? string.Empty : _typing.ToString();
        _typing.Clear();

        foreach (var listener in _listeners)
        {
            listener.Rolled();
        }
    }
}

/// <summary>
/// What <c>input.Mouse.Cursor</c> shows: an <see cref="Image"/> drawn <see cref="Size"/> interface units
/// square, with the mouse at <see cref="Hotspot"/>, in the image's own pixels from its top left.
/// </summary>
public sealed record CursorLook(Ui.GameImage Image, float Size, System.Numerics.Vector2 Hotspot);
