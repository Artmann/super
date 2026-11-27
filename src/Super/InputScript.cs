using System.Numerics;
using Super.Ui;

namespace Super;

/// <summary>
/// A player's input written down ahead of time, so a run can be repeated exactly. A game
/// names these in <see cref="IGame.Scripts"/> and <c>--script</c> plays one in place of
/// the real mouse and keyboard.
/// </summary>
public sealed class InputScript
{
    private readonly List<(long Tick, Action<Input, UiTree> Apply)> _tape = [];
    private long _at;

    /// <summary>Whatever comes next happens this many ticks later.</summary>
    public InputScript Wait(long ticks)
    {
        _at += ticks;
        return this;
    }

    public InputScript MoveMouse(Vector2 position) => Then(input => input.Mouse.Feed.MouseMoved(position));

    /// <summary>
    /// Moves the cursor to the middle of the view given this id with <see cref="UiExtensions.Id"/>.
    /// The view is looked up when the step runs, in the layout last shown.
    /// </summary>
    public InputScript MoveMouse(object id) => Then((input, ui) => input.Mouse.Feed.MouseMoved(ui.ScreenMiddle(id)));

    /// <summary>Holds the button down until <see cref="Release(MouseButton)"/>.</summary>
    public InputScript Hold(MouseButton button) => Then(input => input.Mouse.Feed.MouseButtonDown(button));

    public InputScript Release(MouseButton button) => Then(input => input.Mouse.Feed.MouseButtonUp(button));

    /// <summary>A tap: the key is pressed on this tick and let go on the next. It takes a tick.</summary>
    public InputScript Press(Key key) => Hold(key).Release(key).Wait(1);

    /// <summary>Holds the key down until <see cref="Release(Key)"/>.</summary>
    public InputScript Hold(Key key) => Then(input => input.KeyDown(key));

    public InputScript Release(Key key) => Then(input => input.KeyUp(key));

    /// <summary>Types this text, as the keyboard sends characters to a focused text field. It takes a tick.</summary>
    public InputScript Type(string text) => Then(input =>
    {
        foreach (char character in text)
        {
            input.Keys.Typed(character);
        }
    }).Wait(1);

    /// <summary>Takes the cursor off the window.</summary>
    public InputScript LeaveWindow() => Then(input => input.Mouse.Feed.MouseLeftWindow());

    /// <summary>A notch of the wheel. Positive turns it away from the player.</summary>
    public InputScript Scroll(float notches) => Then(input => input.Mouse.Feed.Scrolled(notches));

    /// <summary>Moves the cursor here and clicks, taking a tick so the interface sees it arrive.</summary>
    public InputScript Click(Vector2 position) => MoveMouse(position).Wait(1).ClickHere();

    /// <summary>
    /// Clicks the middle of the view given this id with <see cref="UiExtensions.Id"/>. The
    /// view is looked up when the step runs, so it can move or appear after the script is written.
    /// </summary>
    public InputScript Click(object id) => MoveMouse(id).Wait(1).ClickHere();

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

    /// <summary>
    /// Presses <paramref name="button"/> on the view given the id <paramref name="from"/>, moves to the one given
    /// <paramref name="to"/> in a few steps, and lets go. Both are looked up when each step runs.
    /// </summary>
    public InputScript Drag(object from, object to, MouseButton button = MouseButton.Left)
    {
        MoveMouse(from).Wait(1).Hold(button).Wait(1);
        const int Steps = 4;
        for (int step = 1; step <= Steps; step++)
        {
            float share = step / (float)Steps;
            Then((input, ui) => input.Mouse.Feed.MouseMoved(Vector2.Lerp(ui.ScreenMiddle(from), ui.ScreenMiddle(to), share))).Wait(1);
        }

        return Release(button).Wait(1);
    }

    /// <summary>Puts a finger down here, in screen pixels, until <see cref="TouchUp"/>.</summary>
    public InputScript TouchDown(Vector2 position, int finger = 0) => Then(input => input.Touches.Feed.Down(finger, position));

    /// <summary>Puts a finger down on the middle of the view given this id with <see cref="UiExtensions.Id"/>, looked up when the step runs.</summary>
    public InputScript TouchDown(object id, int finger = 0) => Then((input, ui) => input.Touches.Feed.Down(finger, ui.ScreenMiddle(id)));

    /// <summary>Moves a finger that's down to here.</summary>
    public InputScript TouchMove(Vector2 position, int finger = 0) => Then(input => input.Touches.Feed.Moved(finger, position));

    public InputScript TouchUp(int finger = 0) => Then(input => input.Touches.Feed.Up(finger));

    /// <summary>Taps the middle of the view given this id: a finger down, and up a tick later. It takes two ticks.</summary>
    public InputScript Tap(object id) => TouchDown(id).Wait(1).TouchUp().Wait(1);

    /// <summary>
    /// Puts a finger down here, moves it there over <paramref name="ticks"/> ticks, and lifts it at once, so a
    /// scroll view under it flings on. Fewer ticks is a faster swipe.
    /// </summary>
    public InputScript Swipe(Vector2 from, Vector2 to, int ticks = 4)
    {
        TouchDown(from).Wait(1);
        for (int step = 1; step <= ticks; step++)
        {
            TouchMove(Vector2.Lerp(from, to, step / (float)ticks)).Wait(1);
        }

        return TouchUp().Wait(1);
    }

    /// <summary>
    /// Swipes from the middle of the view given this id, <paramref name="by"/> pixels, over <paramref name="ticks"/>
    /// ticks. The view is looked up when the finger comes down, and the finger keeps to that line.
    /// </summary>
    public InputScript Swipe(object id, Vector2 by, int ticks = 4)
    {
        var from = Vector2.Zero;
        Then((input, ui) =>
        {
            from = ui.ScreenMiddle(id);
            input.Touches.Feed.Down(0, from);
        }).Wait(1);
        for (int step = 1; step <= ticks; step++)
        {
            float share = step / (float)ticks;
            Then(input => input.Touches.Feed.Moved(0, from + by * share)).Wait(1);
        }

        return TouchUp().Wait(1);
    }

    /// <summary>Adds something to happen now, or this many ticks from now without moving on.</summary>
    internal InputScript Then(Action<Input> apply, long ticksFromNow = 0) => Then((input, _) => apply(input), ticksFromNow);

    /// <summary>Adds something that reads the interface's last layout when it happens.</summary>
    internal InputScript Then(Action<Input, UiTree> apply, long ticksFromNow = 0)
    {
        _tape.Add((_at + ticksFromNow, apply));
        return this;
    }

    /// <summary>Feeds everything due on this tick into the game's input, just before the tick rolls.</summary>
    internal void Play(long tick, Input input, UiTree ui)
    {
        _at = Math.Max(_at, tick + 1);
        for (int i = 0; i < _tape.Count; i++)
        {
            if (_tape[i].Tick <= tick)
            {
                var apply = _tape[i].Apply;
                _tape.RemoveAt(i);
                i--;
                apply(input, ui);
            }
        }
    }

    private InputScript ClickHere() => Hold(MouseButton.Left).Wait(1).Release(MouseButton.Left).Wait(1);
}
