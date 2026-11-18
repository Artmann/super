using System.Numerics;

namespace Super;

/// <summary>
/// The mouse as Update sees it, frozen for one tick: where the cursor is, how far it moved,
/// the wheel, and how the system shows the cursor. Reach it through <c>input.Mouse</c>. Its
/// buttons are read like keys, with <c>input.Down(MouseButton.Left)</c>.
/// </summary>
public sealed class MouseInput
{
    private readonly InputTick _tick;

    internal MouseInput(InputTick tick)
    {
        _tick = tick;
    }

    /// <summary>Set in Update to hide the cursor and lock it to the window, for looking around with the mouse.</summary>
    public bool Locked { get; set; }

    /// <summary>
    /// Set to hide the system cursor over the window while leaving it free, for a game that draws its own
    /// pointer in the interface. <see cref="Locked"/> hides it anyway.
    /// </summary>
    public bool HideCursor { get; set; }

    /// <summary>
    /// A picture the system draws as the cursor in place of its arrow, or null for the arrow. The system moves
    /// it with the mouse itself, between frames, so it never lags behind the hand the way a pointer drawn in
    /// the interface does on a slow frame. <see cref="HideCursor"/> and <see cref="Locked"/> still hide it.
    /// </summary>
    public CursorLook? Cursor { get; set; }

    /// <summary>Where the cursor is, in the same pixels the interface lays itself out in.</summary>
    public Vector2 Position { get => _tick.Read(field); private set; }

    /// <summary>How far the cursor moved this tick.</summary>
    public Vector2 Delta { get => _tick.Read(field); private set; }

    /// <summary>
    /// Whether the mouse belongs to the interface this tick: it's over something solid, or
    /// a press began there. Mouse buttons and the wheel read as idle then.
    /// </summary>
    public bool OverUi { get => _tick.Read(field); private set; }

    /// <summary>Wheel notches turned this tick, positive away from the player.</summary>
    public float Scroll { get => _tick.Read(field); private set; }

    /// <summary>What the window sent since the last tick, waiting for the next one.</summary>
    internal MouseFeed Feed { get; } = new();

    /// <summary>The line from the camera through the cursor, for working out what was clicked.</summary>
    public Ray Ray(in Camera camera) => camera.Ray(Position, _tick.ScreenSize);

    /// <summary>Turns what the window sent since the last tick into this tick's snapshot.</summary>
    internal void Roll()
    {
        Feed.Buttons.Roll();
        OverUi = Feed.PendingMouseOverUi;
        (Position, Delta, Scroll) = Feed.TakeMotion();
    }
}

/// <summary>
/// The mouse between ticks: what the window sent, held until the next tick. When the
/// interface is on, events wait in <see cref="MouseEvents"/> for it to pass on what it
/// doesn't take.
/// </summary>
internal sealed class MouseFeed
{
    internal const int ButtonCount = 8;

    private Vector2 _position;
    private Vector2 _delta;
    private float _scroll;
    private bool _hasPosition;

    public ButtonSet Buttons { get; } = new(ButtonCount);

    /// <summary>When set, the mouse waits in <see cref="MouseEvents"/> for the interface to pass on what it doesn't take.</summary>
    public bool RoutesMouse { get; set; }

    /// <summary>What the mouse did that the interface hasn't seen yet, in order.</summary>
    public List<MouseEvent> MouseEvents { get; } = [];

    /// <summary>Whether the mouse belongs to the interface on the next tick.</summary>
    public bool PendingMouseOverUi { get; set; }

    public void MouseButtonDown(MouseButton button)
    {
        if (RoutesMouse)
        {
            MouseEvents.Add(new MouseEvent(MouseEventKind.Down, button, default, 0f));
        }
        else
        {
            ApplyMouseButton(button, true);
        }
    }

    public void MouseButtonUp(MouseButton button)
    {
        if (RoutesMouse)
        {
            MouseEvents.Add(new MouseEvent(MouseEventKind.Up, button, default, 0f));
        }
        else
        {
            ApplyMouseButton(button, false);
        }
    }

    /// <summary>A button event the interface didn't take, or no interface is there to take.</summary>
    public void ApplyMouseButton(MouseButton button, bool down)
    {
        if (down)
        {
            Buttons.Press((int)button);
        }
        else
        {
            Buttons.Release((int)button);
        }
    }

    /// <summary>A turn of the wheel the interface didn't take.</summary>
    public void ApplyScroll(float notches) => _scroll += notches;

    public void MouseMoved(Vector2 position)
    {
        if (_hasPosition)
        {
            _delta += position - _position;
        }

        _position = position;
        _hasPosition = true;
        if (RoutesMouse)
        {
            MouseEvents.Add(new MouseEvent(MouseEventKind.Moved, default, position, 0f));
        }
    }

    /// <summary>The cursor went off the window, so it's over nothing until it comes back.</summary>
    public void MouseLeftWindow()
    {
        if (RoutesMouse)
        {
            MouseEvents.Add(new MouseEvent(MouseEventKind.Left, default, default, 0f));
        }
    }

    public void Scrolled(float notches)
    {
        if (RoutesMouse)
        {
            MouseEvents.Add(new MouseEvent(MouseEventKind.Wheel, default, default, notches));
        }
        else
        {
            ApplyScroll(notches);
        }
    }

    /// <summary>Where the cursor is, and how far it moved and the wheel turned since the last take, which starts both again.</summary>
    public (Vector2 Position, Vector2 Delta, float Scroll) TakeMotion()
    {
        var taken = (_position, _delta, _scroll);
        _delta = Vector2.Zero;
        _scroll = 0f;
        return taken;
    }
}

internal enum MouseEventKind
{
    Moved,
    Down,
    Up,
    Wheel,
    Left,
}

/// <summary>One thing the mouse did, kept in order for the interface to look at first.</summary>
internal readonly record struct MouseEvent(MouseEventKind Kind, MouseButton Button, Vector2 Position, float Notches);
