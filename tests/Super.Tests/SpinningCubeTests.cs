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
