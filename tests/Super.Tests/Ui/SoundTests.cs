using System.Numerics;
using Super.Testing;
using Super.Ui;
using static Super.Tests.Ui.PointerGame;

namespace Super.Tests.Ui;

/// <summary>The sounds the controls make, heard through the game's real audio on the interface channel.</summary>
public sealed class SoundTests
{
    private static UiView Corner(UiView view) =>
        view.Frame(width: 300f).Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    private static UiView TwoButtons(List<string> log, bool secondOff = false) => Corner(new VStack
    {
        new Button("Play", () => log.Add("play")).Id("play"),
        new Button("Quit", () => log.Add("quit"), Disabled: secondOff).Id("quit"),
    });

    /// <summary>Every interface sound heard while <paramref name="act"/> runs and for a few ticks after, in the order they started.</summary>
    private static List<string> Heard(HeadlessRun<PointerGame> run, Action act)
    {
        var heard = new List<string>();
        var before = run.Sounds.Where(sound => sound.Channel == Channel.Interface).ToList();
        act();
        for (int i = 0; i < 6; i++)
        {
            foreach (var sound in run.Sounds.Where(sound => sound.Channel == Channel.Interface))
            {
                if (!before.Contains(sound) && !heard.Contains(sound.Clip))
                {
                    heard.Add(sound.Clip);
                }
            }

            run.Ticks(1);
        }

        return heard;
    }

    /// <summary>The middle of a view's frame.</summary>
    private static Vector2 Middle(HeadlessRun<PointerGame> run, string id)
    {
        var frame = run.FrameOf(id);
        return new Vector2(frame.X + frame.Width / 2f, frame.Y + frame.Height / 2f);
    }

    [Fact]
    public void Clicking_a_button_plays_the_press_sound()
    {
        var log = new List<string>();
        using var run = PointerGame.Show(_ => TwoButtons(log));
        run.Input.MoveMouse(Middle(run, "play"));
        run.Ticks(6);

        var heard = Heard(run, () => Click(run, Middle(run, "play")));

        Assert.Equal(["interface/press"], heard);
        Assert.Equal(["play"], log);
    }

    [Fact]
    public void Clicking_a_disabled_button_plays_the_sound_for_no()
    {
        var log = new List<string>();
        using var run = PointerGame.Show(_ => TwoButtons(log, secondOff: true));
        run.Input.MoveMouse(Middle(run, "quit"));
        run.Ticks(6);

        var heard = Heard(run, () => Click(run, Middle(run, "quit")));

        Assert.Equal(["interface/denied"], heard);
        Assert.Empty(log);
    }

    [Fact]
    public void The_pointer_coming_onto_a_button_plays_the_hover_sound_and_a_disabled_one_is_quiet()
    {
        using var run = PointerGame.Show(_ => TwoButtons([], secondOff: true));

        var onPlay = Heard(run, () => run.Input.MoveMouse(Middle(run, "play")));
        var onQuit = Heard(run, () => run.Input.MoveMouse(Middle(run, "quit")));

        Assert.Equal(["interface/hover"], onPlay);
        Assert.Empty(onQuit);
    }

    [Fact]
    public void Moving_focus_plays_the_focus_sound_and_Enter_the_press_sound()
    {
        using var run = PointerGame.Show(_ => TwoButtons([]));

        var tab = Heard(run, () => Press(run, Key.Tab));
        var enter = Heard(run, () => Press(run, Key.Enter));

        Assert.Equal(["interface/focus"], tab);
        Assert.Equal(["interface/press"], enter);
    }

    [Fact]
    public void Left_and_right_on_a_slider_play_the_step_sound()
    {
        float volume = 0.5f;
        using var run = PointerGame.Show(_ => Corner(new Slider(volume, value => volume = value)));
        Press(run, Key.Tab);

        var heard = Heard(run, () => Press(run, Key.Right));

        Assert.Equal(["interface/step"], heard);
        Assert.True(volume > 0.5f);
    }

    [Fact]
    public void A_theme_with_no_sounds_is_silent()
    {
        var log = new List<string>();
        var quiet = Theme.Dark with { Sounds = UiSounds.None };
        using var run = PointerGame.Show(_ => TwoButtons(log).Theme(quiet));

        var heard = Heard(run, () =>
        {
            Press(run, Key.Tab);
            Press(run, Key.Enter);
            Click(run, Middle(run, "quit"));
        });

        Assert.Empty(heard);
        Assert.Equal(["play", "quit"], log);
    }

    [Fact]
    public void A_theme_can_give_a_control_another_sound()
    {
        var theme = Theme.Dark with { Sounds = UiSounds.Default with { Press = UiSounds.Default.Confirm } };
        using var run = PointerGame.Show(_ => TwoButtons([]).Theme(theme));
        Press(run, Key.Tab);

        var heard = Heard(run, () => Press(run, Key.Enter));

        Assert.Equal(["interface/confirm"], heard);
    }

    [Fact]
    public void Escape_closing_a_sheet_plays_the_back_sound()
    {
        bool open = true;
        using var run = PointerGame.Show(_ => Corner(new Text("Game").Sheet(open, () => open = false, new Button("Done").Id("done"))));
        run.Ticks(10);

        var heard = Heard(run, () => Press(run, Key.Escape));

        Assert.False(open);
        Assert.Equal(["interface/back"], heard);
    }

    [Fact]
    public void A_popup_the_game_opens_plays_the_open_sound_but_one_a_press_opens_is_heard_as_the_press()
    {
        bool open = false;
        using var run = PointerGame.Show(_ => Corner(new Button("Options", () => open = true).Id("options").Sheet(open, () => open = false, new Text("Sheet"))));

        Press(run, Key.Tab);
        var pressed = Heard(run, () => Press(run, Key.Enter));
        Press(run, Key.Escape);
        run.Ticks(20);
        var opened = Heard(run, () => open = true);

        Assert.Equal(["interface/press"], pressed);
        Assert.Equal(["interface/open"], opened);
    }

    [Fact]
    public void A_sound_asked_for_twice_before_the_frame_plays_once()
    {
        var tree = new UiTree();

        tree.Cues.Raise(UiSounds.Default, UiCue.Focus);
        tree.Cues.Raise(UiSounds.Default, UiCue.Focus);
        tree.Cues.Raise(UiSounds.Default, UiCue.Step);

        Assert.Equal(["interface/focus", "interface/step"], tree.Cues.Take().Select(sound => sound.Name));
        Assert.Empty(tree.Cues.Take());
    }

    [Fact]
    public void Each_built_in_sound_is_short_below_full_scale_and_starts_and_ends_at_silence()
    {
        var sounds = new[] { UiSounds.Default.Hover, UiSounds.Default.Focus, UiSounds.Default.Press, UiSounds.Default.Back, UiSounds.Default.Open, UiSounds.Default.Denied, UiSounds.Default.Step, UiSounds.Default.Confirm };
        foreach (var sound in sounds)
        {
            var samples = sound!.Make!();
            float peak = samples.Max(MathF.Abs);

            Assert.InRange(samples.Length, UiSynth.SampleRate / 100, UiSynth.SampleRate / 2);
            Assert.InRange(peak, 0.05f, 0.5f);

            // Starting or stopping on a loud sample clicks.
            Assert.True(MathF.Abs(samples[0]) < 0.02f, $"{sound.Name} starts at {samples[0]}.");
            Assert.True(MathF.Abs(samples[^1]) < 0.02f, $"{sound.Name} ends at {samples[^1]}.");
        }
    }
}
