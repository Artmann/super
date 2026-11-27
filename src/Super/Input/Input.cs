using System.Numerics;

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

    public Input()
    {
        Mouse = new MouseInput(_tick);
        Gamepad = new GamepadInput(_tick);
        Touches = new TouchInput(_tick);
        Keys = new KeyFeed(_tick);
        Window = new WindowInput(_tick);
    }

    /// <summary>
    /// What the player last played with, for showing the buttons in their hand. It changes on a key, a mouse
    /// button, the wheel or a real move of the mouse, a gamepad button or a stick or trigger past halfway, and
    /// a finger landing.
    /// </summary>
    public InputDevice Device { get => _tick.Read(field); private set; }

    /// <summary>The device in use as fed so far, ahead of the next tick, for the interface to draw prompts with.</summary>
    internal InputDevice PendingDevice => _tick.PendingDevice;

    /// <summary>The cursor, the wheel, and how the system shows the cursor.</summary>
    public MouseInput Mouse { get; }

    /// <summary>The gamepad's sticks and triggers, and whether one is connected.</summary>
    public GamepadInput Gamepad { get; }

    /// <summary>The fingers on the screen. Those that land on the interface belong to it and aren't here.</summary>
    public TouchInput Touches { get; }

    /// <summary>Whether the game's window has focus, for pausing while the player is in another window.</summary>
    public WindowInput Window { get; }

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
    public bool Down(Key key) => _tick.Read(Keys.Buttons.Down((int)key));

    /// <summary>Whether the key went down since the last tick.</summary>
    public bool Pressed(Key key) => _tick.Read(Keys.Buttons.Pressed((int)key));

    /// <summary>Whether the key went up since the last tick.</summary>
    public bool Released(Key key) => _tick.Read(Keys.Buttons.Released((int)key));

    public bool Down(MouseButton button) => _tick.Read(Mouse.Feed.Buttons.Down((int)button));

    public bool Pressed(MouseButton button) => _tick.Read(Mouse.Feed.Buttons.Pressed((int)button));

    public bool Released(MouseButton button) => _tick.Read(Mouse.Feed.Buttons.Released((int)button));

    public bool Down(GamepadButton button) => _tick.Read(Gamepad.Buttons.Down((int)button));

    public bool Pressed(GamepadButton button) => _tick.Read(Gamepad.Buttons.Pressed((int)button));

    public bool Released(GamepadButton button) => _tick.Read(Gamepad.Buttons.Released((int)button));

    internal void CheckReadable() => _tick.Check();

    /// <summary>What hears each tick roll over: every <see cref="Actions{TControl}"/> adds itself.</summary>
    internal List<IInputListener> Listeners { get; } = [];

    /// <summary>The keyboard as it's fed, before a tick reads it.</summary>
    internal KeyFeed Keys { get; }

    internal void KeyDown(Key key) => Keys.Down(key);

    internal void KeyUp(Key key) => Keys.Up(key);

    /// <summary>Turns everything that happened since the last tick into this tick's snapshot.</summary>
    internal void Roll()
    {
        Typed = Keys.Roll();
        Device = _tick.PendingDevice;
        Mouse.Roll();
        Gamepad.Roll();
        Touches.Roll();
        Window.Roll();

        foreach (var listener in Listeners)
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
