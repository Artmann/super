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

    [Theory, Seeds(10)]
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

    [Theory, Seeds(20)]
    public void Color_changes_land_half_a_second_to_three_seconds_apart(int seed)
    {
        var colors = ColorsOver(seed, OneMinute);

        // Index -1 stands for the moment the game loaded, where the first wait begins.
        int lastChange = -1;
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
    public void Holding_W_tips_the_drawn_cube_over()
    {
        using var steered = HeadlessGame.Create<SpinningCube>(3);
        steered.Input.Hold(Key.W);
        steered.Ticks(30);
        using var untouched = HeadlessGame.Create<SpinningCube>(3);
        untouched.Ticks(30);

        // A second of W turns the cube half a turn about X, so the two cubes face opposite ways.
        Assert.NotEqual(untouched.Drawn[0].Transform, steered.Drawn[0].Transform);
    }

    [Fact]
    public void After_a_second_with_no_input_the_cube_has_turned_36_degrees_about_Y_and_21_6_about_X()
    {
        using var run = HeadlessGame.Create<SpinningCube>(3);
        run.Ticks(30);

        // 0.1 turns a second is 36 degrees about Y, and the tip about X is 0.6 of that, 21.6 degrees.
        // Worked by hand: Y(36) * X(21.6) in the row-vector convention.
        float[,] expected =
        {
            { 0.80902f, 0.21638f, -0.54651f, 0f },
            { 0f, 0.92978f, 0.36812f, 0f },
            { 0.58779f, -0.29782f, 0.75220f, 0f },
            { 0f, 0f, 0f, 1f },
        };
        var actual = run.Drawn[0].Transform;
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                Assert.Equal(expected[row, column], actual[row, column], 1e-4f);
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
