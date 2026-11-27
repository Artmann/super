using System.Numerics;

namespace Super.Ui;

/// <summary>
/// What a finger does to the interface. One finger at a time drives it: the first that lands on something
/// solid. A tap clicks, a drag scrolls the scroll view under it and flings on when it lets go, a hold calls a
/// long press, and a slider follows the finger. A finger never hovers. Each gesture is one value. See
/// "Lifecycle state in engine code" in docs/design.md.
/// </summary>
internal sealed class UiTouch(UiTree tree, int ticksPerSecond)
{
    /// <summary>How far a finger moves before a tap becomes a drag. A finger is less steady than a mouse.</summary>
    private const float Slop = 10f;

    private const float LongPressSeconds = 0.5f;

    /// <summary>How much of a fling's speed is left after a second, like a list on a phone.</summary>
    private const float FlingKeptPerSecond = 0.1f;

    /// <summary>Slower than this, in units a second, and a fling stops.</summary>
    private const float FlingStop = 20f;

    private readonly float _tickSeconds = 1f / ticksPerSecond;
    private readonly int _longPressTicks = (int)MathF.Ceiling(LongPressSeconds * ticksPerSecond);
    private readonly HashSet<int> _taken = [];

    private Gesture? _gesture;
    private Fling? _fling;
    private Action? _slide;

    /// <summary>The clickable view a finger holds while it's still on it, which looks pressed.</summary>
    public UiNode? Pressed => _gesture is Press { Click: { } node } press && UiPointerSearch.Inside(Hit(press.At), node) ? node : null;

    /// <summary>
    /// Hands one thing a finger did to the interface. A tap focuses what it lands on and owes its click, and
    /// whatever the interface doesn't take goes on to the game's touches.
    /// </summary>
    public void Route(TouchEvent touch, TouchFeed game, UiFocus focus, UiHover hover, List<Action> callbacks)
    {
        // Fingers are in pixels and the interface in its own units.
        var at = touch.Position / tree.Scale;
        bool taken;
        switch (touch.Kind)
        {
            case TouchEventKind.Down:
                taken = Down(touch.Id, at);
                if (taken)
                {
                    hover.HideTip();
                }

                break;
            case TouchEventKind.Moved:
                taken = Moved(touch.Id, at);
                break;
            default:
                (taken, var tap) = Up(touch.Id, cancelled: touch.Kind == TouchEventKind.Cancelled);
                if (tap is not null)
                {
                    focus.Pointed(tap.Hit, tap.At.X);
                    if (tap.Click is { } click)
                    {
                        callbacks.Add(click);
                    }
                }

                break;
        }

        if (!taken)
        {
            game.Apply(touch);
        }
    }

    /// <summary>A finger came down here. Returns whether the interface takes it, so the game never sees it.</summary>
    private bool Down(int id, Vector2 at)
    {
        var hit = Hit(at);
        if (hit is null)
        {
            return false;
        }

        _taken.Add(id);
        _fling = null;
        if (_gesture is not null)
        {
            return true;
        }

        if (UiPointerSearch.Slidable(hit) is { } slider)
        {
            _gesture = new Slide(id, slider);
            OweSlide(slider, at);
            return true;
        }

        _gesture = new Press(id, hit, UiPointerSearch.Find<ClickModifier>(hit), at, at, Age: 0);
        return true;
    }

    /// <summary>A finger moved here. Returns whether it's one the interface took.</summary>
    private bool Moved(int id, Vector2 at)
    {
        if (!_taken.Contains(id))
        {
            return false;
        }

        switch (_gesture)
        {
            case Press press when press.Id == id:
                _gesture = Vector2.Distance(at, press.DownAt) >= Slop && Pannable(press.Hit, at - press.DownAt) is { } pan
                    ? Pan(new Panning(id, pan, press.DownAt, Vector2.Zero, Vector2.Zero), at)
                    : press with { At = at };
                break;
            case Panning panning when panning.Id == id:
                _gesture = Pan(panning, at);
                break;
            case Slide slide when slide.Id == id:
                OweSlide(slide.Slider, at);
                break;
        }

        return true;
    }

    /// <summary>
    /// A finger lifted, or the system took it. Returns whether it's one the interface took, and the tap it
    /// made, if it was one.
    /// </summary>
    private (bool Taken, Tap? Tap) Up(int id, bool cancelled)
    {
        if (!_taken.Remove(id))
        {
            return (false, null);
        }

        Tap? tap = null;
        switch (_gesture)
        {
            case Press press when press.Id == id && !cancelled:
                var hit = Hit(press.At);
                // Like a mouse click, it only counts if the finger lifts on what it pressed, and that is still on.
                var click = press.Click is { } node && UiPointerSearch.Inside(hit, node) ? UiPointerSearch.Clicked(node, (node.View as ClickModifier)?.Click) : null;
                tap = new Tap(hit, press.At, click);
                break;
            case Panning panning when panning.Id == id && !cancelled:
                _fling = panning.Velocity.Length() > FlingStop ? new Fling(panning.Scroll, panning.Velocity) : null;
                break;
        }

        if (_gesture?.Id == id)
        {
            _gesture = null;
        }

        return (true, tap);
    }

    /// <summary>
    /// Once a tick: works out how fast a drag goes, carries a fling on, and calls a long press once a finger
    /// has held still long enough. Returns that long press, which waits for the tick like a click.
    /// </summary>
    public Action? Tick()
    {
        Coast();
        switch (_gesture)
        {
            case Panning panning:
                // The speed leans on the last few ticks, and a finger that rests a whole tick before it lifts doesn't fling.
                var velocity = panning.MovedThisTick == Vector2.Zero ? Vector2.Zero : Vector2.Lerp(panning.Velocity, panning.MovedThisTick / _tickSeconds, 0.6f);
                _gesture = panning with { Velocity = velocity, MovedThisTick = Vector2.Zero };
                break;
            case Press press:
                _gesture = press with { Age = press.Age + 1 };
                if (press.Age + 1 == _longPressTicks && Held(press.At) is { } held)
                {
                    _gesture = new Spent(press.Id);
                    return held;
                }

                break;
        }

        return null;
    }

    /// <summary>What a finger held still here does: the view's long press, or else its context menu, opened under the finger.</summary>
    private Action? Held(Vector2 at)
    {
        var hit = Hit(at);
        if (UiPointerSearch.Find<LongPressModifier>(hit) is { View: LongPressModifier held, Environment.Disabled: false })
        {
            return held.LongPress;
        }

        return UiPointerSearch.Find<ContextModifier>(hit) is { View: ContextModifier menu, Environment.Disabled: false } ? () => menu.Open(at) : null;
    }

    /// <summary>Takes the slide owed since the last tick. Only the latest counts, as with the mouse.</summary>
    public Action? TakeSlide()
    {
        var slide = _slide;
        _slide = null;
        return slide;
    }

    /// <summary>Lets go of what left the tree.</summary>
    public void ForgetUnmounted()
    {
        if (_gesture is Press { Hit.Mounted: false } or Slide { Slider.Mounted: false } or Panning { Scroll.Mounted: false })
        {
            _gesture = new Spent(_gesture.Id);
        }

        if (_fling is { Scroll.Mounted: false })
        {
            _fling = null;
        }
    }

    private UiNode? Hit(Vector2 at) => tree.Layers.Hit(at.X, at.Y);

    /// <summary>The nearest scroll view the finger can move this way, along the way it mostly went.</summary>
    private static UiNode? Pannable(UiNode hit, Vector2 moved)
    {
        var axis = MathF.Abs(moved.X) > MathF.Abs(moved.Y) ? Axis.Horizontal : Axis.Vertical;
        for (var node = UiPointerSearch.Find<ScrollView>(hit); node is not null; node = UiPointerSearch.Find<ScrollView>(node.Parent))
        {
            if (((ScrollView)node.View).Axis == axis && node.Scroll.MaxScroll > 0f)
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>Moves the content with the finger, so what's under it stays under it.</summary>
    private Panning Pan(Panning panning, Vector2 at)
    {
        var moved = at - panning.At;
        float along = ((ScrollView)panning.Scroll.View).Axis == Axis.Horizontal ? moved.X : moved.Y;
        UiScrolling.ScrollTo(tree, panning.Scroll, panning.Scroll.Scroll.Offset - along);
        return panning with { At = at, MovedThisTick = panning.MovedThisTick + moved };
    }

    private void Coast()
    {
        if (_fling is not { } fling)
        {
            return;
        }

        float along = ((ScrollView)fling.Scroll.View).Axis == Axis.Horizontal ? fling.Velocity.X : fling.Velocity.Y;
        var velocity = fling.Velocity * MathF.Pow(FlingKeptPerSecond, _tickSeconds);
        bool moved = UiScrolling.ScrollTo(tree, fling.Scroll, fling.Scroll.Scroll.Offset - along * _tickSeconds);
        _fling = moved && velocity.Length() > FlingStop ? fling with { Velocity = velocity } : null;
    }

    private void OweSlide(UiNode slider, Vector2 at)
    {
        if (slider.View is SlideModifier view)
        {
            float fraction = view.Fraction(slider, at.X);
            _slide = () => view.Slide(fraction);
        }
    }

    /// <summary>What a finger that came down on the interface is doing.</summary>
    private abstract record Gesture(int Id);

    /// <summary>Down, and not moved far enough to be a drag. A tap if it lifts here.</summary>
    private sealed record Press(int Id, UiNode Hit, UiNode? Click, Vector2 DownAt, Vector2 At, int Age) : Gesture(Id);

    /// <summary>Dragging a scroll view, last at <paramref name="At"/>.</summary>
    private sealed record Panning(int Id, UiNode Scroll, Vector2 At, Vector2 Velocity, Vector2 MovedThisTick) : Gesture(Id);

    /// <summary>Holding a slider, which follows the finger until it lifts.</summary>
    private sealed record Slide(int Id, UiNode Slider) : Gesture(Id);

    /// <summary>Done, as after a long press, so lifting does nothing.</summary>
    private sealed record Spent(int Id) : Gesture(Id);

    /// <summary>A scroll view still moving after the finger let go, at this speed in units a second.</summary>
    private sealed record Fling(UiNode Scroll, Vector2 Velocity);
}

/// <summary>A finger that came down and lifted without moving far: where, and the click it pressed, if any.</summary>
internal sealed record Tap(UiNode? Hit, Vector2 At, Action? Click);
