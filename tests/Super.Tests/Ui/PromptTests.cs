using Super.Testing;
using Super.Ui;
using static Super.Tests.Ui.PointerGame;

namespace Super.Tests.Ui;

/// <summary>Button prompts: how each binding is named for each device, and how an action's prompt follows the player.</summary>
public sealed class PromptTests
{
    private enum Control
    {
        Jump,
        Move,
    }

    private static string[] Prompts(HeadlessRun<PointerGame> run) =>
        [.. run.Elements.Where(element => element.Role == UiRole.Prompt).Select(element => $"{element.Label} ({element.Value})")];

    private static HeadlessRun<PointerGame> Showing(UiView view)
    {
        var run = Show(_ => new VStack { view });
        run.Ticks(1);
        return run;
    }

    [Theory]
    [InlineData(GamepadFamily.Xbox, GamepadButton.South, "A (Xbox)")]
    [InlineData(GamepadFamily.Xbox, GamepadButton.West, "X (Xbox)")]
    [InlineData(GamepadFamily.PlayStation, GamepadButton.South, "Cross (PlayStation)")]
    [InlineData(GamepadFamily.PlayStation, GamepadButton.North, "Triangle (PlayStation)")]
    [InlineData(GamepadFamily.Nintendo, GamepadButton.South, "B (Nintendo)")]
    [InlineData(GamepadFamily.Nintendo, GamepadButton.East, "A (Nintendo)")]
    [InlineData(GamepadFamily.Other, GamepadButton.South, "A (Other)")]
    [InlineData(GamepadFamily.PlayStation, GamepadButton.RightBumper, "R1 (PlayStation)")]
    [InlineData(GamepadFamily.Nintendo, GamepadButton.Start, "+ (Nintendo)")]
    [InlineData(GamepadFamily.Xbox, GamepadButton.DPadLeft, "D-pad left (Xbox)")]
    public void A_pad_button_is_named_as_its_family_marks_it(GamepadFamily family, GamepadButton button, string expected)
    {
        using var run = Showing(new ButtonPrompt(button) { Family = family });

        Assert.Equal([expected], Prompts(run));
    }

    [Theory]
    [InlineData(GamepadFamily.Xbox, "RT (Xbox)")]
    [InlineData(GamepadFamily.PlayStation, "R2 (PlayStation)")]
    [InlineData(GamepadFamily.Nintendo, "ZR (Nintendo)")]
    public void A_trigger_is_named_as_its_family_names_it(GamepadFamily family, string expected)
    {
        using var run = Showing(new ButtonPrompt(GamepadTrigger.Right) { Family = family });

        Assert.Equal([expected], Prompts(run));
    }

    [Theory]
    [InlineData(Key.Escape, "Esc")]
    [InlineData(Key.D1, "1")]
    [InlineData(Key.LeftShift, "Shift")]
    [InlineData(Key.E, "E")]
    [InlineData(Key.Slash, "/")]
    [InlineData(Key.Up, "Up")]
    public void A_key_is_named_as_keyboards_print_it(Key key, string expected)
    {
        using var run = Showing(new ButtonPrompt(key));

        Assert.Equal([$"{expected} (Keyboard)"], Prompts(run));
    }

    [Fact]
    public void A_keycap_shows_the_short_name_as_text()
    {
        using var run = Showing(new ButtonPrompt(Key.Escape));

        Assert.Contains(run.ShownText, shown => shown.Text == "Esc");
    }

    [Fact]
    public void A_composite_shows_each_of_its_keys_in_order()
    {
        using var run = Showing(new ButtonPrompt(Keys.WASD));

        Assert.Equal(["W (Keyboard)", "S (Keyboard)", "A (Keyboard)", "D (Keyboard)"], Prompts(run));
    }

    [Fact]
    public void Mouse_buttons_and_motion_are_drawn_as_the_mouse()
    {
        using var run = Showing(new HStack { new ButtonPrompt(MouseButton.Right), new ButtonPrompt(Binding.MouseDelta) });

        Assert.Equal(["Right click (Mouse)", "Mouse (Mouse)"], Prompts(run));
    }

    [Fact]
    public void A_button_prompt_follows_the_pad_plugged_in()
    {
        using var run = Showing(new ButtonPrompt(GamepadButton.South));
        Assert.Equal(["A (Other)"], Prompts(run));

        run.Input.ConnectGamepad("PS5 Controller");
        run.Ticks(1);

        Assert.Equal(["Cross (PlayStation)"], Prompts(run));
    }

    [Fact]
    public void An_action_prompt_shows_the_binding_for_the_device_in_use()
    {
        Actions<Control>? actions = null;
        using var run = Show(game =>
        {
            if (actions is null)
            {
                actions = new Actions<Control>(game.Input);
                actions.Bind(Control.Jump, Key.Space, GamepadButton.South);
            }

            return new ActionPrompt<Control>(actions, Control.Jump, "Jump");
        });
        run.Input.ConnectGamepad("Xbox Controller");
        run.Ticks(1);
        Assert.Equal(["Space (Keyboard)"], Prompts(run));
        Assert.Contains(run.ShownText, shown => shown.Text == "Jump");

        Press(run, GamepadButton.South);
        Assert.Equal(["A (Xbox)"], Prompts(run));

        Press(run, Key.E);
        Assert.Equal(["Space (Keyboard)"], Prompts(run));
    }

    [Fact]
    public void An_action_prompt_follows_a_rebind()
    {
        Actions<Control>? actions = null;
        using var run = Show(game =>
        {
            actions ??= new Actions<Control>(game.Input);
            return new ActionPrompt<Control>(actions, Control.Jump);
        });
        actions!.Bind(Control.Jump, Key.Space);
        run.Ticks(1);
        Assert.Equal(["Space (Keyboard)"], Prompts(run));

        actions.Unbind(Control.Jump);
        actions.Bind(Control.Jump, Key.F);
        run.Ticks(1);

        Assert.Equal(["F (Keyboard)"], Prompts(run));
    }

    [Fact]
    public void An_action_prompt_shows_nothing_on_a_touch_screen()
    {
        Actions<Control>? actions = null;
        using var run = Show(game =>
        {
            if (actions is null)
            {
                actions = new Actions<Control>(game.Input);
                actions.Bind(Control.Jump, Key.Space, GamepadButton.South);
            }

            return new ActionPrompt<Control>(actions, Control.Jump, "Jump");
        });
        run.Ticks(1);

        run.Input.Tap(new System.Numerics.Vector2(700f, 500f));
        run.Ticks(2);

        Assert.Empty(Prompts(run));
        Assert.DoesNotContain(run.ShownText, shown => shown.Text == "Jump");
    }
}
