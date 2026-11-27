using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using StorybookExample;
using Super;
using Super.Testing;
using Super.Ui;

namespace Storybook.Tests;

public sealed class StorybookTests
{
    public static TheoryData<string> StoryIds => new(Stories.All.Select(story => story.Id));

    private static HeadlessRun<StorybookGame> Open(StoryChoice choice)
    {
        var run = HeadlessGame.Create<StorybookGame>(1, services => services.AddSingleton(choice));
        run.Ticks(1);
        return run;
    }

    /// <summary>Clicks the middle of the view with this id, and lets the click land.</summary>
    private static void Click(HeadlessRun<StorybookGame> run, string id)
    {
        var frame = run.FrameOf(id);
        run.Input.Click(MouseButton.Left, at: new Vector2(frame.X + frame.Width / 2, frame.Y + frame.Height / 2) * run.UiScale);
        run.Ticks(2);
    }

    private static bool Shows(HeadlessRun<StorybookGame> run, string text) => run.ShownText.Any(shown => shown.Text == text);

    [Theory]
    [MemberData(nameof(StoryIds))]
    public void Every_story_opens_in_every_theme_with_its_title_on_the_page(string id)
    {
        foreach (var theme in Enum.GetValues<StoryTheme>())
        {
            using var run = Open(new StoryChoice(id, theme));

            Assert.Equal(id, run.Game.Story.Id);
            Assert.Equal(run.Game.ThemeFor(theme), run.Game.Theme);
            // As the page's title, right of the sidebar, which scrolls, so a story far down it can be out of sight there,
            // and in the header, since a showcase such as Settings has a heading of its own with the same name.
            Assert.Single(run.ShownText, shown => shown.Text == run.Game.Story.Name && shown.Frame.X > 232f && shown.Frame.Y < 100f);
        }
    }

    [Fact]
    public void With_no_choice_it_opens_the_first_story_in_the_dark_theme()
    {
        using var run = Open(new StoryChoice());

        Assert.Equal(Stories.All[0], run.Game.Story);
        Assert.Equal(Theme.Dark, run.Game.Theme);
    }

    [Fact]
    public void Every_story_has_an_id_of_its_own()
    {
        var ids = Stories.All.Select(story => story.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void A_story_id_is_its_name_in_lower_case_with_dashes()
    {
        Assert.Equal("key-hints", Stories.All.Single(story => story.Name == "Key hints").Id);
    }

    [Fact]
    public void Clicking_a_story_in_the_sidebar_opens_it()
    {
        using var run = Open(new StoryChoice("typography"));
        Assert.False(Shows(run, "Beside text"));

        Click(run, "badges");

        Assert.Equal("badges", run.Game.Story.Id);
        Assert.True(Shows(run, "Beside text"), "The badges story should be on the page.");
    }

    [Fact]
    public void The_theme_switch_changes_the_theme_both_ways()
    {
        using var run = Open(new StoryChoice());

        Click(run, "theme-light");
        Assert.Equal(Theme.Light, run.Game.Theme);

        Click(run, "theme-dark");
        Assert.Equal(Theme.Dark, run.Game.Theme);
    }

    [Fact]
    public void The_theme_switch_picks_the_textured_theme_whose_panels_are_art()
    {
        using var run = Open(new StoryChoice());

        Click(run, "theme-textured");

        Assert.Same(run.Game.TexturedTheme, run.Game.Theme);
        Assert.NotNull(run.Game.Theme.Panels.Card);
        Assert.NotNull(run.Game.Theme.Panels.Popup);
        Assert.NotNull(run.Game.Theme.Panels.Button);
    }

    [Fact]
    public void The_art_panels_story_asks_for_the_textured_theme_until_it_has_it()
    {
        const string note = "This theme draws boxes. Pick Textured at the top right to see these drawn from art.";
        using var run = Open(new StoryChoice("art-panels"));
        Assert.True(Shows(run, note), "The dark theme has no art, so the story should say so.");
        Assert.False(Shows(run, "SliceScale 2"));

        Click(run, "theme-textured");
        // The 9-slice specimen is the last, below the fold of a small window.
        run.Input.MoveMouse(new Vector2(run.ScreenSize.X / 2, run.ScreenSize.Y / 2));
        run.Input.Scroll(-40f);
        run.Ticks(2);

        Assert.False(Shows(run, note));
        Assert.True(Shows(run, "SliceScale 2"), "With art, the story should show the card's picture as a 9-slice.");
    }

    [Fact]
    public void The_command_line_picks_the_story_and_theme_and_leaves_other_options_alone()
    {
        var choice = StoryChoice.Parse(["--seed", "4", "--story", "badges", "--theme", "light", "--ticks", "2"]);

        Assert.Equal(new StoryChoice("badges", StoryTheme.Light), choice);
    }

    [Fact]
    public void The_command_line_picks_the_textured_theme()
    {
        Assert.Equal(StoryTheme.Textured, StoryChoice.Parse(["--theme", "textured"]).Theme);
    }

    [Fact]
    public void The_dark_theme_can_be_asked_for_by_name()
    {
        Assert.Equal(StoryTheme.Dark, StoryChoice.Parse(["--theme", "light", "--theme", "dark"]).Theme);
    }

    [Fact]
    public void A_theme_other_than_the_three_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => StoryChoice.Parse(["--theme", "blue"]));

        Assert.Equal("--theme takes dark, light or textured, not \"blue\".", error.Message);
    }

    [Fact]
    public void An_option_without_its_value_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => StoryChoice.Parse(["--story"]));

        Assert.Equal("--story needs a value after it.", error.Message);
    }

    [Fact]
    public void An_unknown_story_is_refused_with_the_ones_there_are()
    {
        var error = Assert.Throws<ArgumentException>(() => Stories.Find("tables"));

        Assert.StartsWith("There's no story \"tables\". The stories are typography, colours,", error.Message);
        Assert.EndsWith("key-hints, buttons, toggles, sliders, steppers, pickers, game-controls, progress, gauges, text-fields, focus, prompts, touch, menus, dialogs, sheets, toasts, layers, grids, flows, lazy-lists, cards, forms, tabs, navigation, art-panels, animation, game-feel, settings, inventory, main-menu, hud, character-creator.", error.Message);
    }

    /// <summary>Clicks a control this far across it, halfway down, and lets the click land.</summary>
    private static void ClickControl(HeadlessRun<StorybookGame> run, string id, float across = 0.5f) =>
        ClickAt(run, run.Element(id).Frame, across);

    private static void ClickAt(HeadlessRun<StorybookGame> run, UiRect frame, float across = 0.5f)
    {
        run.Input.Click(MouseButton.Left, at: new Vector2(frame.X + frame.Width * across, frame.Y + frame.Height / 2) * run.UiScale);
        run.Ticks(1);
        run.Input.Release(MouseButton.Left);
        run.Ticks(2);
    }

    [Fact]
    public void The_toggles_story_keeps_what_its_switches_are_set_to()
    {
        using var run = Open(new StoryChoice("toggles"));
        Assert.Equal(false, run.Element("subtitles").Checked);

        ClickControl(run, "subtitles");

        Assert.Equal(true, run.Element("subtitles").Checked);
        Assert.Equal(true, run.Element("music").Checked);
    }

    [Fact]
    public void The_sliders_story_moves_its_volume_to_where_the_track_is_pressed()
    {
        using var run = Open(new StoryChoice("sliders"));
        Assert.Equal("0.7", run.Element("volume").Value);

        // The far left of the track is past the thumb's half width, so it reads as none.
        ClickControl(run, "volume", across: 0f);

        Assert.Equal("0", run.Element("volume").Value);
    }

    [Fact]
    public void The_pickers_story_picks_the_difficulty_clicked()
    {
        using var run = Open(new StoryChoice("pickers"));
        var hard = run.Elements.Single(element => element.Role == UiRole.Option && element.Label == "Hard");

        ClickAt(run, hard.Frame);

        Assert.Equal("Hard", run.Element("difficulty").Value);
    }

    private static void Press(HeadlessRun<StorybookGame> run, Key key)
    {
        run.Input.Press(key);
        run.Ticks(2);
    }

    [Fact]
    public void The_focus_story_moves_round_the_locked_level_with_the_arrows_and_presses_with_enter()
    {
        using var run = Open(new StoryChoice("focus"));
        Assert.True(run.Element("level-1").Focused);

        // The first arrow only shows the ring; then down to level 4 and right past locked level 5.
        Press(run, Key.Right);
        Press(run, Key.Down);
        Press(run, Key.Right);
        Assert.True(run.Element("level-6").Focused);

        Press(run, Key.Enter);
        Assert.True(Shows(run, "Pressed: Level 6. Level 5 is locked, so focus passes it by."));
    }

    [Fact]
    public void The_text_fields_story_signs_in_with_tab_and_enter()
    {
        using var run = Open(new StoryChoice("text-fields"));
        // The form is the last card, below the fold of a small window.
        var name = run.FrameOf("name");
        run.Input.MoveMouse(new Vector2(name.X + name.Width / 2, name.Y + name.Height / 2) * run.UiScale);
        run.Input.Scroll(-20f);
        run.Ticks(2);

        Click(run, "account");
        run.Input.Type("Ada");
        run.Ticks(2);
        Press(run, Key.Tab);
        run.Input.Type("engine");
        run.Ticks(2);
        Press(run, Key.Enter);

        Assert.Equal("Ada", run.Element("account").Value);
        Assert.Equal("••••••", run.Element("secret").Value);
        Assert.True(Shows(run, "Signed in as Ada."));
    }

    [Fact]
    public void The_focus_and_typing_scripts_end_on_level_6_and_with_a_name_typed()
    {
        using var focus = Open(new StoryChoice("focus"));
        focus.Play(focus.Game.Scripts["focus"]);
        focus.Ticks(20);
        Assert.True(focus.Element("level-6").Focused);

        using var typing = Open(new StoryChoice("text-fields"));
        typing.Play(typing.Game.Scripts["typing"]);
        typing.Ticks(30);
        Assert.Equal("Ada Lovelace", typing.Element("name").Value);
        Assert.Equal(new string('•', 7), typing.Element("password").Value);
    }

    [Fact]
    public void The_touch_script_flings_the_quests_then_picks_one_and_pins_another()
    {
        using var run = Open(new StoryChoice("touch"));
        float top = run.FrameOf("quest-1").Y;

        run.Play(run.Game.Scripts["touch"]);
        run.Ticks(80);

        Assert.True(run.FrameOf("quest-1").Y < top - 400f, $"The swipe should fling the list well on, but quest 1 went from {top} to {run.FrameOf("quest-1").Y}.");
        Assert.True(Shows(run, "Picked: Ravens over the keep. Pinned: A crown of reeds."));
    }

    [Theory]
    [InlineData("menus", "menu", "Ctrl+S")]
    [InlineData("menus", "context", "Compare")]
    [InlineData("dialogs", "alert", "Delete this save?")]
    [InlineData("dialogs", "confirm", "Quit to the title screen?")]
    [InlineData("sheets", "sheet", "Hardcore")]
    [InlineData("sheets", "popover", "Running and blocking use it.")]
    [InlineData("toasts", "toasts", "The drowned bell: find the diver.")]
    public void Each_popup_script_opens_its_popup(string story, string script, string onlyInThePopup)
    {
        using var run = Open(new StoryChoice(story));
        Assert.False(Shows(run, onlyInThePopup), $"\"{onlyInThePopup}\" shouldn't show before the script runs.");

        run.Play(run.Game.Scripts[script]);
        run.Ticks(20);

        Assert.True(Shows(run, onlyInThePopup), $"The {script} script should open a popup showing \"{onlyInThePopup}\".");
    }

    [Fact]
    public void The_menus_story_picks_from_its_menu_with_the_keys()
    {
        using var run = Open(new StoryChoice("menus"));
        Click(run, "game-menu");

        Press(run, Key.Down);
        Press(run, Key.Down);
        Press(run, Key.Enter);

        Assert.True(Shows(run, "Picked: save."));
    }

    [Fact]
    public void The_dialogs_story_keeps_the_save_when_escape_cancels()
    {
        using var run = Open(new StoryChoice("dialogs"));
        Click(run, "delete-save");

        Press(run, Key.Escape);

        Assert.True(Shows(run, "The save was kept."));
        Assert.DoesNotContain(run.Elements, element => element.Role == UiRole.Dialog);
    }

    [Fact]
    public void The_toasts_story_counts_its_saves_and_lets_them_go_after_four_seconds()
    {
        using var run = Open(new StoryChoice("toasts"));
        Click(run, "save-toast");
        Click(run, "save-toast");
        Assert.Equal(["Slot 1 · save 1", "Slot 1 · save 2"], run.Elements.Where(element => element.Role == UiRole.Toast).Select(element => element.Value));

        run.Ticks(4 * 30);

        Assert.DoesNotContain(run.Elements, element => element.Role == UiRole.Toast);
    }

    [Fact]
    public void The_layers_story_brings_the_middle_badge_back_without_moving_the_last()
    {
        using var run = Open(new StoryChoice("layers"));
        var last = run.ShownText.Single(shown => shown.Text == "Last").Frame;
        Assert.False(Shows(run, "Middle"), "The middle badge starts hidden.");

        ClickControl(run, "hide");

        Assert.True(Shows(run, "Middle"));
        Assert.Equal(last, run.ShownText.Single(shown => shown.Text == "Last").Frame);
    }

    [Fact]
    public void The_lazy_lists_story_makes_only_the_rows_near_the_screen_as_it_scrolls()
    {
        using var run = Open(new StoryChoice("lazy-lists"));
        run.Ticks(2);
        Assert.True(Shows(run, "Player 1"));
        int made = run.Elements.Count(element => element.Label.StartsWith("Player ", StringComparison.Ordinal));
        Assert.InRange(made, 8, 100);

        var first = run.ShownText.Single(shown => shown.Text == "Player 1").Frame;
        run.Input.MoveMouse(new Vector2(first.X + 20f, first.Y + 100f) * run.UiScale);
        for (int i = 0; i < 10; i++)
        {
            run.Input.Scroll(-20f);
            run.Ticks(2);
        }

        Assert.False(Shows(run, "Player 1"), "Scrolling should take the first row out of sight.");
        var shown = run.ShownText.Where(text => text.Text.StartsWith("Player ", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(shown);
        Assert.InRange(run.Elements.Count(element => element.Label.StartsWith("Player ", StringComparison.Ordinal)), 8, 100);
    }

    private static UiElement Role(HeadlessRun<StorybookGame> run, UiRole role, string label) =>
        run.Elements.Single(element => element.Role == role && element.Label == label);

    [Fact]
    public void The_forms_story_opens_its_advanced_audio_settings()
    {
        using var run = Open(new StoryChoice("forms"));
        Assert.False(Shows(run, "Mono"));

        ClickAt(run, Role(run, UiRole.Disclosure, "Advanced").Frame);

        Assert.True(Shows(run, "Mono"));
        Assert.Equal(true, Role(run, UiRole.Disclosure, "Advanced").Checked);
    }

    [Fact]
    public void The_tabs_story_shows_the_tab_clicked()
    {
        using var run = Open(new StoryChoice("tabs"));
        Assert.False(Shows(run, "Speakers"));

        ClickAt(run, Role(run, UiRole.Tab, "Audio").Frame);

        Assert.True(Shows(run, "Speakers"));
    }

    [Fact]
    public void The_navigation_story_goes_two_pages_deep_and_escape_comes_back_one()
    {
        using var run = Open(new StoryChoice("navigation"));
        ClickControl(run, "graphics");
        ClickControl(run, "advanced");
        Assert.True(Shows(run, "Shadow cascades"));

        Press(run, Key.Escape);

        Assert.True(Shows(run, "Quality"));
        Assert.Equal("Graphics", run.Elements.Single(element => element.Role == UiRole.Navigation).Label);
    }

    [Fact]
    public void Turning_animations_off_on_the_animation_page_stops_everything_on_it_moving()
    {
        using var run = Open(new StoryChoice("animation"));
        // The spinner and the sweeping bar under Waiting keep moving for as long as they show.
        Assert.True(run.Animating, "The page should be moving with animations on.");

        ClickControl(run, "animations");

        Assert.Equal(false, run.Element("animations").Checked);
        Assert.False(run.Animating, "Nothing should move with animations off.");
    }

    [Fact]
    public void The_animation_pages_health_drops_by_30_a_hit_and_heals_to_full()
    {
        using var run = Open(new StoryChoice("animation"));
        // The health bar is the last specimen, below the fold of a small window.
        run.Input.MoveMouse(new Vector2(run.ScreenSize.X / 2, run.ScreenSize.Y / 2));
        run.Input.Scroll(-40f);
        run.Ticks(2);
        Assert.True(Shows(run, "100"), "The health should be in sight once scrolled to.");

        ClickControl(run, "hit");
        ClickControl(run, "hit");
        Assert.True(Shows(run, "40"));

        ClickControl(run, "heal");

        Assert.True(Shows(run, "100"));
    }

    [Fact]
    public void The_settings_screen_says_applied_until_something_changes()
    {
        using var run = Open(new StoryChoice("settings"));
        Assert.False(Shows(run, "Applied."));

        ClickControl(run, "apply");
        Assert.True(Shows(run, "Applied."));

        ClickControl(run, "fullscreen");
        Assert.Equal(false, run.Element("fullscreen").Checked);
        Assert.False(Shows(run, "Applied."), "A change after applying should take the note away.");
    }

    [Fact]
    public void The_settings_screen_resets_to_its_defaults()
    {
        using var run = Open(new StoryChoice("settings"));
        ClickControl(run, "fullscreen");
        Assert.Equal(false, run.Element("fullscreen").Checked);

        ClickControl(run, "reset");

        Assert.Equal(true, run.Element("fullscreen").Checked);
    }

    [Fact]
    public void The_inventory_shows_the_details_of_the_item_picked()
    {
        using var run = Open(new StoryChoice("inventory"));
        Assert.True(Shows(run, "A plain blade, well kept. It holds an edge."));

        ClickControl(run, "item-3");

        Assert.True(Shows(run, "Mends a third of your health over a few seconds."));
        Assert.True(Shows(run, "Drink"), "A potion is drunk, not equipped.");
    }

    [Fact]
    public void The_main_menu_asks_before_quitting_and_cancel_stays()
    {
        using var run = Open(new StoryChoice("main-menu"));
        ClickControl(run, "menu-quit");
        Assert.True(Shows(run, "Quit to the desktop?"));

        Press(run, Key.Escape);

        Assert.True(Shows(run, "You stayed."));
        Assert.False(Shows(run, "Quit to the desktop?"));
    }

    [Fact]
    public void The_hud_hotbar_picks_the_slot_clicked()
    {
        using var run = Open(new StoryChoice("hud"));
        Assert.Equal(true, run.Element("hotbar-1").Checked);

        ClickControl(run, "hotbar-4");

        Assert.Equal(true, run.Element("hotbar-4").Checked);
        Assert.Equal(false, run.Element("hotbar-1").Checked);
    }

    [Fact]
    public void The_character_creator_creates_only_with_a_name_and_every_point_spent()
    {
        using var run = Open(new StoryChoice("character-creator"));
        Assert.True(run.Element("create").Disabled);
        Assert.True(Shows(run, "Give them a name."));

        ClickControl(run, "hero-name");
        run.Input.Type("Wren");
        run.Ticks(2);
        Assert.True(Shows(run, "6 points to spend."));
        Assert.True(run.Element("create").Disabled);

        for (int i = 0; i < 6; i++)
        {
            // The plus button is the last thing along the stepper's row.
            var stepper = run.Element("strength").Frame;
            ClickAt(run, stepper, across: 1f - 12f / stepper.Width);
        }

        Assert.Equal("7", run.Element("strength").Value);
        Assert.True(Shows(run, "Ready."));
        Assert.False(run.Element("create").Disabled);

        ClickControl(run, "create");

        Assert.True(Shows(run, "Wren sets out from the coast."));

        // A colour picked after creating makes a new character, who hasn't set out yet.
        ClickControl(run, "colour-2");
        Assert.False(Shows(run, "Wren sets out from the coast."));
    }
}
