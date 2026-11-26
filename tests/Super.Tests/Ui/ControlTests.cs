using System.Globalization;
using System.Numerics;
using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>The built-in controls, driven with the mouse and read back by what they mean.</summary>
public sealed class ControlTests
{
    private static HeadlessRun<PointerGame> Run(Func<PointerGame, UiView> screen) => PointerGame.Show(screen);

    private static UiView Corner(UiView view, float width = 300f) =>
        view.Frame(width: width)
            .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    private static IReadOnlyList<UiElement> ElementsOf(UiView view)
    {
        var tree = new UiTree();
        tree.Update(view, 800, 600);
        return tree.Elements;
    }

    private static UiElement ElementOf(UiView view, object id) => UiElements.Find(ElementsOf(view), id);

    private static Vector2 Middle(UiRect frame) => new(frame.X + frame.Width / 2f, frame.Y + frame.Height / 2f);

    private static void Click(HeadlessRun<PointerGame> run, Vector2 at)
    {
        run.Input.MoveMouse(at);
        run.Input.Hold(MouseButton.Left);
        run.Ticks(1);
        run.Input.Release(MouseButton.Left);
        run.Ticks(2);
    }

    /// <summary>The point on a regular slider's track this far along. Its thumb is 16 wide, so the track is 8 in at each end.</summary>
    private static Vector2 Along(UiRect slider, float fraction) =>
        new(slider.X + 8f + fraction * (slider.Width - 16f), slider.Y + slider.Height / 2f);

    [Fact]
    public void Elements_list_each_control_with_its_role_label_value_and_state()
    {
        var elements = ElementsOf(new VStack
        {
            new Button("Play", () => { }),
            new Toggle("Music", true),
            new Slider(0.25f) { Label = "Volume" },
            new Stepper("Players", 3),
        });

        Assert.Equal(
            [
                (UiRole.Button, "Play", (string?)null, (bool?)null),
                (UiRole.Switch, "Music", null, true),
                (UiRole.Slider, "Volume", "0.25", null),
                (UiRole.Stepper, "Players", "3", null),
                (UiRole.Button, "Minus", null, null),
                (UiRole.Button, "Plus", null, null),
            ],
            elements.Select(element => (element.Role, element.Label, element.Value, element.Checked)));
    }

    [Fact]
    public void An_element_has_the_frame_of_the_control_not_the_space_round_it()
    {
        // The button is 36 high at the regular size, centred in a 40-high frame 30 in from the left.
        var element = ElementOf(
            new Button("Play", () => { }).Id("play").Frame(height: 40f).Padding(30f, 0f).Frame(800f, 600f, alignment: Alignment.TopLeading),
            "play");

        Assert.Equal(30f, element.Frame.X);
        Assert.Equal(2f, element.Frame.Y);
        Assert.Equal(36f, element.Frame.Height);
    }

    [Fact]
    public void Element_finds_the_control_with_an_id()
    {
        var element = ElementOf(new VStack { new Toggle("Music", false).Id("music"), new Toggle("Subtitles", true).Id("subtitles") }, "subtitles");

        Assert.Equal("Subtitles", element.Label);
        Assert.Equal(true, element.Checked);
    }

    [Fact]
    public void Element_with_an_unknown_id_lists_the_ids_there_are()
    {
        var view = new VStack { new Toggle("Music", false).Id("music"), new Button("Play", () => { }).Id("play") };

        var error = Assert.Throws<InvalidOperationException>(() => ElementOf(view, "volume"));

        Assert.Equal("No control on screen has the id 'volume'. These ids are: music, play.", error.Message);
    }

    [Fact]
    public void Element_says_so_when_no_control_has_an_id()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ElementOf(new Button("Play", () => { }), "play"));

        Assert.Equal("No control on screen has the id 'play', and none has an id.", error.Message);
    }

    [Fact]
    public void Element_refuses_an_id_two_controls_share()
    {
        var view = new VStack { new Button("A", () => { }).Id("same"), new Button("B", () => { }).Id("same") };

        var error = Assert.Throws<InvalidOperationException>(() => ElementOf(view, "same"));

        Assert.Equal("2 controls on screen have the id 'same', so it doesn't say which one.", error.Message);
    }

    [Fact]
    public void An_id_on_a_stack_of_controls_belongs_to_none_of_them()
    {
        var elements = ElementsOf(new VStack { new Button("Back", () => { }), new Button("Next", () => { }) }.Id("footer"));

        Assert.Equal(2, elements.Count);
        Assert.All(elements, element => Assert.Null(element.Id));
    }

    [Fact]
    public void Controls_and_a_games_own_views_inside_disabled_read_as_disabled()
    {
        var elements = ElementsOf(new VStack
        {
            new Toggle("Music", true),
            new Slider(0.5f) { Label = "Volume" },
            new Text("☆").Semantics(UiRole.Checkbox, "Favourite"),
        }.Disabled());

        Assert.Equal(3, elements.Count);
        Assert.All(elements, element => Assert.True(element.Disabled));
    }

    [Fact]
    public void A_games_own_view_describes_itself_with_semantics()
    {
        var element = ElementOf(new Text("☆").Semantics(UiRole.Checkbox, "Favourite", isChecked: true).Id("star"), "star");

        Assert.Equal(UiRole.Checkbox, element.Role);
        Assert.Equal("Favourite", element.Label);
        Assert.Equal(true, element.Checked);
    }

    [Fact]
    public void A_button_with_only_an_icon_is_named_after_it()
    {
        var element = Assert.Single(ElementsOf(new Button("", () => { }) { Icon = IconName.Close }));

        Assert.Equal("Close", element.Label);
    }

    [Fact]
    public void Clicking_a_switch_turns_it_on_and_it_shows_what_it_is_given()
    {
        bool on = false;
        using var run = Run(_ => Corner(new Toggle("Music", on, value => on = value).Id("music")));
        Assert.Equal(false, run.Element("music").Checked);

        Click(run, Middle(run.Element("music").Frame));

        Assert.True(on);
        Assert.Equal(true, run.Element("music").Checked);
    }

    [Fact]
    public void Clicking_a_checkboxs_label_turns_it_off()
    {
        bool on = true;
        using var run = Run(_ => Corner(new Toggle("Accept", on, value => on = value) { Style = ToggleStyle.Checkbox }.Id("accept")));
        var frame = run.Element("accept").Frame;

        Click(run, new Vector2(frame.Right - 4f, frame.Y + frame.Height / 2f));

        Assert.False(on);
        Assert.Equal(UiRole.Checkbox, run.Element("accept").Role);
    }

    [Fact]
    public void A_toggle_turned_off_ignores_clicks()
    {
        int changes = 0;
        using var run = Run(_ => Corner(new Toggle("Music", false, _ => changes++, Disabled: true).Id("music")));

        Click(run, Middle(run.Element("music").Frame));

        Assert.Equal(0, changes);
        Assert.True(run.Element("music").Disabled);
    }

    [Fact]
    public void Pressing_a_slider_track_jumps_the_value_there()
    {
        float volume = 0f;
        using var run = Run(_ => Corner(new Slider(volume, value => volume = value).Id("volume")));

        Click(run, Along(run.Element("volume").Frame, 0.25f));

        Assert.Equal(0.25f, volume, 3);
        Assert.Equal("0.25", run.Element("volume").Value);
    }

    [Fact]
    public void Dragging_a_slider_follows_the_mouse_until_it_is_let_go()
    {
        float volume = 0.5f;
        using var run = Run(_ => Corner(new Slider(volume, value => volume = value).Id("volume")));
        var frame = run.Element("volume").Frame;

        run.Input.MoveMouse(Along(frame, 0.5f));
        run.Input.Hold(MouseButton.Left);
        run.Ticks(1);
        run.Input.MoveMouse(Along(frame, 0.8f));
        run.Ticks(2);
        float dragged = volume;
        run.Input.MoveMouse(Along(frame, 1.2f) with { Y = frame.Y + 200f });
        run.Ticks(2);
        float pastTheEnd = volume;
        run.Input.Release(MouseButton.Left);
        run.Ticks(1);
        run.Input.MoveMouse(Along(frame, 0.1f));
        run.Ticks(2);

        Assert.Equal(0.8f, dragged, 3);
        Assert.Equal(1f, pastTheEnd, 3);
        Assert.Equal(1f, volume, 3);
    }

    [Fact]
    public void A_slider_counts_only_the_last_move_in_a_tick()
    {
        var values = new List<float>();
        using var run = Run(_ => Corner(new Slider(0f, values.Add).Id("volume")));
        var frame = run.Element("volume").Frame;

        run.Input.MoveMouse(Along(frame, 0.2f));
        run.Input.Hold(MouseButton.Left);
        run.Input.MoveMouse(Along(frame, 0.4f));
        run.Input.MoveMouse(Along(frame, 0.6f));
        run.Ticks(2);

        Assert.Equal(0.6f, Assert.Single(values), 3);
    }

    [Fact]
    public void A_slider_says_nothing_when_the_value_stays_the_same()
    {
        int changes = 0;
        using var run = Run(_ => Corner(new Slider(0f, _ => changes++).Id("volume")));

        Click(run, Along(run.Element("volume").Frame, 0f));

        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_slider_turned_off_ignores_presses()
    {
        int changes = 0;
        using var run = Run(_ => Corner(new Slider(0f, _ => changes++) { Disabled = true }.Id("volume")));

        Click(run, Along(run.Element("volume").Frame, 0.7f));

        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_games_own_view_follows_a_slide_across_it_less_its_inset()
    {
        var fractions = new List<float>();
        using var run = Run(_ => Corner(new Rectangle().Fill(Color.White).Frame(height: 20f).OnSlide(fractions.Add, inset: 50f), width: 300f));

        // 50 in from each end of 300 leaves 200 to slide across, from 50 to 250, so 100 is a quarter.
        Click(run, new Vector2(100f, 10f));

        Assert.Equal(0.25f, Assert.Single(fractions), 3);
    }

    [Theory]
    [InlineData(0.51f, 90f)]
    [InlineData(0.55f, 95f)]
    [InlineData(-1f, 60f)]
    [InlineData(2f, 120f)]
    public void A_stepped_slider_lands_on_its_steps_inside_its_range(float fraction, float expected)
    {
        // 60 + 0.51 * 60 is 90.6, nearest 90; 60 + 0.55 * 60 is 93, nearest 95.
        var slider = new Slider(90f, Min: 60f, Max: 120f) { Step = 5f };

        Assert.Equal(expected, slider.ValueAt(fraction));
    }

    [Fact]
    public void A_stepper_moves_by_its_step_and_stops_at_its_ends()
    {
        int arrows = 90;
        using var run = Run(_ => Corner(new Stepper("Arrows", arrows, value => arrows = value, 0, 99) { Step = 6 }.Id("arrows")));

        ClickStep(run, "Plus");
        int once = arrows;
        ClickStep(run, "Plus");

        Assert.Equal(96, once);
        Assert.Equal(99, arrows);
        Assert.True(StepButton(run, "Plus").Disabled);
        Assert.False(StepButton(run, "Minus").Disabled);
    }

    [Fact]
    public void A_stepper_at_its_minimum_turns_minus_off()
    {
        int changes = 0;
        using var run = Run(_ => Corner(new Stepper("Players", 1, _ => changes++, 1, 4)));

        ClickStep(run, "Minus");

        Assert.Equal(0, changes);
        Assert.True(StepButton(run, "Minus").Disabled);
    }

    [Fact]
    public void A_stepper_near_the_largest_int_stops_there_instead_of_wrapping()
    {
        int value = int.MaxValue - 2;
        using var run = Run(_ => Corner(new Stepper("Gold", value, next => value = next) { Step = 6 }));

        ClickStep(run, "Plus");

        Assert.Equal(int.MaxValue, value);
    }

    private static UiElement StepButton(HeadlessRun<PointerGame> run, string name) =>
        Assert.Single(run.Elements, element => element.Role == UiRole.Button && element.Label == name);

    private static void ClickStep(HeadlessRun<PointerGame> run, string name) => Click(run, Middle(StepButton(run, name).Frame));

    private static readonly PickerOption<int>[] Levels = [new("Easy", 1), new("Normal", 2), new("Hard", 3)];

    [Theory]
    [InlineData(PickerStyle.Segmented)]
    [InlineData(PickerStyle.Radio)]
    public void Clicking_an_option_picks_it(PickerStyle style)
    {
        int level = 2;
        using var run = Run(_ => Corner(new Picker<int>("Difficulty", level, Levels, value => level = value) { Style = style }.Id("difficulty")));

        var hard = Assert.Single(run.Elements, element => element.Role == UiRole.Option && element.Label == "Hard");
        Click(run, Middle(hard.Frame));

        Assert.Equal(3, level);
        Assert.Equal("Hard", run.Element("difficulty").Value);
        Assert.Equal(
            [("Easy", false), ("Normal", false), ("Hard", true)],
            run.Elements.Where(element => element.Role == UiRole.Option).Select(element => (element.Label, element.Checked ?? false)));
    }

    [Fact]
    public void A_picker_turned_off_ignores_clicks()
    {
        int changes = 0;
        using var run = Run(_ => Corner(new Picker<int>("Difficulty", 2, Levels, _ => changes++) { Disabled = true }));

        var easy = Assert.Single(run.Elements, element => element.Label == "Easy");
        Click(run, Middle(easy.Frame));

        Assert.Equal(0, changes);
        Assert.True(easy.Disabled);
    }

    [Theory]
    [InlineData(0.35f, 1f, 0.35f)]
    [InlineData(206f, 412f, 0.5f)]
    [InlineData(5f, 4f, 1f)]
    [InlineData(-1f, 4f, 0f)]
    [InlineData(3f, 0f, 0f)]
    public void Progress_is_the_part_done_kept_between_none_and_all(float value, float total, float expected)
    {
        Assert.Equal(expected, new ProgressView("Loading", value, total).Fraction, 4);
    }

    [Theory]
    [InlineData(72f, 0f, 100f, 0.72f)]
    [InlineData(-4f, -20f, 40f, 0.2667f)]
    [InlineData(50f, 0f, 40f, 1f)]
    [InlineData(1f, 5f, 5f, 0f)]
    public void A_gauge_reads_how_far_along_its_scale_the_value_is(float value, float min, float max, float expected)
    {
        Assert.Equal(expected, new Gauge("Heat", value, min, max).Fraction, 4);
    }

    [Fact]
    public void Meters_read_and_show_their_numbers_the_same_in_every_language()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        IReadOnlyList<UiElement> elements;
        IReadOnlyList<string> shown;
        try
        {
            var view = new VStack
            {
                new ProgressView("Loading", 0.355f),
                new Gauge("Temperature", -4.5f, -20f, 40f) { Style = MeterStyle.Linear },
            };
            var tree = new UiTree();
            tree.Update(view, 800, 600);
            elements = tree.Elements;
            shown = tree.ShownText.Select(text => text.Text).ToList();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal(["0.355", "-4.5"], elements.Select(element => element.Value));
        Assert.Equal(["Loading", "36%", "Temperature", "-4.5", "-20", "40"], shown);
    }
}
