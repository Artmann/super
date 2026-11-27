using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>The views that tell a story: backdrops, dialogue, title cards, achievements and subtitles.</summary>
public sealed class StoryTests
{
    // A second at 30 ticks a second.
    private const int Second = 30;

    private const string Long = "The bridge is out, so we'll have to go round by the old mill before the sun sets.";

    private static string Shown(HeadlessRun<PointerGame> run, string start) =>
        run.ShownText.Select(text => text.Text).SingleOrDefault(text => start.StartsWith(text, StringComparison.Ordinal) && text.Length > 0) ?? "";

    private static ShownText Text(HeadlessRun<PointerGame> run, string text) => run.ShownText.Single(shown => shown.Text == text);

    [Fact]
    public void A_backdrop_blurs_the_game_behind_it_as_it_fades_in()
    {
        using var run = PointerGame.Show(_ => new ZStack { new Backdrop(), new Text("Paused") });
        run.Ticks(3);
        float early = run.Screen.Blur;
        run.Ticks(Second);

        Assert.InRange(early, 0.01f, 0.7f);
        Assert.Equal(0.8f, run.Screen.Blur, 3);
    }

    [Fact]
    public void The_blur_goes_with_the_backdrop()
    {
        using var run = PointerGame.Show(_ => new ZStack { new Backdrop(), new Text("Game") });
        run.Ticks(Second);
        run.Game.Screen = _ => new Text("Game");
        run.Ticks(2);

        Assert.Equal(0f, run.Screen.Blur);
    }

    [Fact]
    public void A_backdrop_never_lowers_the_games_own_blur_or_drops_its_other_effects()
    {
        using var run = PointerGame.Show(_ => new Backdrop());
        var effects = run.World.Spawn(new Rendition.ScreenEffects { Blur = 0.9f, Desaturate = 0.4f });
        run.Ticks(Second);
        float stronger = run.Screen.Blur;
        run.World.Get<Rendition.ScreenEffects>(effects).Blur = 0.2f;
        run.Ticks(2);

        Assert.Equal(0.9f, stronger, 3);
        Assert.Equal(0.8f, run.Screen.Blur, 3);
        Assert.Equal(0.4f, run.Screen.Desaturate, 3);
    }

    [Fact]
    public void A_backdrop_with_no_blur_leaves_the_game_sharp()
    {
        using var run = PointerGame.Show(_ => new Backdrop { Blur = 0f });
        run.Ticks(Second);

        Assert.Equal(0f, run.Screen.Blur);
    }

    [Fact]
    public void Without_animations_a_backdrop_blurs_at_once()
    {
        using var run = PointerGame.Show(_ => new Backdrop { Blur = 0.5f }.Animations(false));

        Assert.Equal(0.5f, run.Screen.Blur, 3);
    }

    [Fact]
    public void A_line_types_itself_out()
    {
        using var run = PointerGame.Show(_ => new DialogueBox("Mira", Long));
        run.Ticks(Second / 2);
        string partway = Shown(run, Long);
        run.Ticks(Second * 3);

        Assert.InRange(partway.Length, 10, Long.Length - 20);
        Assert.Equal(Long, Shown(run, Long));
        Assert.Contains(run.ShownText, text => text.Text == "Mira");
    }

    [Fact]
    public void Typed_text_takes_the_room_of_the_whole_line_from_the_start()
    {
        using var run = PointerGame.Show(_ => FocusTests.Corner(new DialogueBox("Mira", Long)));
        using var shortLine = PointerGame.Show(_ => FocusTests.Corner(new DialogueBox("Mira", "Hi.")));
        shortLine.Ticks(Second);
        run.Ticks(10);
        var partway = run.ShownText.Single(text => Long.StartsWith(text.Text, StringComparison.Ordinal) && text.Text.Length < Long.Length);
        run.Ticks(Second * 3);
        var whole = Text(run, Long);

        Assert.Equal(whole.Frame, partway.Frame);
        Assert.True(whole.Frame.Height > Text(shortLine, "Hi.").Frame.Height * 1.5f, $"The line should wrap in 300 wide, but it is {whole.Frame}.");
    }

    [Fact]
    public void The_first_press_shows_the_whole_line_and_the_next_moves_on()
    {
        int next = 0;
        using var run = PointerGame.Show(_ => new DialogueBox("Mira", Long) { OnNext = () => next++ });
        run.Ticks(1);
        PointerGame.Press(run, Key.Enter);

        Assert.Equal(Long, Shown(run, Long));
        Assert.Equal(0, next);

        PointerGame.Press(run, Key.Enter);
        Assert.Equal(1, next);
    }

    [Fact]
    public void A_click_shows_the_whole_line()
    {
        using var run = PointerGame.Show(_ => new DialogueBox("Mira", Long).Id("box"));
        var frame = run.FrameOf("box");
        PointerGame.Click(run, new(frame.X + frame.Width / 2f, frame.Y + frame.Height / 2f));

        Assert.Equal(Long, Shown(run, Long));
    }

    [Fact]
    public void A_new_line_types_out_from_the_start()
    {
        string line = "Hello there.";
        using var run = PointerGame.Show(_ => new DialogueBox("Mira", line));
        run.Ticks(Second * 2);
        line = Long;
        run.Ticks(2);

        Assert.InRange(Shown(run, Long).Length, 0, 10);
    }

    [Fact]
    public void Choices_show_once_the_line_is_out_and_the_first_is_picked_with_enter()
    {
        string picked = "";
        DialogueChoice[] choices = [new("Take the mill road", () => picked = "mill"), new("Wait for morning", () => picked = "wait")];
        using var run = PointerGame.Show(_ => new DialogueBox("Mira", "Which way?") { Choices = choices });
        bool early = run.ShownText.Any(text => text.Text == "Take the mill road");
        run.Ticks(Second);

        Assert.False(early);
        Assert.Contains(run.ShownText, text => text.Text == "Wait for morning");

        PointerGame.Press(run, Key.Enter);
        Assert.Equal("mill", picked);
    }

    [Fact]
    public void Down_twice_then_enter_picks_the_second_choice_as_the_first_only_shows_the_ring()
    {
        string picked = "";
        DialogueChoice[] choices = [new("Yes", () => picked = "yes"), new("No", () => picked = "no")];
        using var run = PointerGame.Show(_ => new DialogueBox("Mira", "Ready?") { Choices = choices });
        run.Ticks(Second);
        PointerGame.Press(run, Key.Down);
        PointerGame.Press(run, Key.Down);
        PointerGame.Press(run, Key.Enter);

        Assert.Equal("no", picked);
    }

    [Fact]
    public void Without_animations_a_line_shows_whole()
    {
        using var run = PointerGame.Show(_ => new DialogueBox("Mira", Long).Animations(false));

        Assert.Equal(Long, Shown(run, Long));
    }

    [Fact]
    public void At_no_characters_a_second_a_line_shows_whole()
    {
        using var run = PointerGame.Show(_ => new DialogueBox("Mira", Long) { CharactersPerSecond = 0f });

        Assert.Equal(Long, Shown(run, Long));
    }

    [Fact]
    public void A_skip_button_shows_only_when_skipping_is_offered()
    {
        using var plain = PointerGame.Show(_ => new DialogueBox("Mira", "Hi."));
        using var skippable = PointerGame.Show(_ => new DialogueBox("Mira", "Hi.") { OnSkip = () => { } });

        Assert.DoesNotContain(plain.Elements, element => element.Label == "Hold to skip");
        Assert.Contains(skippable.Elements, element => element.Label == "Hold to skip" && element.Role == UiRole.Button);
    }

    [Fact]
    public void A_title_card_fades_in_and_away_by_itself()
    {
        using var run = PointerGame.Show(_ => new TitleCard("The Old Mill") { Subtitle = "Chapter 2", Seconds = 2f });
        run.Ticks(3);
        float coming = Text(run, "The Old Mill").Color.A;
        run.Ticks(Second);
        float full = Text(run, "The Old Mill").Color.A;
        run.Ticks(Second * 2);
        float gone = Text(run, "The Old Mill").Color.A;

        Assert.InRange(coming, 0.01f, 0.6f);
        Assert.Equal(1f, full, 3);
        Assert.Equal(0f, gone, 3);
        Assert.Contains(run.ShownText, text => text.Text == "Chapter 2");
    }

    [Fact]
    public void A_title_card_with_no_time_stays()
    {
        using var run = PointerGame.Show(_ => new TitleCard("Victory"));
        run.Ticks(Second * 20);

        Assert.Equal(1f, Text(run, "Victory").Color.A, 3);
    }

    [Fact]
    public void Without_animations_a_timed_title_card_shows_and_stays()
    {
        using var run = PointerGame.Show(_ => new TitleCard("Victory") { Seconds = 1f }.Animations(false));
        run.Ticks(Second * 3);

        Assert.Equal(1f, Text(run, "Victory").Color.A, 3);
    }

    [Fact]
    public void An_achievement_says_what_was_earned_and_lifts_away()
    {
        using var run = PointerGame.Show(_ => new Achievement("Miller") { Description = "Fix the mill wheel", Seconds = 2f });
        run.Ticks(Second);

        var element = Assert.Single(run.Elements, element => element.Role == UiRole.Toast);
        Assert.Equal("Miller", element.Label);
        Assert.Equal("Fix the mill wheel", element.Value);
        Assert.Equal(1f, Text(run, "Achievement unlocked").Color.A, 3);

        run.Ticks(Second * 2);
        Assert.Equal(0f, Text(run, "Miller").Color.A, 3);
    }

    [Fact]
    public void A_track_finds_the_line_said_at_a_time()
    {
        var track = new SubtitleTrack([new("Second", 2f, 4f), new("First", 0f, 1.5f), new("Over it", 3f, 5f)]);

        Assert.Equal(["First", "Second", "Over it"], track.Lines.Select(line => line.Text));
        Assert.Equal("First", track.At(0f)?.Text);
        Assert.Null(track.At(1.75f));
        Assert.Equal("Second", track.At(2.5f)?.Text);
        Assert.Equal("Over it", track.At(3.5f)?.Text);
        Assert.Null(track.At(5f));
        Assert.Equal(5f, track.Length);
    }

    [Fact]
    public void A_line_that_ends_before_it_starts_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => new SubtitleTrack([new("Backwards", 2f, 1.5f)]));

        Assert.StartsWith("The subtitle \"Backwards\" ends at 1.5 seconds, which isn't after it starts at 2.", error.Message);
    }

    [Fact]
    public void Subtitles_show_the_speaker_and_the_words_at_the_bottom()
    {
        var line = new SubtitleLine("Over here!", 0f, 2f) { Speaker = "Mira" };
        using var run = PointerGame.Show(_ => new Subtitles(line));
        run.Ticks(Second);
        var words = Text(run, "Over here!");

        Assert.Contains(run.ShownText, text => text.Text == "Mira:" && text.Bold);
        Assert.Equal(22f, words.Size);
        Assert.InRange(words.Frame.Y, run.ScreenSize.Y / run.UiScale * 0.75f, run.ScreenSize.Y / run.UiScale);
    }

    [Fact]
    public void No_line_shows_no_subtitles()
    {
        using var run = PointerGame.Show(_ => new Subtitles(null));

        Assert.Empty(run.ShownText);
    }
}
