using System.Numerics;
using Super.Testing;
using Super.Ui;
using static Super.Tests.Ui.PointerGame;

namespace Super.Tests.Ui;

/// <summary>A view that waits for any key, mouse button or gamepad button, as a title screen does.</summary>
public sealed class AnyPressTests
{
    private static HeadlessRun<PointerGame> Waiting(Action press) =>
        PointerGame.Show(_ => new Text("Press any button").OnAnyPress(press));

    [Theory]
    [InlineData(Key.J)]
    [InlineData(Key.Escape)]
    [InlineData(Key.Enter)]
    public void Any_key_presses_it(Key key)
    {
        int presses = 0;
        using var run = Waiting(() => presses++);

        Press(run, key);

        Assert.Equal(1, presses);
    }

    [Fact]
    public void A_gamepad_button_presses_it()
    {
        int presses = 0;
        using var run = Waiting(() => presses++);

        Press(run, GamepadButton.North);

        Assert.Equal(1, presses);
    }

    [Fact]
    public void A_click_anywhere_presses_it_even_off_the_view()
    {
        int presses = 0;
        using var run = Waiting(() => presses++);

        Click(run, new Vector2(run.ScreenSize.X - 5f, run.ScreenSize.Y - 5f));

        Assert.Equal(1, presses);
    }

    [Fact]
    public void Nothing_presses_it_until_something_is_pressed()
    {
        int presses = 0;
        using var run = Waiting(() => presses++);

        run.Ticks(30);

        Assert.Equal(0, presses);
    }

    [Fact]
    public void The_press_reaches_neither_the_game_nor_the_button_that_takes_its_place()
    {
        int started = 0;
        bool waiting = true;
        using var run = PointerGame.Show(game =>
        {
            game.Watch = input => input.Pressed(Key.Enter);
            return waiting
                ? new Text("Press any button").OnAnyPress(() => waiting = false)
                : new Button("Start", () => started++).DefaultFocus();
        });

        Press(run, Key.Enter);
        run.Ticks(2);

        Assert.False(waiting);
        Assert.Equal(0, started);
        Assert.Equal(0, run.Game.Reads);
    }
}
