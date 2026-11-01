using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Super.Testing;

namespace Super.Tests;

public class GameLoopTests
{
    private class Counter(GameRandom random, GameTime time) : IGame
    {
        public GameRandom Random { get; } = random;
        public GameTime Time { get; } = time;
        public int Loads { get; private set; }
        public List<long> TicksSeen { get; } = [];
        public int Draws { get; private set; }

        public void Load() => Loads++;

        public void Update(GameTime time) => TicksSeen.Add(time.Tick);

        public void Draw(View view)
        {
            Draws++;
            view.Model(Draws, ModelRef.Cube, Matrix4x4.Identity);
        }
    }

    public interface IWeather
    {
        string Today { get; }
    }

    private class Sunny : IWeather
    {
        public string Today => "sunny";
    }

    private class Forecast(IWeather weather) : IGame
    {
        public IWeather Weather { get; } = weather;

        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }
    }

    [Fact]
    public void Load_runs_once_before_the_first_tick()
    {
        using var run = HeadlessGame.Create<Counter>(1);

        Assert.Equal(1, run.Game.Loads);
        Assert.Empty(run.Game.TicksSeen);
    }

    [Fact]
    public void Load_is_not_run_again_by_ticks()
    {
        using var run = HeadlessGame.Create<Counter>(1);

        run.Ticks(3);

        Assert.Equal(1, run.Game.Loads);
    }

    [Fact]
    public void Ticks_count_up_from_zero()
    {
        using var run = HeadlessGame.Create<Counter>(1);

        run.Ticks(3);

        Assert.Equal([0L, 1L, 2L], run.Game.TicksSeen);
        Assert.Equal(3, run.Time.Tick);
        Assert.Equal(0.1, run.Time.TotalSeconds, 0.0001);
    }

    [Fact]
    public void Every_tick_is_drawn_and_Drawn_holds_only_the_last_frame()
    {
        using var run = HeadlessGame.Create<Counter>(1);
        int drawnByCreate = run.Game.Draws;

        run.Ticks(5);

        Assert.Equal(drawnByCreate + 5, run.Game.Draws);
        Assert.Equal(drawnByCreate + 5, Assert.Single(run.Drawn).Key);
    }

    [Fact]
    public void Creating_a_game_draws_once_so_there_is_something_to_see()
    {
        using var run = HeadlessGame.Create<Counter>(1);

        Assert.Equal(1, run.Game.Draws);
        Assert.Single(run.Drawn);
        Assert.Empty(run.Game.TicksSeen);
    }

    [Fact]
    public void A_frame_draws_without_ticking()
    {
        using var run = HeadlessGame.Create<Counter>(1);
        run.Ticks(2);

        run.Frame(0.5f);

        Assert.Equal(2, run.Time.Tick);
        Assert.Equal(2, run.Game.TicksSeen.Count);
        Assert.Equal(4, run.Game.Draws);
    }

    [Fact]
    public void The_game_gets_engine_services_and_the_seed_reaches_GameRandom()
    {
        using var run = HeadlessGame.Create<Counter>(1234);

        Assert.Equal(1234, run.Game.Random.Seed);
        Assert.Same(run.Time, run.Game.Time);
    }

    [Fact]
    public void A_game_gets_the_services_a_test_adds()
    {
        using var run = HeadlessGame.Create<Forecast>(1, services => services.AddSingleton<IWeather, Sunny>());

        Assert.Equal("sunny", run.Game.Weather.Today);
    }

    [Fact]
    public void Fakes_replace_the_engines_own_services_in_one_line()
    {
        using var run = HeadlessGame.Create<Counter>(1, services => services.AddSingleton(new GameRandom(99)));

        Assert.Equal(99, run.Game.Random.Seed);
    }

    [Fact]
    public void A_missing_service_fails_at_startup_naming_it()
    {
        var failure = Assert.Throws<AggregateException>(() => HeadlessGame.Create<Forecast>(1));

        Assert.Contains(nameof(IWeather), failure.Message);
    }

    [Fact]
    public void Invariants_stop_at_the_tick_that_broke_them()
    {
        using var run = HeadlessGame.Create<Counter>(417);
        run.Invariant("fewer than five ticks", game => game.TicksSeen.Count < 5);

        var failure = Assert.Throws<InvariantFailedException>(() => run.Ticks(100));

        Assert.Equal("fewer than five ticks", failure.Invariant);
        Assert.Equal(417, failure.Seed);
        Assert.Equal(5, failure.Tick);
        Assert.Contains("seed 417", failure.Message);
        Assert.Contains("--seed 417 --ticks 5", failure.Message);
    }

    [Fact]
    public void Several_games_share_a_process_without_touching_each_other()
    {
        using var first = HeadlessGame.Create<Counter>(1);
        using var second = HeadlessGame.Create<Counter>(2);

        first.Ticks(3);

        Assert.Empty(second.Game.TicksSeen);
        Assert.NotSame(first.Game.Random, second.Game.Random);
    }
}
