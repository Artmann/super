using System.Numerics;

namespace Super.Tests;

public class SplineFollowerTests
{
    private const float Tolerance = 1e-3f;

    /// <summary>A straight 10 m spline along +x.</summary>
    private static readonly Spline Line = Spline.Through([Vector3.Zero, new Vector3(10f, 0f, 0f)]);

    [Fact]
    public void Once_moves_at_its_speed_and_stops_at_the_end()
    {
        var follower = new SplineFollower(Line, speed: 4f);

        follower.Advance(1f);
        Assert.Equal(4f, follower.Distance, Tolerance);
        Assert.Equal(4f, follower.Position.X, Tolerance);
        Assert.False(follower.Completed);

        follower.Advance(2f);
        Assert.Equal(10f, follower.Distance, Tolerance);
        Assert.True(follower.Completed);
    }

    [Fact]
    public void Loop_jumps_back_to_the_start_and_carries_on()
    {
        var follower = new SplineFollower(Line, speed: 4f, SplineLoop.Loop);

        follower.Advance(3f);

        // 12 m along a 10 m spline is 2 m into the second time round.
        Assert.Equal(2f, follower.Distance, Tolerance);
        Assert.False(follower.Completed);
    }

    [Fact]
    public void PingPong_turns_round_at_the_end_and_faces_back()
    {
        var follower = new SplineFollower(Line, speed: 4f, SplineLoop.PingPong);

        follower.Advance(3f);

        // 10 m out and 2 m back.
        Assert.Equal(8f, follower.Distance, Tolerance);
        Assert.True(follower.Backwards);
        Assert.False(follower.Completed);
        AssertNear(-Vector3.UnitX, follower.Pose.Forward);
        AssertNear(Vector3.UnitY, follower.Pose.Up);

        follower.Advance(3f);
        Assert.Equal(4f, follower.Distance, Tolerance);
        Assert.False(follower.Backwards);
        Assert.False(follower.Completed);
    }

    [Fact]
    public void A_looping_follower_keeps_its_place_after_a_long_time()
    {
        var follower = new SplineFollower(Line, speed: 3f, SplineLoop.Loop);

        // A tenth of a second at a time for nearly three hours: 30,000 m, which is 3,000 times round.
        for (int tick = 0; tick < 100_000; tick++)
        {
            follower.Advance(0.1f);
        }

        // Each step is the float nearest 0.3 m, and a lap is the measured length.
        double expected = (double)(3f * 0.1f) * 100_000 % Line.Length;
        float off = MathF.Abs(follower.Distance - (float)expected);
        off = MathF.Min(off, Line.Length - off);
        // Rounding each step adds up to millimetres; a distance left to grow would be metres out.
        Assert.True(off < 0.05f, $"It drifted {off} m from where it should be.");
    }

    [Fact]
    public void A_negative_speed_goes_back_to_the_start_facing_backwards()
    {
        var follower = new SplineFollower(Line, speed: -2f, distance: 5f);

        follower.Advance(1f);

        Assert.Equal(3f, follower.Distance, Tolerance);
        AssertNear(-Vector3.UnitX, follower.Pose.Forward);

        follower.Advance(5f);
        Assert.True(follower.Completed);
        Assert.Equal(0f, follower.Distance, Tolerance);
    }

    [Fact]
    public void Update_moves_by_the_ticks_game_time()
    {
        var follower = new SplineFollower(Line, speed: 4f);

        // A tenth of a second, at half speed.
        follower.Update(new GameTime(10) { Scale = 0.5f });

        Assert.Equal(0.2f, follower.Distance, Tolerance);
    }

    [Fact]
    public void Smooth_blends_between_the_last_tick_and_this_one()
    {
        var follower = new SplineFollower(Line, speed: 4f, SplineLoop.Loop, distance: 9f);

        follower.Advance(0.5f);

        // From 9 m to 11 m, which wrapped to 1 m; halfway is the end, back where it started.
        Assert.Equal(1f, follower.Distance, Tolerance);
        Assert.Equal(1f, follower.Smooth(1f).Position.X, Tolerance);
        Assert.Equal(9f, follower.Smooth(0f).Position.X, Tolerance);
        Assert.Equal(9.5f, follower.Smooth(0.25f).Position.X, Tolerance);
    }

    [Fact]
    public void Smooth_doesnt_run_past_the_end_after_stopping()
    {
        var follower = new SplineFollower(Line, speed: 4f, distance: 9f);

        follower.Advance(1f);

        Assert.Equal(9.5f, follower.Smooth(0.5f).Position.X, Tolerance);
    }

    [Theory]
    [InlineData(float.NaN, 0f, "speed", "The speed must be a finite number.")]
    [InlineData(1f, float.PositiveInfinity, "distance", "The distance must be a finite number.")]
    public void Followers_that_cant_move_are_explained(float speed, float distance, string parameter, string expected)
    {
        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => new SplineFollower(Line, speed, distance: distance));

        Assert.Equal(parameter, problem.ParamName);
        Assert.StartsWith(expected, problem.Message, StringComparison.Ordinal);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) <= Tolerance, $"Expected {expected}, got {actual}.");
}
