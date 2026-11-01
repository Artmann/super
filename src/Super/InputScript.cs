using System.Numerics;

namespace Super;

/// <summary>
/// A player's input written down ahead of time, so a run can be repeated exactly. A game
/// names these in <see cref="IGame.Scripts"/> and <c>--script</c> plays one in place of
/// the real mouse and keyboard.
/// </summary>
public sealed class InputScript
{
    private readonly List<(long Tick, Action<Input> Apply)> _tape = [];
    private long _at;

    /// <summary>Whatever comes next happens this many ticks later.</summary>
    public InputScript Wait(long ticks)
    {
        _at += ticks;
        return this;
    }

    public InputScript MoveMouse(Vector2 position) => Then(input => input.MouseMoved(position));

    /// <summary>Holds the button down until <see cref="Release"/>.</summary>
    public InputScript Hold(MouseButton button) => Then(input => input.MouseButtonDown(button));

    public InputScript Release(MouseButton button) => Then(input => input.MouseButtonUp(button));

    /// <summary>Takes the cursor off the window.</summary>
    public InputScript LeaveWindow() => Then(input => input.MouseLeftWindow());

    /// <summary>A notch of the wheel. Positive turns it away from the player.</summary>
    public InputScript Scroll(float notches) => Then(input => input.Scrolled(notches));

    /// <summary>Moves the cursor here and clicks, taking a tick so the interface sees it arrive.</summary>
    public InputScript Click(Vector2 position) =>
        MoveMouse(position).Wait(1).Hold(MouseButton.Left).Wait(1).Release(MouseButton.Left).Wait(1);

    /// <summary>Presses here, moves there with a few steps between, and lets go.</summary>
    public InputScript Drag(Vector2 from, Vector2 to)
    {
        MoveMouse(from).Wait(1).Hold(MouseButton.Left).Wait(1);
        const int Steps = 4;
        for (int step = 1; step <= Steps; step++)
        {
            MoveMouse(Vector2.Lerp(from, to, step / (float)Steps)).Wait(1);
        }

        return Release(MouseButton.Left).Wait(1);
    }

    /// <summary>Adds something to happen now, or this many ticks from now without moving on.</summary>
    internal InputScript Then(Action<Input> apply, long ticksFromNow = 0)
    {
        _tape.Add((_at + ticksFromNow, apply));
        return this;
    }

    /// <summary>Feeds everything due on this tick into the game's input, just before the tick rolls.</summary>
    internal void Play(long tick, Input input)
    {
        _at = Math.Max(_at, tick + 1);
        for (int i = 0; i < _tape.Count; i++)
        {
            if (_tape[i].Tick <= tick)
            {
                _tape[i].Apply(input);
                _tape.RemoveAt(i);
                i--;
            }
        }
    }
}
