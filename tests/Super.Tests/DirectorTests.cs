using System.Numerics;
using Super.Testing;

namespace Super.Tests;

public class DirectorTests
{
    private const float Tolerance = 0.0001f;

    private static Camera At(float x) => new(new Vector3(x, 0f, 0f), new Vector3(x, 0f, -1f));

    /// <summary>
    /// Plays a cutscene from Load: a one-second slide from x 0 to x 30, so a tick at 30 a
    /// second moves it one unit, then a cut to a still camera at x 100. The game notes
    /// which tick each moment reached its Update and the time scale every Update saw.
    /// </summary>
    private sealed class Film(Director director, GameTime time) : IGame
    {
        private bool _cued;

        public List<long> CuedTicks { get; } = [];

        public List<float> Scales { get; } = [];

        public Director Director => director;

        public void Load()
        {
            time.Scale = 0.8f;
            director.Play(new Cutscene()
                .Then(Shot.Move(1.05f, At(0f), At(31.5f), Easing.Linear))
                .Then(Shot.Hold(1f, At(100f)))
                .At(0f, () => _cued = true)
                .At(0.51f, () => _cued = true)
                .Slow(0.5f, 1.5f, 0.25f, ramp: 0.1f));
        }

        public void Update(GameTime gameTime)
        {
            if (_cued)
            {
                CuedTicks.Add(gameTime.Tick);
                _cued = false;
            }

            Scales.Add(gameTime.Scale);
        }

        public void Draw(View view) => view.Camera(new Vector3(7f, 7f, 7f), Vector3.Zero);
    }

    [Fact]
    public void Moments_reach_the_update_of_the_tick_they_fall_in()
    {
        using var run = HeadlessGame.Create<Film>(1);

        run.Ticks(20);

        // 0.51 s falls between tick 15 at 0.5 s and tick 16 at 0.533 s.
        Assert.Equal([0L, 16L], run.Game.CuedTicks.ToArray());
    }

    [Fact]
    public void The_camera_follows_the_shot_and_blends_between_ticks()
    {
        using var run = HeadlessGame.Create<Film>(1);
        run.Ticks(3);

        run.Frame(0.5f);

        // The last two ticks put it at x 1 and x 2.
        Assert.Equal(1.5f, run.Camera.Position.X, Tolerance);
    }

    [Fact]
    public void A_cut_jumps_without_blending()
    {
        using var run = HeadlessGame.Create<Film>(1);

        // Tick 31 is still in the slide; tick 32 at 1.067 s is the first of the still shot.
        run.Ticks(33);
        run.Frame(0.5f);

        Assert.Equal(100f, run.Camera.Position.X, Tolerance);
    }

    [Fact]
    public void Slow_motion_sets_the_time_scale_the_game_sees()
    {
        using var run = HeadlessGame.Create<Film>(1);

        run.Ticks(31);

        Assert.Equal(1f, run.Game.Scales[0]);
        Assert.Equal(0.25f, run.Game.Scales[30], Tolerance);
    }

    [Fact]
    public void At_the_end_the_game_gets_its_camera_and_time_scale_back()
    {
        using var run = HeadlessGame.Create<Film>(1);

        // The cutscene lasts 2.05 s, which is tick 61.5.
        run.Ticks(63);
        run.Frame(0.5f);

        Assert.False(run.Game.Director.IsPlaying);
        Assert.Equal(new Vector3(7f, 7f, 7f), run.Camera.Position);
        Assert.Equal(0.8f, run.Time.Scale);
    }

    [Fact]
    public void Stop_ends_it_at_once()
    {
        using var run = HeadlessGame.Create<Film>(1);
        run.Ticks(5);

        run.Game.Director.Stop();
        run.Frame(0f);

        Assert.False(run.Game.Director.IsPlaying);
        Assert.Equal(new Vector3(7f, 7f, 7f), run.Camera.Position);
    }

    [Fact]
    public void A_cutscene_with_no_shots_is_refused()
    {
        using var run = HeadlessGame.Create<Film>(1);

        var problem = Assert.Throws<ArgumentException>(() => run.Game.Director.Play(new Cutscene()));

        Assert.StartsWith("The cutscene has no shots. Add one with Then.", problem.Message);
    }

    [Fact]
    public void A_negative_time_scale_is_refused()
    {
        using var run = HeadlessGame.Create<Film>(1);

        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => run.Time.Scale = -1f);

        Assert.StartsWith("The time scale must be a number of at least zero.", problem.Message);
    }
}
