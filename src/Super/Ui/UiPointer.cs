using System.Numerics;

namespace Super.Ui;

/// <summary>
/// Everything the mouse does to the interface. What only the eyes care about, such as
/// hover and scrolling, happens as soon as a frame sees it. What changes the game waits
/// for <see cref="Deliver"/>, the one place in a tick a callback may run. Whatever the
/// interface doesn't take carries on to <see cref="Input"/>, in the order it happened.
/// </summary>
internal sealed class UiPointer(UiTree tree, Input input, int ticksPerSecond)
{
    private const int Left = 1 << (int)MouseButton.Left;

    private readonly List<Action> _callbacks = [];
    private readonly UiHover _hover = new(tree, ticksPerSecond);
    private readonly UiGestures _gestures = new(tree, input);
    private readonly UiTouch _touch = new(tree, ticksPerSecond);
    private UiFocus? _focus;

    private Vector2 _position;
    private bool _inWindow;
    private int _uiHeld;
    private int _gameHeld;

    /// <summary>The keyboard and the gamepad, which share the callbacks so everything lands in the order it happened.</summary>
    private UiFocus Focus => _focus ??= new UiFocus(tree, input, _callbacks, ticksPerSecond);

    /// <summary>Runs before the tick rolls, so the tick sees everything up to now.</summary>
    public void BeforeRoll()
    {
        Pump();
        Focus.Tick();
        if (_touch.Tick() is { } longPress)
        {
            _callbacks.Add(longPress);
        }

        _hover.Tick();
        input.Mouse.Feed.PendingMouseOverUi = _uiHeld != 0 || (_gameHeld == 0 && !input.Mouse.Locked && Hit() is not null);
    }

    /// <summary>Handles what the mouse did since the last time this ran.</summary>
    public void Pump()
    {
        Focus.PumpKeys();
        foreach (var mouse in input.Mouse.Feed.MouseEvents)
        {
            switch (mouse.Kind)
            {
                case MouseEventKind.Moved:
                    // The mouse is in pixels and the interface in its own units.
                    Moved(mouse.Position / tree.Scale);
                    break;
                case MouseEventKind.Down:
                    Down(mouse.Button);
                    break;
                case MouseEventKind.Up:
                    Up(mouse.Button);
                    break;
                case MouseEventKind.Left:
                    _inWindow = false;
                    break;
                default:
                    Wheel(mouse.Notches);
                    break;
            }
        }

        input.Mouse.Feed.MouseEvents.Clear();
        foreach (var touch in input.Touches.Feed.Events)
        {
            _touch.Route(touch, input.Touches.Feed, Focus, _hover, _callbacks);
        }

        input.Touches.Feed.Events.Clear();
    }

    /// <summary>Runs the callbacks owed, in the order they happened. Update comes right after.</summary>
    public void Deliver()
    {
        _gestures.TakeSlide()?.Invoke();
        _touch.TakeSlide()?.Invoke();
        foreach (var callback in _callbacks)
        {
            callback();
        }

        _callbacks.Clear();
    }

    /// <summary>
    /// Works out how things look under the cursor, from the layout that's about to be
    /// drawn, so nothing lags. Returns whether that needs another update.
    /// </summary>
    public bool Refresh()
    {
        _gestures.ForgetUnmounted();
        _touch.ForgetUnmounted();
        var hit = (_gameHeld & Left) != 0 || input.Mouse.Locked ? null : Hit();
        bool leftHeld = (_uiHeld & Left) != 0;
        // A finger holding something shows it pressed, and otherwise the mouse says what looks hovered.
        var (look, pressed) = _touch.Pressed is { } touched ? (touched, true) : _gestures.LookAt(hit, leftHeld);
        bool changed = _hover.ShowPress(look, pressed);
        changed |= _hover.RefreshBar(_gestures.HeldBar, hit, _position, leftHeld);
        _hover.FollowTip(hit);
        return changed;
    }

    /// <summary>Draws the drag preview and the tooltip, above everything.</summary>
    public void ShowOverlays() => tree.ShowOverlays(_gestures.Preview, _hover.Tooltip(_uiHeld != 0), _position);

    private UiNode? Hit() => _inWindow ? tree.Root?.Hit(_position.X, _position.Y) : null;

    private void Moved(Vector2 position)
    {
        _position = position;
        _inWindow = true;
        if (_gestures.Follow(position))
        {
            _hover.HideTip();
        }
    }

    private void Down(MouseButton button)
    {
        int bit = 1 << (int)button;
        var hit = input.Mouse.Locked ? null : Hit();
        Focus.Pointed(hit, _position.X);
        if (hit is null)
        {
            _gameHeld |= bit;
            input.Mouse.Feed.ApplyMouseButton(button, true);
            return;
        }

        _uiHeld |= bit;
        _hover.HideTip();
        _gestures.Press(hit, button, _position);
    }

    private void Up(MouseButton button)
    {
        int bit = 1 << (int)button;
        if ((_uiHeld & bit) == 0)
        {
            _gameHeld &= ~bit;
            input.Mouse.Feed.ApplyMouseButton(button, false);
            return;
        }

        _uiHeld &= ~bit;
        _gestures.ForgetUnmounted();
        if (_gestures.Release(Hit(), button) is { } callback)
        {
            _callbacks.Add(callback);
        }
    }

    private void Wheel(float notches)
    {
        var hit = input.Mouse.Locked ? null : Hit();
        if (hit is null)
        {
            input.Mouse.Feed.ApplyScroll(notches);
            return;
        }

        if (UiScrolling.Wheel(tree, hit, notches))
        {
            _hover.HideTip();
        }
    }
}
