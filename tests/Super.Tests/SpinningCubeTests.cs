using System.Numerics;
using ColorCube;
using Super.Testing;

namespace Super.Tests;

public class SpinningCubeTests
{
    private const int OneMinute = 30 * 60;

    private static List<Vector3> ColorsOver(int seed, int ticks)
    {
        using var run = HeadlessGame.Create<SpinningCube>(seed);
        var colors = new List<Vector3>(ticks);
        for (int i = 0; i < ticks; i++)
        {
            run.Ticks(1);
            colors.Add(run.Game.Color);
        }

        return colors;
    }

    [Theory, Seeds(1000)]
    public void Always_shows_one_cube_in_a_valid_color_that_keeps_changing(int seed)
    {
        using var run = HeadlessGame.Create<SpinningCube>(seed);
        run.Invariant("the color stays between 0 and 1", game =>
            Vector3.Clamp(game.Color, Vector3.Zero, Vector3.One) == game.Color);

        Vector3 last = run.Game.Color;
        int steady = 0;
        for (int tick = 0; tick < OneMinute; tick++)
        {
            run.Ticks(1);
            steady = run.Game.Color == last ? steady + 1 : 0;
            last = run.Game.Color;

            // The longest wait is 90 ticks, and a new color could only rarely match the old one.
            Assert.InRange(steady, 0, 90 * 3);
        }

        var cube = Assert.Single(run.Drawn);
        Assert.Equal(ModelRef.Cube, cube.Model);
        Assert.Equal(run.Game.Color, cube.Color);
    }

    [Fact]
    public void Color_changes_land_half_a_second_to_three_seconds_apart()
    {
        var colors = ColorsOver(seed: 5, OneMinute);

        int lastChange = 0;
        int changes = 0;
        for (int tick = 1; tick < colors.Count; tick++)
        {
            if (colors[tick] != colors[tick - 1])
            {
                Assert.InRange(tick - lastChange, 15, 90);
                lastChange = tick;
                changes++;
            }
        }

        Assert.InRange(changes, 15, 120);
    }

    [Fact]
    public void Same_seed_replays_exactly()
    {
        Assert.Equal(ColorsOver(42, OneMinute), ColorsOver(42, OneMinute));
    }

    [Fact]
    public void Different_seeds_play_differently()
    {
        Assert.NotEqual(ColorsOver(1, OneMinute), ColorsOver(2, OneMinute));
    }

    [Theory, Seeds(100)]
    public void Pressing_cycle_changes_the_color_that_tick(int seed)
    {
        using var run = HeadlessGame.Create<SpinningCube>(seed);
        run.Ticks(1);
        var before = run.Game.Color;
        run.Input.Press(Control.Cycle);
        run.Ticks(1);
        Assert.NotEqual(before, run.Game.Color);
    }

    [Fact]
    public void Space_is_bound_to_cycle()
    {
        using var run = HeadlessGame.Create<SpinningCube>(3);
        run.Ticks(1);
        var before = run.Game.Color;
        run.Input.Press(Key.Space);
        run.Ticks(1);
        Assert.NotEqual(before, run.Game.Color);
    }

    [Fact]
    public void Holding_W_steers_the_spin()
    {
        using var run = HeadlessGame.Create<SpinningCube>(3);
        run.Input.Hold(Key.W);
        run.Ticks(1);
        Assert.Equal(new Vector2(0f, 1f), run.Game.Steer);
    }

    [Fact]
    public void The_cube_spins_the_same_as_before_with_no_input()
    {
        using var run = HeadlessGame.Create<SpinningCube>(3);
        run.Ticks(30);
        float angle = MathF.Tau * 0.1f * 30 * (1f / 30);
        var expected = Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateRotationX(angle * 0.6f);
        var actual = run.Drawn[0].Transform;
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                Assert.Equal(expected[row, column], actual[row, column], 1e-5f);
            }
        }
    }

    [Fact]
    public void Running_the_game_allocates_nothing_per_frame()
    {
        using var run = HeadlessGame.Create<SpinningCube>(1);
        run.Ticks(10_000);

        long before = GC.GetAllocatedBytesForCurrentThread();
        run.Ticks(10_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
