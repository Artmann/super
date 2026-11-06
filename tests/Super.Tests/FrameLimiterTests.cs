namespace Super.Tests;

public class FrameLimiterTests
{
    /// <summary>A clock that only moves when told, counting in 100 nanosecond ticks.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _now = 1_000_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _now;

        public void Advance(TimeSpan by) => _now += by.Ticks;
    }

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void Without_a_limit_a_frame_never_waits()
    {
        var clock = new ManualClock();
        var limiter = new FrameLimiter(clock);

        Assert.Equal(TimeSpan.Zero, limiter.Next(0));
        clock.Advance(Ms(1));
        Assert.Equal(TimeSpan.Zero, limiter.Next(0));
    }

    [Fact]
    public void Quick_frames_wait_out_the_rest_of_their_slot()
    {
        // At 50 a second each frame has 20 ms. Frames taking 5 ms wait 15 after the first.
        var clock = new ManualClock();
        var limiter = new FrameLimiter(clock);

        Assert.Equal(Ms(20), limiter.Next(50));
        clock.Advance(Ms(20 + 5));
        Assert.Equal(Ms(15), limiter.Next(50));
        clock.Advance(Ms(15 + 5));
        Assert.Equal(Ms(15), limiter.Next(50));
    }

    [Fact]
    public void A_frame_that_runs_a_little_long_is_made_up_by_the_next_wait()
    {
        // The slots are at 20, 40 and 60 ms. A 28 ms frame from 20 misses the slot at 40 and
        // doesn't wait, and the next, ending at 50, waits only to 60, so the rate holds on average.
        var clock = new ManualClock();
        var limiter = new FrameLimiter(clock);

        clock.Advance(limiter.Next(50));
        clock.Advance(Ms(28));
        Assert.Equal(TimeSpan.Zero, limiter.Next(50));
        clock.Advance(Ms(2));
        Assert.Equal(Ms(10), limiter.Next(50));
    }

    [Fact]
    public void After_a_long_stall_frames_keep_their_pace_instead_of_rushing_to_catch_up()
    {
        // A 100 ms frame from the slot at 20 ends at 120, four slots late. The count starts
        // again from now, so the next frame waits a whole period, not none.
        var clock = new ManualClock();
        var limiter = new FrameLimiter(clock);

        clock.Advance(limiter.Next(50));
        clock.Advance(Ms(100));
        Assert.Equal(Ms(20), limiter.Next(50));
    }

    [Fact]
    public void A_new_limit_takes_effect_from_the_next_frame()
    {
        // 50 a second gives a slot at 20 ms. Changed to 25 a second at 5 ms, the next slot
        // is a 25-a-second period of 40 ms from then.
        var clock = new ManualClock();
        var limiter = new FrameLimiter(clock);

        limiter.Next(50);
        clock.Advance(Ms(5));
        Assert.Equal(Ms(40), limiter.Next(25));
    }
}
