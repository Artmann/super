using System.Collections;
using System.Numerics;

namespace Super;

/// <summary>
/// One finger on the screen during a tick. <see cref="Id"/> stays the same from the moment it comes down
/// until it lifts, so a game can follow each finger on its own.
/// </summary>
/// <param name="Id">Which finger this is, for as long as it's down.</param>
/// <param name="Position">Where it is, in the same pixels as the cursor and the interface.</param>
/// <param name="Delta">How far it moved this tick.</param>
/// <param name="Pressed">Whether it came down since the last tick.</param>
/// <param name="Released">Whether it lifted since the last tick. It's gone on the next one.</param>
public readonly record struct Touch(int Id, Vector2 Position, Vector2 Delta, bool Pressed, bool Released);

/// <summary>
/// The fingers on the screen, frozen for one tick, with those that lifted since the last tick still in the
/// list once, marked <see cref="Touch.Released"/>. A finger that comes down on the interface belongs to it,
/// and the game never sees it. Reach it through <c>input.Touches</c>.
/// </summary>
public sealed class TouchInput : IReadOnlyList<Touch>
{
    private readonly InputTick _tick;
    private Touch[] _touches = [];

    internal TouchInput(InputTick tick)
    {
        _tick = tick;
        Feed = new TouchFeed(tick);
    }

    public int Count => _tick.Read(_touches).Length;

    public Touch this[int index] => _tick.Read(_touches)[index];

    /// <summary>What the screen sent since the last tick, waiting for the next one.</summary>
    internal TouchFeed Feed { get; }

    public IEnumerator<Touch> GetEnumerator() => ((IEnumerable<Touch>)_tick.Read(_touches)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Roll() => _touches = Feed.Roll();
}

/// <summary>
/// Fingers between ticks, as the window and scripts feed them. While <see cref="Routes"/> is on, each
/// event waits in <see cref="Events"/> for the interface, which takes the fingers that land on it and
/// applies the rest with <see cref="Apply"/>.
/// </summary>
internal sealed class TouchFeed(InputTick tick)
{
    private readonly Dictionary<int, Finger> _fingers = [];

    /// <summary>When set, touches wait in <see cref="Events"/> for the interface to pass on what it doesn't take.</summary>
    public bool Routes { get; set; }

    /// <summary>What the fingers did that the interface hasn't seen yet, in order.</summary>
    public List<TouchEvent> Events { get; } = [];

    public void Down(int id, Vector2 position)
    {
        tick.PendingDevice = InputDevice.Touch;
        Route(new TouchEvent(TouchEventKind.Down, id, position));
    }

    public void Moved(int id, Vector2 position) => Route(new TouchEvent(TouchEventKind.Moved, id, position));

    public void Up(int id) => Route(new TouchEvent(TouchEventKind.Up, id, default));

    /// <summary>The system took the finger away, as when a system gesture starts. It lifts without a tap.</summary>
    public void Cancelled(int id) => Route(new TouchEvent(TouchEventKind.Cancelled, id, default));

    /// <summary>A finger event the interface didn't take, or no interface is there to take.</summary>
    public void Apply(TouchEvent touch)
    {
        switch (touch.Kind)
        {
            case TouchEventKind.Down:
                _fingers[touch.Id] = new Finger(touch.Position, touch.Position, Pressed: true, Released: false);
                break;
            case TouchEventKind.Moved:
                if (_fingers.TryGetValue(touch.Id, out var moving))
                {
                    _fingers[touch.Id] = moving with { Position = touch.Position };
                }

                break;
            default:
                if (_fingers.TryGetValue(touch.Id, out var lifting))
                {
                    _fingers[touch.Id] = lifting with { Released = true };
                }

                break;
        }
    }

    /// <summary>The fingers as this tick sees them. Those that lifted are dropped after, and the rest start the next tick still.</summary>
    public Touch[] Roll()
    {
        // Most ticks have no fingers, and those mustn't allocate.
        if (_fingers.Count == 0)
        {
            return [];
        }

        var touches = new Touch[_fingers.Count];
        int at = 0;
        foreach (var (id, finger) in _fingers)
        {
            touches[at++] = new Touch(id, finger.Position, finger.Position - finger.RolledAt, finger.Pressed, finger.Released);
        }

        foreach (var touch in touches)
        {
            if (touch.Released)
            {
                _fingers.Remove(touch.Id);
            }
            else
            {
                _fingers[touch.Id] = new Finger(touch.Position, touch.Position, Pressed: false, Released: false);
            }
        }

        return touches;
    }

    private void Route(TouchEvent touch)
    {
        if (Routes)
        {
            Events.Add(touch);
        }
        else
        {
            Apply(touch);
        }
    }

    private readonly record struct Finger(Vector2 Position, Vector2 RolledAt, bool Pressed, bool Released);
}

internal enum TouchEventKind
{
    Down,
    Moved,
    Up,
    Cancelled,
}

/// <summary>One thing a finger did, kept in order for the interface to look at first.</summary>
internal readonly record struct TouchEvent(TouchEventKind Kind, int Id, Vector2 Position);
