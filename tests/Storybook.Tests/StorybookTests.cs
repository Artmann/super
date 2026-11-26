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
    public void Every_story_opens_in_both_themes_with_its_title_on_the_page(string id)
    {
        foreach (bool light in new[] { false, true })
        {
            using var run = Open(new StoryChoice(id, light));

            Assert.Equal(id, run.Game.Story.Id);
            Assert.Equal(light ? Theme.Light : Theme.Dark, run.Game.Theme);
            // Once in the sidebar and once as the page's title.
            Assert.Equal(2, run.ShownText.Count(shown => shown.Text == run.Game.Story.Name));
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
    public void The_command_line_picks_the_story_and_theme_and_leaves_other_options_alone()
    {
        var choice = StoryChoice.Parse(["--seed", "4", "--story", "badges", "--theme", "light", "--ticks", "2"]);

        Assert.Equal(new StoryChoice("badges", Light: true), choice);
    }

    [Fact]
    public void The_dark_theme_can_be_asked_for_by_name()
    {
        Assert.False(StoryChoice.Parse(["--theme", "light", "--theme", "dark"]).Light);
    }

    [Fact]
    public void A_theme_other_than_dark_or_light_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => StoryChoice.Parse(["--theme", "blue"]));

        Assert.Equal("--theme takes dark or light, not \"blue\".", error.Message);
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
        Assert.EndsWith("key-hints.", error.Message);
    }
}
