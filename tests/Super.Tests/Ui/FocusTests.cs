using System.Numerics;
using Super.Testing;
using Super.Ui;
using static Super.Tests.Ui.PointerGame;

namespace Super.Tests.Ui;

/// <summary>Moving focus with the keyboard and the gamepad, and what the game still sees.</summary>
public sealed class FocusTests
{
    private static HeadlessRun<PointerGame> Run(Func<PointerGame, UiView> screen) => PointerGame.Show(screen);

    private static UiView Corner(UiView view) =>
        view.Frame(width: 300f).Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    private static object? FocusedId(HeadlessRun<PointerGame> run) => run.Elements.SingleOrDefault(element => element.Focused)?.Id;

    private static UiView ThreeButtons(List<string> log) => Corner(new VStack
    {
        new Button("Play", () => log.Add("play")).Id("play"),
        new Button("Options", () => log.Add("options")).Id("options"),
        new Button("Quit", () => log.Add("quit")).Id("quit"),
    });

    [Fact]
    public void Nothing_has_focus_until_a_key_asks_for_it()
    {
        using var run = Run(_ => ThreeButtons([]));

        Assert.Null(FocusedId(run));
    }

    [Fact]
    public void Tab_focuses_the_first_control_and_Enter_presses_it()
    {
        var log = new List<string>();
        using var run = Run(_ => ThreeButtons(log));

        Press(run, Key.Tab);
        Assert.Equal("play", FocusedId(run));

        Press(run, Key.Enter);
        Assert.Equal(["play"], log);
    }

    [Fact]
    public void Tab_moves_through_the_controls_in_order_and_round_to_the_first()
    {
        using var run = Run(_ => ThreeButtons([]));
        var seen = new List<object?>();
        for (int i = 0; i < 4; i++)
        {
            Press(run, Key.Tab);
            seen.Add(FocusedId(run));
        }

        Assert.Equal(["play", "options", "quit", "play"], seen);
    }

    [Fact]
    public void Shift_Tab_moves_back_and_round_to_the_last()
    {
        using var run = Run(_ => ThreeButtons([]));
        Press(run, Key.Tab);

        run.Input.Hold(Key.LeftShift);
        Press(run, Key.Tab);
        Assert.Equal("quit", FocusedId(run));
        Press(run, Key.Tab);
        Assert.Equal("options", FocusedId(run));
    }

    [Fact]
    public void Space_presses_the_focused_button_too()
    {
        var log = new List<string>();
        using var run = Run(_ => ThreeButtons(log));
        Press(run, Key.Tab);
        Press(run, Key.Tab);

        Press(run, Key.Space);

        Assert.Equal(["options"], log);
    }

    [Fact]
    public void The_game_sees_no_part_of_a_key_focus_took()
    {
        using var run = Run(_ => ThreeButtons([]));
        run.Game.Watch = input => input.Pressed(Key.Tab) || input.Down(Key.Tab) || input.Released(Key.Tab)
            || input.Pressed(Key.Enter) || input.Down(Key.Enter) || input.Released(Key.Enter);

        Press(run, Key.Tab);
        Press(run, Key.Enter);

        Assert.Equal(0, run.Game.Reads);
    }

    [Fact]
    public void With_nothing_focused_the_arrows_and_Enter_reach_the_game()
    {
        using var run = Run(_ => ThreeButtons([]));
        run.Game.Watch = input => input.Pressed(Key.Down) || input.Pressed(Key.Enter);

        Press(run, Key.Down);
        Press(run, Key.Enter);

        Assert.Equal(2, run.Game.Reads);
        Assert.Null(FocusedId(run));
    }

    [Fact]
    public void Keys_focus_doesnt_use_reach_the_game_while_something_is_focused()
    {
        using var run = Run(_ => ThreeButtons([]));
        Press(run, Key.Tab);
        run.Game.Watch = input => input.Pressed(Key.W) || input.Pressed(Key.Escape);

        Press(run, Key.W);
        Press(run, Key.Escape);

        Assert.Equal(2, run.Game.Reads);
    }

    [Fact]
    public void A_key_the_game_held_before_focus_came_still_lets_go_in_the_game()
    {
        using var run = Run(_ => ThreeButtons([]));
        run.Input.Hold(Key.W);
        run.Ticks(2);
        Press(run, Key.Tab);
        run.Game.Watch = input => input.Down(Key.W);

        run.Input.Release(Key.W);
        run.Ticks(2);

        Assert.Equal("play", FocusedId(run));
        Assert.Equal(0, run.Game.Reads);
    }

    [Fact]
    public void A_held_Tab_repeats_and_its_repeats_never_reach_the_game()
    {
        using var run = Run(_ => ThreeButtons([]));
        var keys = run.Service<Input>().Keys;
        run.Input.Hold(Key.Tab);
        run.Ticks(2);
        run.Game.Watch = input => input.Pressed(Key.Tab);

        keys.Repeated(Key.Tab);
        keys.Repeated(Key.Tab);
        run.Ticks(2);

        Assert.Equal("quit", FocusedId(run));
        Assert.Equal(0, run.Game.Reads);
    }

    [Fact]
    public void A_repeat_of_a_key_the_game_holds_moves_no_focus()
    {
        using var run = Run(_ => ThreeButtons([]));
        run.Input.Hold(Key.Down);
        run.Ticks(2);
        Press(run, Key.Tab);

        run.Service<Input>().Keys.Repeated(Key.Down);
        run.Ticks(2);

        Assert.Equal("play", FocusedId(run));
    }

    [Fact]
    public void Focus_lets_go_of_a_control_that_turns_off()
    {
        bool off = false;
        using var run = Run(_ => ThreeButtons([]).Disabled(off));
        Press(run, Key.Tab);
        Assert.Equal("play", FocusedId(run));

        off = true;
        run.Ticks(2);

        Assert.Null(FocusedId(run));
    }

    [Fact]
    public void Down_and_up_move_to_the_control_below_and_above()
    {
        using var run = Run(_ => ThreeButtons([]));
        Press(run, Key.Tab);

        Press(run, Key.Down);
        Assert.Equal("options", FocusedId(run));
        Press(run, Key.Down);
        Assert.Equal("quit", FocusedId(run));
        Press(run, Key.Up);
        Assert.Equal("options", FocusedId(run));
    }

    [Fact]
    public void An_arrow_at_the_edge_stays_put_and_is_still_taken()
    {
        using var run = Run(_ => ThreeButtons([]));
        Press(run, Key.Tab);
        run.Game.Watch = input => input.Pressed(Key.Up);

        Press(run, Key.Up);

        Assert.Equal("play", FocusedId(run));
        Assert.Equal(0, run.Game.Reads);
    }

    [Fact]
    public void Arrows_pick_the_nearest_control_that_way_in_a_grid()
    {
        UiView Cell(string id) => new Button(id, () => { }).Id(id).Frame(width: 80f);
        using var run = Run(_ => Corner(new VStack
        {
            new HStack { Cell("a"), Cell("b"), Cell("c") },
            new HStack { Cell("d"), Cell("e"), Cell("f") },
        }));
        Press(run, Key.Tab);

        Press(run, Key.Right);
        Press(run, Key.Down);
        Assert.Equal("e", FocusedId(run));
        Press(run, Key.Right);
        Assert.Equal("f", FocusedId(run));
        Press(run, Key.Up);
        Assert.Equal("c", FocusedId(run));
    }

    [Fact]
    public void An_arrow_goes_on_along_its_row_past_a_gap_before_a_nearer_control_in_the_next_row()
    {
        UiView Cell(string id) => new Button(id, () => { }).Id(id).Frame(width: 80f);
        using var run = Run(_ => Corner(new VStack(Spacing: 8f)
        {
            new HStack(Spacing: 8f) { Cell("a"), Cell("locked").Disabled(), Cell("c") },
            new HStack(Spacing: 8f) { Cell("d"), Cell("e"), Cell("f") },
        }));
        Press(run, Key.Tab);

        Press(run, Key.Right);
        Assert.Equal("c", FocusedId(run));
    }

    [Fact]
    public void Focus_passes_a_turned_off_control_by()
    {
        using var run = Run(_ => Corner(new VStack
        {
            new Button("Play", () => { }).Id("play"),
            new Button("Continue", () => { }, Disabled: true).Id("continue"),
            new Button("Quit", () => { }).Id("quit").Disabled(),
            new Button("Credits", () => { }).Id("credits"),
        }));
        Press(run, Key.Tab);

        Press(run, Key.Tab);
        Assert.Equal("credits", FocusedId(run));
        Press(run, Key.Up);
        Assert.Equal("play", FocusedId(run));
    }

    [Fact]
    public void Left_and_right_move_a_focused_slider_by_its_step()
    {
        float volume = 0.5f;
        using var run = Run(_ => Corner(new Slider(volume, value => volume = value) { Step = 0.1f, Label = "Volume" }.Id("volume")));
        Press(run, Key.Tab);

        Press(run, Key.Right);
        Assert.Equal(0.6f, volume, 0.0001f);
        Press(run, Key.Left);
        Press(run, Key.Left);
        Assert.Equal(0.4f, volume, 0.0001f);
    }

    [Fact]
    public void A_smooth_slider_moves_a_twentieth_of_its_range()
    {
        float speed = 0f;
        using var run = Run(_ => Corner(new Slider(speed, value => speed = value, Min: 0f, Max: 200f) { Label = "Speed" }));
        Press(run, Key.Tab);

        Press(run, Key.Right);

        Assert.Equal(10f, speed);
    }

    [Fact]
    public void A_stepper_is_one_stop_that_left_and_right_step()
    {
        int players = 2;
        using var run = Run(_ => Corner(new VStack
        {
            new Stepper("Players", players, value => players = value, Min: 1, Max: 4).Id("players"),
            new Button("Start", () => { }).Id("start"),
        }));
        Press(run, Key.Tab);
        Assert.Equal("players", FocusedId(run));

        Press(run, Key.Right);
        Press(run, Key.Right);
        Press(run, Key.Right);
        Assert.Equal(4, players);

        Press(run, Key.Tab);
        Assert.Equal("start", FocusedId(run));
    }

    [Fact]
    public void Left_and_right_pick_the_option_beside_and_Enter_the_next_round()
    {
        string difficulty = "Normal";
        PickerOption<string>[] options = [new("Easy", "Easy"), new("Normal", "Normal"), new("Hard", "Hard")];
        using var run = Run(_ => Corner(new Picker<string>("Difficulty", difficulty, options, value => difficulty = value).Id("difficulty")));
        Press(run, Key.Tab);
        Assert.Equal("difficulty", FocusedId(run));

        Press(run, Key.Right);
        Assert.Equal("Hard", difficulty);
        Press(run, Key.Right);
        Assert.Equal("Hard", difficulty);
        Press(run, Key.Enter);
        Assert.Equal("Easy", difficulty);
    }

    [Fact]
    public void A_default_focus_takes_focus_as_it_appears()
    {
        var log = new List<string>();
        using var run = Run(_ => Corner(new VStack
        {
            new Button("Back", () => log.Add("back")).Id("back"),
            new VStack { new Button("Play", () => log.Add("play")).Id("play") }.DefaultFocus(),
        }));
        run.Ticks(1);

        Assert.Equal("play", FocusedId(run));
        Press(run, GamepadButton.South);
        Assert.Equal(["play"], log);
    }

    [Fact]
    public void The_first_arrow_after_a_default_focus_only_shows_where_it_is()
    {
        using var run = Run(_ => Corner(new VStack
        {
            new Button("Play", () => { }).Id("play"),
            new Button("Quit", () => { }).Id("quit"),
        }.DefaultFocus()));
        run.Ticks(1);

        Press(run, Key.Down);
        Assert.Equal("play", FocusedId(run));
        Press(run, Key.Down);
        Assert.Equal("quit", FocusedId(run));
    }

    [Fact]
    public void With_nothing_focused_an_arrow_comes_back_to_the_default_focus()
    {
        using var run = Run(_ => Corner(new VStack
        {
            new Button("Play", () => { }).Id("play"),
            new Button("Quit", () => { }).Id("quit").DefaultFocus(),
        }));
        run.Ticks(1);
        run.Input.Click(MouseButton.Left, new Vector2(1000f, 600f));
        run.Ticks(2);
        Assert.Null(FocusedId(run));

        Press(run, Key.Up);

        Assert.Equal("quit", FocusedId(run));
    }

    [Fact]
    public void A_click_lets_focus_go()
    {
        using var run = Run(_ => ThreeButtons([]));
        Press(run, Key.Tab);

        run.Input.Click("quit");
        run.Ticks(2);

        Assert.Null(FocusedId(run));
    }

    [Fact]
    public void Focus_lets_go_when_its_control_goes_away()
    {
        bool shown = true;
        using var run = Run(_ => Corner(new VStack
        {
            shown ? new Button("Play", () => { }).Id("play") : null,
            new Button("Quit", () => { }).Id("quit"),
        }));
        Press(run, Key.Tab);
        Assert.Equal("play", FocusedId(run));

        shown = false;
        run.Ticks(2);
        Assert.Null(FocusedId(run));
        Press(run, Key.Tab);
        Assert.Equal("quit", FocusedId(run));
    }

    [Fact]
    public void The_d_pad_moves_focus_and_South_presses()
    {
        var log = new List<string>();
        using var run = Run(_ => ThreeButtons(log));
        Press(run, Key.Tab);

        Press(run, GamepadButton.DPadDown);
        Press(run, GamepadButton.DPadDown);
        Assert.Equal("quit", FocusedId(run));
        Press(run, GamepadButton.South);
        Assert.Equal(["quit"], log);
    }

    [Fact]
    public void The_game_doesnt_see_the_buttons_focus_used()
    {
        using var run = Run(_ => ThreeButtons([]));
        Press(run, Key.Tab);
        run.Game.Watch = input => input.Pressed(GamepadButton.South) || input.Down(GamepadButton.South)
            || input.Pressed(GamepadButton.DPadDown) || input.Released(GamepadButton.DPadDown);

        Press(run, GamepadButton.DPadDown);
        Press(run, GamepadButton.South);

        Assert.Equal(0, run.Game.Reads);
    }

    [Fact]
    public void With_nothing_focused_the_gamepad_belongs_to_the_game()
    {
        using var run = Run(_ => ThreeButtons([]));
        run.Game.Watch = input => input.Pressed(GamepadButton.South) || input.Pressed(GamepadButton.DPadDown);

        Press(run, GamepadButton.DPadDown);
        Press(run, GamepadButton.South);

        Assert.Equal(2, run.Game.Reads);
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void A_held_d_pad_moves_again_after_a_moment_and_then_steadily()
    {
        UiView Row(int i) => new Button($"Item {i}", () => { }).Id(i);
        using var run = Run(_ => Corner(new VStack(Spacing: 0f) { Row(0), Row(1), Row(2), Row(3), Row(4), Row(5), Row(6), Row(7) }));
        Press(run, Key.Tab);

        run.Input.Hold(GamepadButton.DPadDown);
        run.Ticks(1);
        Assert.Equal(1, FocusedId(run));

        // At 30 ticks a second: the first move, a wait of 0.4 s or 12 ticks, then one every 0.1 s or 3 ticks.
        run.Ticks(11);
        Assert.Equal(1, FocusedId(run));
        run.Ticks(1);
        Assert.Equal(2, FocusedId(run));
        run.Ticks(9);
        Assert.Equal(5, FocusedId(run));
    }

    [Fact]
    public void The_stick_moves_focus_and_the_game_reads_it_centred_meanwhile()
    {
        using var run = Run(_ => ThreeButtons([]));
        Press(run, Key.Tab);
        run.Game.Watch = input => input.Gamepad.Stick(GamepadStick.Left) != Vector2.Zero;

        run.Input.Stick(GamepadStick.Left, new Vector2(0.1f, -0.9f));
        run.Ticks(2);

        Assert.Equal("options", FocusedId(run));
        Assert.Equal(0, run.Game.Reads);
    }

    [Fact]
    public void Focus_scrolls_a_control_into_view()
    {
        using var run = Run(_ =>
        {
            var list = new VStack(Spacing: 0f);
            for (int i = 0; i < 20; i++)
            {
                list.Add(new Button($"Item {i}", () => { }).Id(i));
            }

            return Corner(new ScrollView { list }.Frame(height: 200f));
        });

        Press(run, Key.Tab);
        for (int i = 0; i < 12; i++)
        {
            Press(run, Key.Down);
        }

        var focused = run.Element(12);
        Assert.True(focused.Focused);
        Assert.InRange(focused.Frame.Bottom, focused.Frame.Height, 200f);
    }

    [Fact]
    public void A_view_made_focusable_runs_its_own_activate()
    {
        int opened = 0;
        using var run = Run(_ => Corner(new Text("Chest").Focusable(() => opened++).Semantics(UiRole.Button, "Chest").Id("chest")));
        Press(run, Key.Tab);

        Press(run, Key.Enter);

        Assert.Equal("chest", FocusedId(run));
        Assert.Equal(1, opened);
    }
}
