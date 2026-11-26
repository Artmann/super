using System.Globalization;
using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>Auto exposure: metering the frame's histogram and easing towards it, as the GPU does.</summary>
public class ExposureTests
{
    private const float Tolerance = 1e-5f;

    /// <summary>A meter that moves at one speed both ways, counts every pixel the same and has no limits.</summary>
    private static ExposureAim Steady(float speed) => new(speed, speed, 0f, -float.MaxValue, float.MaxValue);

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
        Assert.Equal(expected, Exposure.Adapt(0f, target, 2f, 2f, 0.5f), Tolerance);
    }

    [Fact]
    public void Adaptation_stops_at_a_near_target_instead_of_overshooting()
    {
        Assert.Equal(0.5f, Exposure.Adapt(0f, 0.5f, 2f, 2f, 0.5f));
    }

    [Fact]
    public void Adaptation_snaps_when_told()
    {
        Assert.Equal(3f, Exposure.Adapt(float.NaN, 3f, 2f, 2f, 1f / 60f));
    }

    [Fact]
    public void Adaptation_holds_still_without_a_target()
    {
        Assert.Equal(1.5f, Exposure.Adapt(1.5f, float.NaN, 2f, 2f, 0.5f));
    }

    [Fact]
    public void Sixty_steps_of_a_sixtieth_at_2_stops_a_second_move_2_stops()
    {
        float exposure = Exposure.Adapt(float.NaN, 0f, 2f, 2f, 1f / 60f);
        for (int i = 0; i < 60; i++)
        {
            exposure = Exposure.Adapt(exposure, 3f, 2f, 2f, 1f / 60f);
        }

        // A second at 2 stops a second moves 2 stops of the 3.
        Assert.Equal(2f, exposure, 1e-4f);
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
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => Exposure.Adapt(0f, 3f, speed, speed, 0.5f));

            Assert.StartsWith($"The adaptation speeds must be above 0 stops a second, but they are {shown} towards the light and {shown} towards the dark.", error.Message);
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
        Assert.Equal(1f, Exposure.Adapt(0f, 3f, Exposure.Speed(0f), 1f, 0.5f), Tolerance);
        Assert.Equal(5f, Exposure.Speed(5f));
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.PositiveInfinity)]
    public void A_speed_that_no_view_would_accept_adapts_at_two_stops_a_second_instead(float requested)
    {
        // Rendition used without Super's View has nothing refusing these. Each frame's step must stay
        // a finite number of stops 0 or more: half a second at 2 stops a second is 1.
        var clock = new ExposureClock();
        clock.Next(metered: true, time: 1f, aim: Steady(Exposure.Speed(requested)));

        var step = clock.Next(metered: true, time: 1.5f, aim: Steady(Exposure.Speed(requested)));

        Assert.Equal(1f, step!.Value.Rise, Tolerance);
        Assert.Equal(1f, Exposure.Adapt(0f, 3f, Exposure.Speed(requested), 1f, 0.5f), Tolerance);
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

        var step = clock.Next(metered: true, time: 12f, aim: Steady(2f));

        Assert.True(step!.Value.Snap);
        Assert.Equal(0f, step.Value.Rise);
    }

    [Fact]
    public void Later_frames_move_speed_times_the_time_since_the_last()
    {
        var clock = new ExposureClock();
        clock.Next(metered: true, time: 1f, aim: Steady(2f));

        var step = clock.Next(metered: true, time: 1.25f, aim: Steady(2f));

        Assert.False(step!.Value.Snap);
        Assert.Equal(0.5f, step.Value.Rise, Tolerance);
    }

    [Fact]
    public void Frames_drawn_again_at_the_same_time_hold_the_exposure_still()
    {
        // A screenshot settles by drawing the last tick's moment again and again.
        var clock = new ExposureClock();
        clock.Next(metered: true, time: 1f, aim: Steady(2f));

        var step = clock.Next(metered: true, time: 1f, aim: Steady(2f));

        Assert.False(step!.Value.Snap);
        Assert.Equal(0f, step.Value.Rise);
    }

    [Fact]
    public void Turning_metering_back_on_snaps_again()
    {
        var clock = new ExposureClock();
        clock.Next(metered: true, time: 1f, aim: Steady(2f));

        Assert.Null(clock.Next(metered: false, time: 2f, aim: Steady(2f)));
        var step = clock.Next(metered: true, time: 3f, aim: Steady(2f));

        Assert.True(step!.Value.Snap);
    }

    [Fact]
    public void Time_going_backwards_snaps_as_a_restart_does()
    {
        var clock = new ExposureClock();
        clock.Next(metered: true, time: 30f, aim: Steady(2f));

        var step = clock.Next(metered: true, time: 0f, aim: Steady(2f));

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
                () => view.Effects.Finish(new PostSettings { AutoExposure = true, AdaptationSpeed = speed }));

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

        view.Effects.Finish(new PostSettings { AutoExposure = true, Exposure = -0.3f });

        Assert.True(view.Effects.Post.AutoExposure);
        Assert.Equal(0f, view.Effects.Post.AdaptationSpeed);
    }

    [Theory]
    [InlineData(3f, 2f)]    // a second towards the light at 2 stops a second
    [InlineData(-3f, -0.5f)] // a second towards the dark at half a stop a second
    public void The_dark_is_followed_at_its_own_speed(float target, float expected)
    {
        Assert.Equal(expected, Exposure.Adapt(0f, target, 2f, 0.5f, 1f), Tolerance);
    }

    [Fact]
    public void A_speed_into_the_dark_must_be_above_zero_too()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => Exposure.Adapt(0f, -3f, 2f, -0.5f, 0.5f));

            Assert.StartsWith("The adaptation speeds must be above 0 stops a second, but they are 2 towards the light and -0.5 towards the dark.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Left_at_zero_the_meter_follows_the_light_at_2_and_the_dark_at_1()
    {
        var aim = Exposure.Aim(new PostSettings { AutoExposure = true });

        Assert.Equal(2f, aim.Brighter);
        Assert.Equal(1f, aim.Darker);
        Assert.Equal(0f, aim.Centre);
        Assert.Equal(-1e6f, aim.Limit(-1e6f));
        Assert.Equal(1e6f, aim.Limit(1e6f));
    }

    [Fact]
    public void A_faster_speed_into_the_light_leaves_the_dark_at_its_own()
    {
        var aim = Exposure.Aim(new PostSettings { AdaptationSpeed = 4f, Metering = new MeteringSettings { DarkSpeed = 0.25f } });

        Assert.Equal(4f, aim.Brighter);
        Assert.Equal(0.25f, aim.Darker);
    }

    [Fact]
    public void Each_frame_may_rise_and_fall_by_its_own_speed_times_the_time_since_the_last()
    {
        var clock = new ExposureClock();
        var aim = new ExposureAim(2f, 0.5f, 0f, -float.MaxValue, float.MaxValue);
        clock.Next(metered: true, time: 1f, aim);

        var step = clock.Next(metered: true, time: 1.25f, aim)!.Value;

        Assert.Equal(0.5f, step.Rise, Tolerance);
        Assert.Equal(0.125f, step.Fall, Tolerance);
    }

    [Theory]
    [InlineData(-5f, -3f)]
    [InlineData(4f, 2f)]
    [InlineData(0.5f, 0.5f)]
    public void The_target_is_held_to_the_darkest_and_brightest_scenes_asked_for(float target, float expected)
    {
        var aim = Exposure.Aim(new PostSettings { Metering = new MeteringSettings { Darkest = -3f, Brightest = 2f } });

        Assert.Equal(expected, aim.Limit(target));
    }

    [Fact]
    public void A_target_that_is_not_a_number_stays_one_so_the_exposure_holds()
    {
        var aim = Exposure.Aim(new PostSettings { Metering = new MeteringSettings { Darkest = -3f, Brightest = 2f } });

        Assert.True(float.IsNaN(aim.Limit(float.NaN)));
    }

    [Theory]
    [InlineData(3f, 1f, 1f)]                    // a darkest past the brightest: neither limit
    [InlineData(float.NaN, float.PositiveInfinity, 1f)]
    [InlineData(-2f, float.NaN, float.NaN)]     // centre weight not a number: none
    public void Metering_no_view_would_accept_is_made_safe_for_the_shaders(float darkest, float brightest, float centre)
    {
        // Rendition used without Super's View has nothing refusing these, so a limit that can't hold is left out
        // and the centre weight is held to 0 to 1.
        var aim = Exposure.Aim(new PostSettings { Metering = new MeteringSettings { Darkest = darkest, Brightest = brightest, Centre = centre } });

        Assert.Equal(float.IsNaN(darkest) || darkest > brightest ? -1e6f : -2f, aim.Limit(-1e6f));
        Assert.Equal(1e6f, aim.Limit(1e6f));
        Assert.Equal(float.IsNaN(centre) ? 0f : 1f, aim.Centre);
    }

    [Fact]
    public void A_centre_weight_above_one_counts_as_one()
    {
        var aim = Exposure.Aim(new PostSettings { Metering = new MeteringSettings { Centre = 3f } });

        Assert.Equal(1f, aim.Centre);
    }

    [Theory]
    [InlineData(0.5f, 0.5f, 0f, 1u)]    // no centre weight: every pixel once
    [InlineData(0.5f, 0.5f, 1f, 16u)]   // the very middle, all of it: 1 + 15
    [InlineData(0.5f, 0.5f, 0.5f, 9u)]  // half: 1 + 7.5, rounded up
    [InlineData(0f, 0f, 1f, 1u)]        // a corner counts once however centred
    [InlineData(1f, 0f, 1f, 1u)]
    [InlineData(0.75f, 0.5f, 1f, 14u)]  // a quarter right: 1 - 2 * 0.0625 = 0.875, 15 * 0.875 = 13.1
    [InlineData(0.5f, 0f, 1f, 9u)]      // the top edge's middle: 1 - 2 * 0.25 = 0.5, 15 * 0.5 = 7.5
    public void The_middle_counts_more_the_more_the_metering_centres(float u, float v, float centre, uint expected)
    {
        Assert.Equal(expected, Exposure.Weight(new Vector2(u, v), centre));
    }

    [Fact]
    public void A_bright_middle_lifts_the_target_once_the_metering_centres()
    {
        // A 4 by 4 frame, the middle 2 by 2 bright in bin 40 and the rest dark in bin 10. Bin 10's centre is
        // -8 + 10.5 * 0.375 = -4.0625 EV and bin 40's -8 + 40.5 * 0.375 = 7.1875.
        // Unweighted: 12 dark and 4 bright, the band from 6.4 to 15.2 keeps 5.6 dark and 3.2 bright,
        // (5.6 * -4.0625 + 3.2 * 7.1875) / 8.8 = 0.0284.
        // Fully centred the middle counts 15, the edges' middles 11 and the corners 8: 120 dark and 60 bright,
        // the band from 72 to 171 keeps 48 dark and 51 bright, (48 * -4.0625 + 51 * 7.1875) / 99 = 1.7330.
        Assert.Equal(0.25f / 8.8f, Meter(centre: 0f), 1e-4f);
        Assert.Equal(171.5625f / 99f, Meter(centre: 1f), 1e-4f);

        static float Meter(float centre)
        {
            var bins = new uint[Exposure.Bins];
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    bool middle = x is 1 or 2 && y is 1 or 2;
                    bins[middle ? 40 : 10] += Exposure.Weight(new Vector2((x + 0.5f) / 4f, (y + 0.5f) / 4f), centre);
                }
            }

            return Exposure.Target(bins, Exposure.MinEv, Exposure.MaxEv);
        }
    }

    [Theory]
    [InlineData(-1f, 0f, null, null, "The speed into the dark must be above 0 stops a second, or 0 for the default of 1, but it is -1.")]
    [InlineData(0f, 1.5f, null, null, "The metering's centre weight must be from 0, every pixel the same, to 1, but it is 1.5.")]
    [InlineData(0f, 0f, float.NaN, null, "The darkest scene to meter for must be a number of stops, or null for no limit, but it is NaN.")]
    [InlineData(0f, 0f, null, float.PositiveInfinity, "The brightest scene to meter for must be a number of stops, or null for no limit, but it is Infinity.")]
    [InlineData(0f, 0f, 2.5f, -1f, "The darkest scene to meter for, 2.5, can't be brighter than the brightest, -1.")]
    public void A_view_refuses_metering_it_cannot_follow(float darkSpeed, float centre, float? darkest, float? brightest, string expected)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var view = new View();
            var metering = new MeteringSettings { DarkSpeed = darkSpeed, Centre = centre, Darkest = darkest, Brightest = brightest };

            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => view.Effects.Finish(new PostSettings { AutoExposure = true, Metering = metering }));

            Assert.StartsWith(expected, error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_view_keeps_metering_it_can_follow()
    {
        var view = new View();
        var metering = new MeteringSettings { DarkSpeed = 0.5f, Centre = 1f, Darkest = -3f, Brightest = -3f };

        view.Effects.Finish(new PostSettings { AutoExposure = true, Metering = metering });

        Assert.Equal(metering, view.Effects.Post.Metering);
    }

    [Fact]
    public void Easing_metering_mixes_speeds_and_centre_and_limits_both_ends_have()
    {
        var from = new MeteringSettings { Darkest = -4f };
        var to = new MeteringSettings { DarkSpeed = 3f, Darkest = -2f, Brightest = 1f, Centre = 1f };

        var quarter = MeteringSettings.Lerp(from, to, 0.25f);

        Assert.Equal(1.5f, quarter.DarkSpeed, Tolerance); // from the default of 1 towards 3
        Assert.Equal(0.25f, quarter.Centre, Tolerance);
        Assert.Equal(-3.5f, quarter.Darkest!.Value, Tolerance);
        Assert.Null(quarter.Brightest);                    // only one end has it, so it waits for halfway
        Assert.Equal(1f, MeteringSettings.Lerp(from, to, 0.5f).Brightest);
    }

    [Fact]
    public void Easing_a_whole_finish_eases_its_metering()
    {
        var from = new PostSettings { Metering = new MeteringSettings { Centre = 0.2f } };
        var to = new PostSettings { Metering = new MeteringSettings { Centre = 0.6f } };

        Assert.Equal(0.4f, PostSettings.Lerp(from, to, 0.5f).Metering.Centre, Tolerance);
    }
}
