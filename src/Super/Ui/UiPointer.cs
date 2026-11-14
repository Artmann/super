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

    // Two gestures that can overlap: the left button holding something, and a drag on any
    // button, so a right drag can run while a left click stays held elsewhere. Each is one
    // value. See "Lifecycle state in engine code" in docs/design.md.
    private Hold? _hold;
    private Drag? _drag;

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
        var preview = (_drag as MovingDrag)?.Preview;
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

    private static UiNode? DropTargetUnder(UiNode? node, Drag drag)
    {
        if (Inside(node, drag.Source))
        {
            return null;
        }

        for (; node is not null; node = node.Parent)
        {
            if (node.View is DropModifier target && target.Accepts(drag.Payload))
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
        if (_hold is ThumbHold { Bar: { View: ScrollView bar } thumb } held)
        {
            ScrollTo(thumb, bar.OffsetFor(thumb, position, held.Grab));
        }
        else if (_drag is ArmedDrag armed && Vector2.Distance(position, armed.DownAt) >= DragThreshold)
        {
            StartMoving(armed);
        }
    }

    private void StartMoving(ArmedDrag armed)
    {
        _drag = new MovingDrag(armed.Source, armed.Payload, armed.Preview, armed.DraggedWith);

        // The drag's own press stops being a click. A left click held while another button drags is kept.
        if (armed.DraggedWith == MouseButton.Left)
        {
            _hold = null;
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

        // One drag at a time: other buttons pressed while it's held do nothing.
        if (_drag is not null)
        {
            return;
        }

        if (button == MouseButton.Left && hit.View is ScrollView bar && bar.InBar(hit, _position.X, _position.Y))
        {
            var thumb = new ThumbHold(hit, bar.Grab(hit, _position));
            _hold = thumb;
            ScrollTo(hit, bar.OffsetFor(hit, _position, thumb.Grab));
            return;
        }

        // What's under the cursor now is what counts, whatever the tree turns into while it's held.
        if (button == MouseButton.Left)
        {
            _hold = Clickable(hit) is { View: ClickModifier clickable } node ? new ClickHold(node, clickable.Click) : null;
        }

        ArmDrag(hit, button);
    }

    /// <summary>What a drag carries is settled at the press, by the button and the keys held then.</summary>
    private void ArmDrag(UiNode hit, MouseButton button)
    {
        if (Find<DragModifier>(hit) is { View: DragModifier drag } source && drag.Grab(Start(button)) is { } payload)
        {
            _drag = new ArmedDrag(source, payload, drag.Preview(payload), button, _position);
        }
    }

    private DragStart Start(MouseButton button) => new(
        button,
        Shift: input.Held(Key.LeftShift) || input.Held(Key.RightShift),
        Control: input.Held(Key.LeftControl) || input.Held(Key.RightControl),
        Alt: input.Held(Key.LeftAlt) || input.Held(Key.RightAlt));

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
        Released(button);
    }

    private void Released(MouseButton button)
    {
        ForgetUnmounted();
        var hit = Hit();
        var ending = _drag?.DraggedWith == button ? _drag : null;
        if (ending is MovingDrag moving && DropTargetUnder(hit, moving) is { View: DropModifier target })
        {
            object payload = moving.Payload;
            _callbacks.Add(() => target.Drop(payload));
        }
        else if (button == MouseButton.Left
            && _hold is ClickHold { Node: { View: ClickModifier { Disabled: false } } node, Click: { } click }
            && Inside(hit, node))
        {
            _callbacks.Add(click);
        }

        if (button == MouseButton.Left)
        {
            _hold = null;
        }

        if (ending is not null)
        {
            _drag = null;
        }
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
        if (_hold is ClickHold { Node.Mounted: false } or ThumbHold { Bar.Mounted: false })
        {
            _hold = null;
        }

        // What it carried may be gone too, so a drag doesn't outlive where it came from.
        if (_drag is { Source.Mounted: false })
        {
            _drag = null;
        }
    }

    private bool RefreshLook(UiNode? hit)
    {
        UiNode? look = null;
        bool pressed = false;
        if (_drag is MovingDrag moving)
        {
            look = DropTargetUnder(hit, moving) is null ? null : Clickable(hit);
        }
        else if (_hold is ClickHold held)
        {
            pressed = Inside(hit, held.Node);
            look = pressed ? held.Node : null;
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
        var lit = (_hold as ThumbHold)?.Bar;
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

    /// <summary>What the left button is holding in the interface.</summary>
    private abstract record Hold;

    /// <summary>A press on something clickable. Its click is kept from the press, so it stays with what was pressed.</summary>
    private sealed record ClickHold(UiNode Node, Action? Click) : Hold;

    /// <summary>A scroll bar's thumb, held this far from its top.</summary>
    private sealed record ThumbHold(UiNode Bar, float Grab) : Hold;

    /// <summary>A drag, carrying what its source handed over at the press.</summary>
    private abstract record Drag(UiNode Source, object Payload, UiView? Preview, MouseButton DraggedWith);

    /// <summary>Pressed on a source, but not yet moved far enough from <paramref name="DownAt"/> to stop being a click.</summary>
    private sealed record ArmedDrag(UiNode Source, object Payload, UiView? Preview, MouseButton DraggedWith, Vector2 DownAt)
        : Drag(Source, Payload, Preview, DraggedWith);

    /// <summary>Moving: the preview follows the cursor and a release over a target drops.</summary>
    private sealed record MovingDrag(UiNode Source, object Payload, UiView? Preview, MouseButton DraggedWith)
        : Drag(Source, Payload, Preview, DraggedWith);
}
