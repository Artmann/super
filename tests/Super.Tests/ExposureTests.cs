using System.Globalization;
using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>Auto exposure: metering the frame's histogram and easing towards it, as the GPU does.</summary>
public class ExposureTests
{
    private const float Tolerance = 1e-5f;

    [Fact]
    public void A_frame_all_in_one_bin_targets_that_bin()
    {
        var bins = new uint[64];
        bins[32] = 1000;

        // 64 bins over -8..+16 EV are 0.375 EV wide, so bin 32's centre is -8 + 32.5 * 0.375.
        Assert.Equal(4.1875f, Exposure.Target(bins, -8f, 16f), Tolerance);
    }

    [Fact]
    public void Target_ignores_the_darkest_40_and_brightest_5_percent()
    {
        // The darkest 40 per cent sit in bin 0 and the brightest 5 per cent, such as the sun's
        // disc, in bin 63, so only bin 40 is left: -8 + 40.5 * 0.375.
        var bins = new uint[64];
        bins[0] = 400;
        bins[40] = 550;
        bins[63] = 50;

        Assert.Equal(7.1875f, Exposure.Target(bins, -8f, 16f), Tolerance);
    }

    [Fact]
    public void Target_averages_what_is_left_weighted_by_pixels()
    {
        // 400 dark pixels are cut, then 300 in bin 10 and 250 in bin 20 are averaged, and the
        // brightest 50 are cut: (300 * -4.0625 + 250 * -0.3125) / 550 = -2.357955.
        var bins = new uint[64];
        bins[0] = 400;
        bins[10] = 300;
        bins[20] = 250;
        bins[63] = 50;

        Assert.Equal(-2.357955f, Exposure.Target(bins, -8f, 16f), 1e-4f);
    }

    [Fact]
    public void An_empty_histogram_has_no_target()
    {
        Assert.True(float.IsNaN(Exposure.Target(new uint[64], -8f, 16f)));
    }

    [Theory]
    [InlineData(1f, 1f, 1f, 21u)]       // EV 0 is 8 EV above the bottom: 8 / 0.375 = 21.3
    [InlineData(1f, 0f, 0f, 15u)]       // red alone counts 0.2126: log2(0.2126) = -2.23, 5.77 / 0.375 = 15.4
    [InlineData(30000f, 30000f, 30000f, 60u)] // the sun's disc: log2(30000) = 14.87, 22.87 / 0.375 = 60.99
    [InlineData(1e9f, 1e9f, 1e9f, 63u)]  // far past the top stays in the last bin
    [InlineData(float.PositiveInfinity, 0f, 0f, 63u)]
    [InlineData(0f, 0f, 0f, 0u)]         // black
    [InlineData(-1f, -1f, -1f, 0u)]
    [InlineData(float.NaN, 0f, 0f, 0u)]
    [InlineData(1e-6f, 1e-6f, 1e-6f, 0u)] // below the bottom stays in the first bin
    public void Every_pixel_lands_in_a_bin_however_bright_or_broken(float red, float green, float blue, uint expected)
    {
        Assert.Equal(expected, Exposure.Bin(new Vector3(red, green, blue)));
    }

    [Theory]
    [InlineData(3f, 1f)]
    [InlineData(-3f, -1f)]
    public void Adaptation_moves_at_most_speed_times_dt_stops(float target, float expected)
    {
        Assert.Equal(expected, Exposure.Adapt(0f, target, 2f, 0.5f), Tolerance);
    }

    [Fact]
    public void Adaptation_stops_at_a_near_target_instead_of_overshooting()
    {
        Assert.Equal(0.5f, Exposure.Adapt(0f, 0.5f, 2f, 0.5f));
    }

    [Fact]
    public void Adaptation_snaps_when_told()
    {
        Assert.Equal(3f, Exposure.Adapt(float.NaN, 3f, 2f, 1f / 60f));
    }

    [Fact]
    public void Adaptation_holds_still_without_a_target()
    {
        Assert.Equal(1.5f, Exposure.Adapt(1.5f, float.NaN, 2f, 0.5f));
    }

    [Fact]
    public void Sixty_fixed_steps_from_the_same_frame_give_the_same_exposure_every_run()
    {
        static float Run()
        {
            float exposure = float.NaN;
            exposure = Exposure.Adapt(exposure, 0f, 2f, 1f / 60f);
            for (int i = 0; i < 60; i++)
            {
                exposure = Exposure.Adapt(exposure, 3f, 2f, 1f / 60f);
            }

            return exposure;
        }

        float first = Run();
        float second = Run();

        // A second at 2 stops a second moves 2 stops of the 3.
        Assert.Equal(BitConverter.SingleToInt32Bits(first), BitConverter.SingleToInt32Bits(second));
        Assert.Equal(2f, first, 1e-4f);
    }

    [Theory]
    [InlineData(0f, "0")]
    [InlineData(-0.5f, "-0.5")]
    [InlineData(float.NaN, "NaN")]
    [InlineData(float.PositiveInfinity, "Infinity")]
    public void Adaptation_speed_must_be_above_zero(float speed, string shown)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => Exposure.Adapt(0f, 3f, speed, 0.5f));

            Assert.StartsWith($"The adaptation speed must be above 0 stops a second, but it is {shown}.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void An_adaptation_speed_left_at_zero_adapts_at_two_stops_a_second()
    {
        // Half a second at 2 stops a second.
        Assert.Equal(1f, Exposure.Adapt(0f, 3f, Exposure.Speed(0f), 0.5f), Tolerance);
        Assert.Equal(5f, Exposure.Speed(5f));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void The_frame_is_metered_only_when_the_scene_asks_and_the_switch_allows(bool asked, bool switchedOff, bool metered)
    {
        var post = new PostSettings { AutoExposure = asked };
        var off = RenderEffects.Bloom | (switchedOff ? RenderEffects.AutoExposure : RenderEffects.None);

        var shaderOff = Exposure.ShaderOff(post, off);

        Assert.Equal(metered, !shaderOff.HasFlag(RenderEffects.AutoExposure));
        Assert.True(shaderOff.HasFlag(RenderEffects.Bloom));
        Assert.Equal(off | RenderEffects.AutoExposure, shaderOff | RenderEffects.AutoExposure);
    }

    [Fact]
    public void The_first_metered_frame_snaps_to_the_target()
    {
        var clock = new ExposureClock();

        var step = clock.Next(metered: true, time: 12f, speed: 2f);

        Assert.True(step!.Value.Snap);
        Assert.Equal(0f, step.Value.MaxStep);
    }

    [Fact]
    public void Later_frames_move_speed_times_the_time_since_the_last()
    {
        var clock = new ExposureClock();
        clock.Next(metered: true, time: 1f, speed: 2f);

        var step = clock.Next(metered: true, time: 1.25f, speed: 2f);

        Assert.False(step!.Value.Snap);
        Assert.Equal(0.5f, step.Value.MaxStep, Tolerance);
    }

    [Fact]
    public void Frames_drawn_again_at_the_same_time_hold_the_exposure_still()
    {
        // A screenshot settles by drawing the last tick's moment again and again.
        var clock = new ExposureClock();
        clock.Next(metered: true, time: 1f, speed: 2f);

        var step = clock.Next(metered: true, time: 1f, speed: 2f);

        Assert.False(step!.Value.Snap);
        Assert.Equal(0f, step.Value.MaxStep);
    }

    [Fact]
    public void Turning_metering_back_on_snaps_again()
    {
        var clock = new ExposureClock();
        clock.Next(metered: true, time: 1f, speed: 2f);

        Assert.Null(clock.Next(metered: false, time: 2f, speed: 2f));
        var step = clock.Next(metered: true, time: 3f, speed: 2f);

        Assert.True(step!.Value.Snap);
    }

    [Fact]
    public void Time_going_backwards_snaps_as_a_restart_does()
    {
        var clock = new ExposureClock();
        clock.Next(metered: true, time: 30f, speed: 2f);

        var step = clock.Next(metered: true, time: 0f, speed: 2f);

        Assert.True(step!.Value.Snap);
    }

    [Theory]
    [InlineData(-1f, "-1")]
    [InlineData(float.NaN, "NaN")]
    [InlineData(float.PositiveInfinity, "Infinity")]
    public void A_view_refuses_an_adaptation_speed_below_zero(float speed, string shown)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var view = new View();

            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => view.Finish(new PostSettings { AutoExposure = true, AdaptationSpeed = speed }));

            Assert.StartsWith(
                $"The adaptation speed must be above 0 stops a second, or 0 for the default of 2, but it is {shown}.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_view_keeps_an_adaptation_speed_of_zero_for_the_default()
    {
        var view = new View();

        view.Finish(new PostSettings { AutoExposure = true, Exposure = -0.3f });

        Assert.True(view.Post.AutoExposure);
        Assert.Equal(0f, view.Post.AdaptationSpeed);
    }
}
