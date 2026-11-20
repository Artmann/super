using Super.Testing;

namespace Super.Tests;

public class SceneTransitionTests
{
    /// <summary>Runs <see cref="During"/> in every Update, with the tick, at 30 ticks a second.</summary>
    private sealed class Gate(Scenes scenes, Transitions transitions, GameTime time) : IGame
    {
        public Action<Gate, long>? During { get; set; }

        public Scenes Scenes => scenes;

        public Transitions Transitions => transitions;

        public List<(string Name, long Tick)> Builds { get; } = [];

        public void Update(GameTime gameTime) => During?.Invoke(this, gameTime.Tick);

        /// <summary>A build that notes its name and the tick it ran at the end of.</summary>
        public Action<Scene> Build(string name) => _ => Builds.Add((name, time.Tick));
    }

    private static HeadlessRun<Gate> Start(Action<Gate, long> during)
    {
        var run = HeadlessGame.Create<Gate>(1);
        run.Game.During = during;
        return run;
    }

    [Fact]
    public void A_fade_swaps_the_scene_at_the_end_of_its_cut_tick()
    {
        using var run = Start((game, tick) =>
        {
            if (tick == 10)
            {
                game.Scenes.Go(game.Build("crypt"), Transition.Fade(1f));
            }
        });

        run.Ticks(25);
        Assert.Empty(run.Game.Builds);

        // Ticks run 1/30 s apart, so half a second after tick 10 is tick 25.
        run.Ticks(1);
        Assert.Equal([("crypt", 25L)], run.Game.Builds);
    }

    [Fact]
    public void A_rebuild_with_a_fade_builds_the_same_scene_again_at_the_cut()
    {
        using var run = Start((game, tick) =>
        {
            if (tick == 2)
            {
                game.Scenes.Go(game.Build("crypt"));
            }

            if (tick == 10)
            {
                game.Scenes.Rebuild(Transition.Fade(1f));
            }
        });

        run.Ticks(25);
        Assert.Equal([("crypt", 2L)], run.Game.Builds);
        Assert.True(run.Game.Scenes.IsChanging);

        // Half a second after tick 10 is tick 25, as for a Go.
        run.Ticks(1);
        Assert.Equal([("crypt", 2L), ("crypt", 25L)], run.Game.Builds);
    }

    [Fact]
    public void A_wipe_swaps_at_once_and_holds_the_old_picture()
    {
        using var run = Start((game, tick) =>
        {
            if (tick == 10)
            {
                game.Scenes.Go(game.Build("crypt"), Transition.Wipe(1f));
            }
        });
        run.Ticks(10);
        run.Frame(0f);
        int before = run.Transition.Hold;

        run.Ticks(1);
        run.Frame(0f);

        Assert.Equal([("crypt", 10L)], run.Game.Builds);
        Assert.Equal(before + 1, run.Transition.Hold);
        Assert.Equal(Rendition.TransitionStyle.Wipe, run.Transition.Style);
    }

    [Fact]
    public void A_second_Go_during_the_fade_replaces_the_first_and_keeps_the_fade()
    {
        var fade = Transition.Fade(1f);
        Transition? playingAfterFirst = null;
        using var run = Start((game, tick) =>
        {
            if (tick == 10)
            {
                game.Scenes.Go(game.Build("crypt"), fade);
                playingAfterFirst = game.Transitions.Playing;
            }

            if (tick == 15)
            {
                game.Scenes.Go(game.Build("tower"), Transition.Fade(3f));
            }
        });

        run.Ticks(20);
        Assert.Same(fade, playingAfterFirst);
        Assert.Same(fade, run.Game.Transitions.Playing);

        run.Ticks(10);
        Assert.Equal([("tower", 25L)], run.Game.Builds);
    }

    [Fact]
    public void A_fade_stopped_early_still_swaps_at_the_end_of_the_tick()
    {
        int builtDuringStop = -1;
        using var run = Start((game, tick) =>
        {
            if (tick == 10)
            {
                game.Scenes.Go(game.Build("crypt"), Transition.Fade(1f));
            }

            if (tick == 12)
            {
                game.Transitions.Stop();
                builtDuringStop = game.Builds.Count;
            }
        });

        run.Ticks(13);

        Assert.Equal(0, builtDuringStop);
        Assert.Equal([("crypt", 12L)], run.Game.Builds);
    }

    [Fact]
    public void IsChanging_is_true_until_the_fade_ends()
    {
        using var run = Start((game, tick) =>
        {
            if (tick == 10)
            {
                game.Scenes.Go(game.Build("crypt"), Transition.Fade(1f));
            }
        });

        run.Ticks(10);
        Assert.False(run.Scenes.IsChanging);

        // Past the cut at tick 25, the fade is still coming back in.
        run.Ticks(20);
        Assert.Single(run.Game.Builds);
        Assert.True(run.Scenes.IsChanging);

        // A second after tick 10 is tick 40, and the tick after it sees the fade finished.
        run.Ticks(12);
        Assert.False(run.Scenes.IsChanging);
    }

    [Fact]
    public void IsChanging_is_true_from_Go_until_the_swap_without_a_transition()
    {
        using var run = HeadlessGame.Create<Gate>(1);

        run.Scenes.Go(run.Game.Build("crypt"));
        Assert.True(run.Scenes.IsChanging);

        run.Ticks(1);
        Assert.False(run.Scenes.IsChanging);
    }
}
