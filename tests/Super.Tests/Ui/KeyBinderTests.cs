using System.Numerics;
using Super.Testing;
using Super.Ui;
using static Super.Tests.Ui.PointerGame;

namespace Super.Tests.Ui;

/// <summary>Key binder rows, which listen for the next key or button and hand it to the game.</summary>
public sealed class KeyBinderTests
{
    private static HeadlessRun<PointerGame> Run(Func<PointerGame, UiView> screen) => PointerGame.Show(screen);

    private static UiElement Row(HeadlessRun<PointerGame> run) => Assert.Single(run.Elements, element => element.Label == "Jump");

    private static Vector2 Middle(UiRect frame) => new(frame.X + frame.Width / 2f, frame.Y + frame.Height / 2f);

    [Fact]
    public void Clicking_it_listens_and_the_next_key_binds()
    {
        Binding jump = Key.Space;
        using var run = Run(_ => FocusTests.Corner(new KeyBinder("Jump", jump, binding => jump = binding)));
        Assert.Equal("Space", Row(run).Value);

        Click(run, Middle(Row(run).Frame));
        Assert.Equal("Press a key", Row(run).Value);

        Press(run, Key.J);
        Assert.Equal((Binding)Key.J, jump);
        Assert.Equal("J", Row(run).Value);
    }

    [Fact]
    public void The_key_it_binds_never_reaches_the_game_or_moves_focus()
    {
        Binding jump = Key.Space;
        using var run = Run(game =>
        {
            game.Watch = input => input.Pressed(Key.Tab);
            return FocusTests.Corner(new VStack
            {
                new KeyBinder("Jump", jump, binding => jump = binding).Id("jump"),
                new Button("Done").Id("done"),
            });
        });
        Press(run, Key.Tab);
        Press(run, Key.Enter);

        Press(run, Key.Tab);

        Assert.Equal((Binding)Key.Tab, jump);
        Assert.Equal(0, run.Game.Reads);
        Assert.True(Row(run).Focused);
    }

    [Fact]
    public void Escape_stops_listening_and_changes_nothing()
    {
        int binds = 0;
        using var run = Run(_ => FocusTests.Corner(new KeyBinder("Jump", Key.Space, _ => binds++)));
        Click(run, Middle(Row(run).Frame));

        Press(run, Key.Escape);
        Assert.Equal("Space", Row(run).Value);

        Press(run, Key.K);
        Assert.Equal(0, binds);
    }

    [Fact]
    public void A_mouse_button_binds_wherever_it_is_pressed()
    {
        Binding jump = Key.Space;
        using var run = Run(game =>
        {
            game.Watch = input => input.Down(MouseButton.Right);
            return FocusTests.Corner(new KeyBinder("Jump", jump, binding => jump = binding));
        });
        Click(run, Middle(Row(run).Frame));

        run.Input.MoveMouse(new Vector2(700f, 500f));
        run.Ticks(1);
        run.Input.Hold(MouseButton.Right);
        run.Ticks(2);
        run.Input.Release(MouseButton.Right);
        run.Ticks(1);

        Assert.Equal((Binding)MouseButton.Right, jump);
        Assert.Equal(0, run.Game.Reads);
    }

    [Fact]
    public void A_gamepad_binder_takes_the_next_button_from_South()
    {
        Binding jump = GamepadButton.South;
        using var run = Run(_ => FocusTests.Corner(new KeyBinder("Jump", jump, binding => jump = binding) { Device = InputDevice.Gamepad }));
        run.Input.ConnectGamepad("Xbox Controller");
        Press(run, Key.Tab);
        Press(run, GamepadButton.South);
        Assert.Equal("Press a button", Row(run).Value);

        // A key isn't a gamepad binding, so it's ignored and it keeps listening.
        Press(run, Key.J);
        Assert.Equal("Press a button", Row(run).Value);

        Press(run, GamepadButton.North);
        Assert.Equal((Binding)GamepadButton.North, jump);
    }

    [Fact]
    public void Reset_shows_only_when_it_is_changed_and_puts_the_default_back()
    {
        Binding jump = Key.J;
        using var run = Run(_ => FocusTests.Corner(new KeyBinder("Jump", jump, binding => jump = binding) { Default = Key.Space }));

        var reset = Assert.Single(run.Elements, element => element.Label == "Reset");
        Click(run, Middle(reset.Frame));

        Assert.Equal((Binding)Key.Space, jump);
        Assert.DoesNotContain(run.Elements, element => element.Label == "Reset");
    }

    [Fact]
    public void A_clash_is_shown_beside_the_binding()
    {
        using var run = Run(_ => FocusTests.Corner(new KeyBinder("Jump", Key.Space) { Clash = "Interact" }));

        Assert.Contains(run.ShownText, shown => shown.Text == "Also Interact");
    }

    [Fact]
    public void One_turned_off_does_not_listen()
    {
        using var run = Run(_ => FocusTests.Corner(new KeyBinder("Jump", Key.Space) { Disabled = true }));
        Click(run, Middle(Row(run).Frame));

        Assert.Equal("Space", Row(run).Value);
        Assert.True(Row(run).Disabled);
    }
}
