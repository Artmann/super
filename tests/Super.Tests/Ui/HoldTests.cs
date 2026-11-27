using System.Numerics;
using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>Hold buttons, which only act once they've been held long enough, by the mouse, a key or the gamepad.</summary>
public sealed class HoldTests
{
    // Half a second at 30 ticks a second.
    private const int Needed = 15;

    private static HeadlessRun<PointerGame> Run(Func<PointerGame, UiView> screen) => PointerGame.Show(screen);

    private static UiView Quit(Action quit, bool off = false) =>
        FocusTests.Corner(new HoldButton("Quit", quit, 0.5f) { Disabled = off }.Id("quit"));

    private static Vector2 Middle(HeadlessRun<PointerGame> run)
    {
        var frame = run.FrameOf("quit");
        return new Vector2(frame.X + frame.Width / 2f, frame.Y + frame.Height / 2f);
    }

    private static void PressMouse(HeadlessRun<PointerGame> run)
    {
        run.Input.MoveMouse(Middle(run));
        run.Ticks(1);
        run.Input.Hold(MouseButton.Left);
    }

    [Fact]
    public void Holding_the_mouse_on_it_acts_once_it_is_full_and_only_once()
    {
        int quits = 0;
        using var run = Run(_ => Quit(() => quits++));
        PressMouse(run);

        run.Ticks(Needed - 1);
        Assert.Equal(0, quits);
        Assert.Equal("0.933", run.Element("quit").Value);

        run.Ticks(2);
        Assert.Equal(1, quits);
        Assert.Equal("1", run.Element("quit").Value);

        run.Ticks(Needed * 2);
        Assert.Equal(1, quits);
    }

    [Fact]
    public void Letting_go_early_does_nothing_and_empties_it()
    {
        int quits = 0;
        using var run = Run(_ => Quit(() => quits++));
        PressMouse(run);
        run.Ticks(Needed / 2);
        run.Input.Release(MouseButton.Left);
        run.Ticks(Needed * 2);

        Assert.Equal(0, quits);
        Assert.Equal("0", run.Element("quit").Value);
    }

    [Fact]
    public void A_hold_starts_again_from_empty_after_letting_go()
    {
        int quits = 0;
        using var run = Run(_ => Quit(() => quits++));
        PressMouse(run);
        run.Ticks(Needed - 3);
        run.Input.Release(MouseButton.Left);
        run.Ticks(1);
        run.Input.Hold(MouseButton.Left);
        run.Ticks(Needed - 3);

        Assert.Equal(0, quits);
    }

    [Fact]
    public void Moving_off_it_while_held_stops_the_hold()
    {
        int quits = 0;
        using var run = Run(_ => Quit(() => quits++));
        PressMouse(run);
        run.Ticks(3);
        run.Input.MoveMouse(new Vector2(700f, 500f));
        run.Ticks(Needed * 2);

        Assert.Equal(0, quits);
        Assert.Equal("0", run.Element("quit").Value);
    }

    [Fact]
    public void Holding_Enter_on_it_acts_and_a_tap_does_not()
    {
        int quits = 0;
        using var run = Run(_ => Quit(() => quits++));
        PointerGame.Press(run, Key.Tab);
        PointerGame.Press(run, Key.Enter);
        run.Ticks(Needed * 2);
        Assert.Equal(0, quits);

        run.Input.Hold(Key.Enter);
        run.Ticks(Needed + 2);
        Assert.Equal(1, quits);
    }

    [Fact]
    public void Holding_South_on_it_acts()
    {
        int quits = 0;
        using var run = Run(_ => Quit(() => quits++));
        run.Input.ConnectGamepad("Xbox Controller");
        PointerGame.Press(run, Key.Tab);
        run.Input.Hold(GamepadButton.South);
        run.Ticks(Needed - 2);
        Assert.Equal(0, quits);

        run.Ticks(4);
        Assert.Equal(1, quits);
    }

    [Fact]
    public void Filling_it_plays_the_confirm_sound()
    {
        using var run = Run(_ => Quit(() => { }));
        PressMouse(run);
        run.Ticks(Needed - 2);
        Assert.DoesNotContain(run.Sounds, sound => sound.Clip == "interface/confirm");

        run.Ticks(4);
        Assert.Single(run.Sounds, sound => sound.Clip == "interface/confirm");
    }

    [Fact]
    public void One_turned_off_does_not_fill()
    {
        int quits = 0;
        using var run = Run(_ => Quit(() => quits++, off: true));
        PressMouse(run);
        run.Ticks(Needed * 2);

        Assert.Equal(0, quits);
        Assert.Equal("0", run.Element("quit").Value);
    }
}
