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
    /// <summary>Far enough that a sloppy click stays a click.</summary>
    private const float DragThreshold = 4f;

    private const float WheelStep = 48f;
    private const float TooltipDelaySeconds = 0.5f;
    private const int Left = 1 << (int)MouseButton.Left;

    private readonly int _tooltipTicks = (int)MathF.Ceiling(TooltipDelaySeconds * ticksPerSecond);
    private readonly List<Action> _callbacks = [];

    private Vector2 _position;
    private bool _inWindow;
    private int _uiHeld;
    private int _gameHeld;

    private UiNode? _pressed;
    private Action? _pressedClick;
    private UiNode? _dragSource;
    private DragModifier? _dragged;
    private Vector2 _pressedAt;
    private bool _dragging;
    private UiNode? _thumb;
    private float _thumbGrab;

    private UiNode? _look;
    private PressState _lookState;
    private UiNode? _litBar;

    private UiNode? _tip;
    private int _tipAge;
    private bool _tipHidden;

    /// <summary>Runs before the tick rolls, so the tick sees everything up to now.</summary>
    public void BeforeRoll()
    {
        Pump();
        _tipAge++;
        input.PendingMouseOverUi = _uiHeld != 0 || (_gameHeld == 0 && !input.CaptureMouse && Hit() is not null);
    }

    /// <summary>Handles what the mouse did since the last time this ran.</summary>
    public void Pump()
    {
        foreach (var mouse in input.MouseEvents)
        {
            switch (mouse.Kind)
            {
                case MouseEventKind.Moved:
                    Moved(mouse.Position);
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

        input.MouseEvents.Clear();
    }

    /// <summary>Runs the callbacks owed, in the order they happened. Update comes right after.</summary>
    public void Deliver()
    {
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
        ForgetUnmounted();
        var hit = (_gameHeld & Left) != 0 || input.CaptureMouse ? null : Hit();
        bool changed = RefreshLook(hit);
        changed |= RefreshBar(hit);
        RefreshTooltip(hit);
        return changed;
    }

    /// <summary>Draws the drag preview and the tooltip, above everything.</summary>
    public void ShowOverlays()
    {
        var preview = _dragging ? _dragged!.Preview : null;
        bool showTip = _tip is not null && !_tipHidden && _uiHeld == 0 && _tipAge >= _tooltipTicks;
        tree.ShowOverlays(preview, showTip ? ((TooltipModifier)_tip!.View).Content : null, _position);
    }

    private UiNode? Hit() => _inWindow ? tree.Hit(_position.X, _position.Y) : null;

    /// <summary>The nearest node at or above this one that holds a view of this kind.</summary>
    private static UiNode? Find<T>(UiNode? node)
    {
        for (; node is not null; node = node.Parent)
        {
            if (node.View is T)
            {
                return node;
            }
        }

        return null;
    }

    private static bool Inside(UiNode? node, UiNode ancestor)
    {
        for (; node is not null; node = node.Parent)
        {
            if (node == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    private static UiNode? Clickable(UiNode? node) => Find<ClickModifier>(node) is { View: ClickModifier { Disabled: false } } found ? found : null;

    private UiNode? DropTargetUnder(UiNode? node)
    {
        if (Inside(node, _dragSource!))
        {
            return null;
        }

        for (; node is not null; node = node.Parent)
        {
            if (node.View is DropModifier target && target.Accepts(_dragged!.Payload))
            {
                return node;
            }
        }

        return null;
    }

    private void Moved(Vector2 position)
    {
        _position = position;
        _inWindow = true;
        if (_thumb is { View: ScrollView bar })
        {
            ScrollTo(_thumb, bar.OffsetFor(_thumb, position, _thumbGrab));
        }
        else if (_dragSource is not null && !_dragging && Vector2.Distance(position, _pressedAt) >= DragThreshold)
        {
            _dragging = true;
            _pressed = null;
        }
    }

    private void Down(MouseButton button)
    {
        int bit = 1 << (int)button;
        var hit = input.CaptureMouse ? null : Hit();
        if (hit is null)
        {
            _gameHeld |= bit;
            input.ApplyMouseButton(button, true);
            return;
        }

        _uiHeld |= bit;
        _tipHidden = true;
        if (button != MouseButton.Left)
        {
            return;
        }

        if (hit.View is ScrollView bar && bar.InBar(hit, _position.X, _position.Y))
        {
            _thumb = hit;
            _thumbGrab = bar.Grab(hit, _position);
            ScrollTo(hit, bar.OffsetFor(hit, _position, _thumbGrab));
            return;
        }

        // What's under the cursor now is what counts, whatever the tree turns into while it's held.
        _pressed = Clickable(hit);
        _pressedClick = (_pressed?.View as ClickModifier)?.Click;
        _dragSource = Find<DragModifier>(hit);
        _dragged = _dragSource?.View as DragModifier;
        _pressedAt = _position;
    }

    private void Up(MouseButton button)
    {
        int bit = 1 << (int)button;
        if ((_uiHeld & bit) == 0)
        {
            _gameHeld &= ~bit;
            input.ApplyMouseButton(button, false);
            return;
        }

        _uiHeld &= ~bit;
        if (button == MouseButton.Left)
        {
            Released();
        }
    }

    private void Released()
    {
        ForgetUnmounted();
        var hit = Hit();
        if (_dragging && DropTargetUnder(hit) is { View: DropModifier target })
        {
            object payload = _dragged!.Payload;
            _callbacks.Add(() => target.Drop(payload));
        }
        else if (_pressed is { View: ClickModifier { Disabled: false } } && _pressedClick is { } click && Inside(hit, _pressed))
        {
            _callbacks.Add(click);
        }

        _pressed = null;
        _pressedClick = null;
        _dragSource = null;
        _dragged = null;
        _dragging = false;
        _thumb = null;
    }

    private void Wheel(float notches)
    {
        var hit = input.CaptureMouse ? null : Hit();
        if (hit is null)
        {
            input.ApplyScroll(notches);
            return;
        }

        // A wheel turns up and down, so a sideways strip inside a page leaves it to the page.
        if (!WheelOn(hit, Axis.Vertical, notches))
        {
            WheelOn(hit, Axis.Horizontal, notches);
        }
    }

    private bool WheelOn(UiNode hit, Axis axis, float notches)
    {
        for (var node = Find<ScrollView>(hit); node is not null; node = Find<ScrollView>(node.Parent))
        {
            if (((ScrollView)node.View).Axis == axis && ScrollTo(node, node.ScrollOffset - notches * WheelStep))
            {
                return true;
            }
        }

        return false;
    }

    private bool ScrollTo(UiNode node, float offset)
    {
        offset = Math.Clamp(offset, 0f, node.MaxScroll);
        if (offset == node.ScrollOffset)
        {
            return false;
        }

        node.ScrollOffset = offset;
        tree.RequestLayout();
        _tipHidden = true;
        return true;
    }

    private void ForgetUnmounted()
    {
        if (_pressed is { Mounted: false })
        {
            _pressed = null;
        }

        // What it carried may be gone too, so a drag doesn't outlive where it came from.
        if (_dragSource is { Mounted: false })
        {
            _dragSource = null;
            _dragged = null;
            _dragging = false;
        }

        if (_thumb is { Mounted: false })
        {
            _thumb = null;
        }
    }

    private bool RefreshLook(UiNode? hit)
    {
        UiNode? look = null;
        bool pressed = false;
        if (_dragging)
        {
            look = DropTargetUnder(hit) is null ? null : Clickable(hit);
        }
        else if (_pressed is not null)
        {
            pressed = Inside(hit, _pressed);
            look = pressed ? _pressed : null;
        }
        else if ((_uiHeld & Left) == 0)
        {
            look = Clickable(hit);
        }

        var state = look is null ? default : new PressState(true, pressed);
        if (look == _look && state == _lookState)
        {
            return false;
        }

        if (_look is { Mounted: true, View: ClickModifier { Look: { } old } })
        {
            old.Value = default;
        }

        if (look?.View is ClickModifier { Look: { } shown })
        {
            shown.Value = state;
        }

        _look = look;
        _lookState = state;
        return true;
    }

    private bool RefreshBar(UiNode? hit)
    {
        var lit = _thumb;
        if (lit is null && (_uiHeld & Left) == 0 && hit?.View is ScrollView bar && bar.InBar(hit, _position.X, _position.Y))
        {
            lit = hit;
        }

        if (lit == _litBar)
        {
            return false;
        }

        _litBar?.BarLit = false;
        lit?.BarLit = true;
        _litBar = lit;
        tree.RequestLayout();
        return true;
    }

    /// <summary>A press or a scroll puts a tooltip away until the cursor reaches something else.</summary>
    private void RefreshTooltip(UiNode? hit)
    {
        var tip = Find<TooltipModifier>(hit);
        if (tip != _tip)
        {
            _tip = tip;
            _tipAge = 0;
            _tipHidden = false;
        }
    }
}
