using System.Numerics;

namespace Super.Ui;

/// <summary>
/// What the mouse buttons are doing to the interface. Two gestures can overlap: the left
/// button holding something, and a drag on any button, so a right drag can run while a
/// left click stays held elsewhere. Each is one value. See "Lifecycle state in engine
/// code" in docs/design.md.
/// </summary>
internal sealed class UiGestures(UiTree tree, Input input)
{
    /// <summary>Far enough that a sloppy click stays a click.</summary>
    private const float DragThreshold = 4f;

    private LeftHold? _hold;
    private PointerDrag? _drag;
    private Action? _slide;

    /// <summary>What a moving drag shows under the cursor.</summary>
    public UiView? Preview => (_drag as MovingDrag)?.Floating;

    /// <summary>The scroll view whose thumb the left button holds.</summary>
    public UiNode? HeldBar => (_hold as ThumbHold)?.Bar;

    /// <summary>A button went down over this node, with the cursor here.</summary>
    public void Press(UiNode hit, MouseButton button, Vector2 position)
    {
        // One drag at a time: other buttons pressed while it's held do nothing.
        if (_drag is not null)
        {
            return;
        }

        if (button == MouseButton.Left && hit.View is ScrollView view && view.BarOf(hit) is var bar && bar.Contains(position.X, position.Y))
        {
            var thumb = new ThumbHold(hit, bar.Grab(position));
            _hold = thumb;
            UiScrolling.ScrollTo(tree, hit, bar.OffsetFor(position, thumb.Grab));
            return;
        }

        if (button == MouseButton.Left && UiPointerSearch.Slidable(hit) is { } slider)
        {
            _hold = new SlideHold(slider);
            OweSlide(slider, position);
            return;
        }

        // What's under the cursor now is what counts, whatever the tree turns into while it's held.
        if (button == MouseButton.Left)
        {
            _hold = UiPointerSearch.Clickable(hit) is { View: ClickModifier clickable } node ? new ClickHold(node, clickable.Click) : null;
        }

        ArmDrag(hit, button, position);
    }

    /// <summary>The cursor moved here: a held thumb follows it, and a drag starts once it's far enough. Returns whether that scrolled.</summary>
    public bool Follow(Vector2 position)
    {
        if (_hold is ThumbHold { Bar: { View: ScrollView view } thumb } held)
        {
            return UiScrolling.ScrollTo(tree, thumb, view.BarOf(thumb).OffsetFor(position, held.Grab));
        }

        if (_hold is SlideHold { Slider: var slider })
        {
            OweSlide(slider, position);
            return false;
        }

        if (_drag is ArmedDrag armed && Vector2.Distance(position, armed.DownAt) >= DragThreshold)
        {
            StartMoving(armed);
        }

        return false;
    }

    /// <summary>
    /// A button let go over this node. Returns what that does to the game, a drop or a click,
    /// which waits for the tick.
    /// </summary>
    public Action? Release(UiNode? hit, MouseButton button)
    {
        Action? callback = null;
        var ending = _drag?.DraggedWith == button ? _drag : null;
        if (ending is MovingDrag moving && UiPointerSearch.DropTargetUnder(hit, moving.Source, moving.Payload) is { View: DropModifier target })
        {
            object payload = moving.Payload;
            callback = () => target.Drop(payload);
        }
        else if (button == MouseButton.Left
            && _hold is ClickHold { Node: { View: ClickModifier { Disabled: false }, Environment.Disabled: false } node, Click: { } click }
            && UiPointerSearch.Inside(hit, node))
        {
            callback = click;
        }

        if (button == MouseButton.Left)
        {
            _hold = null;
        }

        if (ending is not null)
        {
            _drag = null;
        }

        return callback;
    }

    /// <summary>Lets go of what left the tree. What a drag carried may be gone too, so a drag doesn't outlive where it came from.</summary>
    public void ForgetUnmounted()
    {
        if (_hold is ClickHold { Node.Mounted: false } or ThumbHold { Bar.Mounted: false } or SlideHold { Slider.Mounted: false })
        {
            _hold = null;
        }

        if (_drag is { Source.Mounted: false })
        {
            _drag = null;
        }
    }

    /// <summary>
    /// The clickable view that should look hovered, and whether it looks pressed: what a
    /// drag would drop on, what the left button holds while the cursor is on it, or else
    /// what the cursor is over while the left button is up.
    /// </summary>
    public (UiNode? Look, bool Pressed) LookAt(UiNode? hit, bool leftHeld)
    {
        if (_drag is MovingDrag moving)
        {
            return (UiPointerSearch.DropTargetUnder(hit, moving.Source, moving.Payload) is null ? null : UiPointerSearch.Clickable(hit), false);
        }

        if (_hold is ClickHold held)
        {
            bool pressed = UiPointerSearch.Inside(hit, held.Node);
            return (pressed ? held.Node : null, pressed);
        }

        return (leftHeld ? null : UiPointerSearch.Clickable(hit), false);
    }

    /// <summary>
    /// Takes the slide owed since the last tick. Only the latest counts, so a slider moved through many
    /// places between ticks is told once, where it ended up.
    /// </summary>
    public Action? TakeSlide()
    {
        var slide = _slide;
        _slide = null;
        return slide;
    }

    private void OweSlide(UiNode slider, Vector2 position)
    {
        if (slider.View is SlideModifier view)
        {
            float fraction = view.Fraction(slider, position.X);
            _slide = () => view.Slide(fraction);
        }
    }

    /// <summary>What a drag carries is settled at the press, by the button and the keys held then.</summary>
    private void ArmDrag(UiNode hit, MouseButton button, Vector2 position)
    {
        if (UiPointerSearch.Find<DragModifier>(hit) is { View: DragModifier drag } source && drag.Grab(Start(button)) is { } payload)
        {
            _drag = new ArmedDrag(source, payload, drag.Preview(payload), button, position);
        }
    }

    private void StartMoving(ArmedDrag armed)
    {
        _drag = new MovingDrag(armed.Source, armed.Payload, armed.Floating, armed.DraggedWith);

        // The drag's own press stops being a click. A left click held while another button drags is kept.
        if (armed.DraggedWith == MouseButton.Left)
        {
            _hold = null;
        }
    }

    /// <summary>On a Mac, Cmd counts as Control: Ctrl with a click means a right-click there.</summary>
    private DragStart Start(MouseButton button) => new(
        button,
        Shift: input.Held(Key.LeftShift) || input.Held(Key.RightShift),
        Control: input.Held(Key.LeftControl) || input.Held(Key.RightControl)
            || (OperatingSystem.IsMacOS() && (input.Held(Key.LeftSuper) || input.Held(Key.RightSuper))),
        Alt: input.Held(Key.LeftAlt) || input.Held(Key.RightAlt));

    /// <summary>What the left button is holding in the interface.</summary>
    private abstract record LeftHold;

    /// <summary>A press on something clickable. Its click is kept from the press, so it stays with what was pressed.</summary>
    private sealed record ClickHold(UiNode Node, Action? Click) : LeftHold;

    /// <summary>A slider, which follows the cursor until the button lets go.</summary>
    private sealed record SlideHold(UiNode Slider) : LeftHold;

    /// <summary>A scroll bar's thumb, held this far from its top.</summary>
    private sealed record ThumbHold(UiNode Bar, float Grab) : LeftHold;

    /// <summary>A drag, carrying what its source handed over at the press.</summary>
    private abstract record PointerDrag(UiNode Source, object Payload, UiView? Floating, MouseButton DraggedWith);

    /// <summary>Pressed on a source, but not yet moved far enough from <paramref name="DownAt"/> to stop being a click.</summary>
    private sealed record ArmedDrag(UiNode Source, object Payload, UiView? Floating, MouseButton DraggedWith, Vector2 DownAt)
        : PointerDrag(Source, Payload, Floating, DraggedWith);

    /// <summary>Moving: the preview follows the cursor and a release over a target drops.</summary>
    private sealed record MovingDrag(UiNode Source, object Payload, UiView? Floating, MouseButton DraggedWith)
        : PointerDrag(Source, Payload, Floating, DraggedWith);
}
