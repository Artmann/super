namespace Super.Tests;

public class FixedStepClockTests
{
    [Fact]
    public void A_fast_display_gets_a_tick_only_when_one_is_due()
    {
        var clock = new FixedStepClock(30);
        int ticks = 0;
        for (int frame = 0; frame < 240; frame++)
        {
            ticks += clock.Advance(1.0 / 240);
        }

        Assert.InRange(ticks, 29, 30);
    }

    [Fact]
    public void A_slow_display_gets_several_ticks_in_one_frame()
    {
        var clock = new FixedStepClock(30);

        Assert.Equal(3, clock.Advance(0.1001));
    }

    [Fact]
    public void Leftover_time_is_kept_and_shows_up_as_blend()
    {
        var clock = new FixedStepClock(10);

        Assert.Equal(0, clock.Advance(0.05));
        Assert.Equal(0.5f, clock.Blend, 0.001f);

        Assert.Equal(1, clock.Advance(0.05001));
        Assert.Equal(0f, clock.Blend, 0.001f);
    }

    [Fact]
    public void A_long_stall_doesnt_become_a_flood_of_ticks()
    {
        var clock = new FixedStepClock(30);

        Assert.InRange(clock.Advance(60.0), 7, 8);
    }

    [Fact]
    public void GameTime_rejects_a_tick_rate_of_zero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameTime(0));
    }
}
