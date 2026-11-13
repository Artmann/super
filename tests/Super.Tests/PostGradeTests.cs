using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

public class PostGradeTests
{
    [Fact]
    public void Default_post_settings_keep_the_soft_glow_and_add_no_tint()
    {
        var (lift, gain) = PostGrade.Tint(new PostSettings());

        Assert.Equal(new Vector4(0f, 0f, 0f, 0.05f), lift);
        Assert.Equal(new Vector4(1f, 1f, 1f, 0f), gain);
    }

    [Fact]
    public void Bloom_adds_to_the_default_glow()
    {
        var (lift, _) = PostGrade.Tint(new PostSettings { Bloom = 0.1f });

        Assert.Equal(0.15f, lift.W, 5);
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(2f, 1f)]
    public void Bloom_stops_at_no_glow_and_at_all_glow(float bloom, float strength)
    {
        var (lift, _) = PostGrade.Tint(new PostSettings { Bloom = bloom });

        Assert.Equal(strength, lift.W);
    }

    [Fact]
    public void Lift_is_passed_on_as_the_colour_added_to_the_darks()
    {
        var (lift, _) = PostGrade.Tint(new PostSettings { Lift = new Vector3(0.04f, 0.02f, 0.06f) });

        Assert.Equal(new Vector3(0.04f, 0.02f, 0.06f), new Vector3(lift.X, lift.Y, lift.Z));
    }

    [Fact]
    public void Gain_brightens_each_channel_by_its_share()
    {
        var (_, gain) = PostGrade.Tint(new PostSettings { Gain = new Vector3(0.1f, 0f, -0.2f) });

        Assert.Equal(1.1f, gain.X, 5);
        Assert.Equal(1f, gain.Y, 5);
        Assert.Equal(0.8f, gain.Z, 5);
    }

    [Fact]
    public void A_gain_below_minus_one_turns_the_channel_off_rather_than_negative()
    {
        var (_, gain) = PostGrade.Tint(new PostSettings { Gain = new Vector3(-3f, 0f, 0f) });

        Assert.Equal(0f, gain.X);
    }

    [Fact]
    public void The_lift_gain_and_haze_follow_the_finish_in_the_frame_block_of_common_glsl()
    {
        // std140: eight mat4s (two, four shadow cascades, the previous one and the view) of 64
        // bytes, then the vec4s cameraPosition, backgroundRadiance, parameters, grade and finish.
        int finish = (8 * 64) + (4 * 16);
        Assert.Equal(finish, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Finish)));
        Assert.Equal(finish + 16, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Lift)));
        Assert.Equal(finish + 32, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Gain)));
        Assert.Equal(finish + 48, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.HazeOrigin)));
        Assert.Equal(finish + 64, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.HazeColor)));
        Assert.Equal(finish + 80, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.WaterAbsorption)));
    }

    [Fact]
    public void The_view_clusters_light_counts_and_effects_left_out_sit_where_common_glsl_reads_them()
    {
        Assert.Equal(7 * 64, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.View)));

        // After shadowRadii come shadowFade, shadowDepthScales, clusters, screen, occlusion and the
        // light probes' two, the four probe cascade origins and the cascade counts, the clear sky's sun
        // and its disc, the fog, the white balance mat4, the curve, the volumetric fog's vec4, the
        // puddles' vec4, the wind's vec4, the baked probes' four, the bake's fade and the two flat environment colours, then
        // eight ints, the last the local light count, then the effects left out, the mip bias, the probe light count and
        // the previous time, so the lights start on a 16 byte boundary: 416 + 80 + 8 * 4 + 4 * 4 = 544 past shadowRadii.
        int radii = (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.ShadowRadii));
        Assert.Equal(radii + 16, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.ShadowFade)));
        Assert.Equal(radii + 32, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.ShadowDepthScales)));
        Assert.Equal(radii + 48, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Clusters)));
        Assert.Equal(radii + 64, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Screen)));
        Assert.Equal(radii + 80, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Occlusion)));
        Assert.Equal(radii + 96, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.LightProbeMin)));
        Assert.Equal(radii + 112, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.LightProbeCounts)));
        Assert.Equal(radii + 128, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.ProbeCascadeOrigins)));
        Assert.Equal(radii + 192, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.ProbeCascadeCounts)));
        Assert.Equal(radii + 208, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.SkySun)));
        Assert.Equal(radii + 224, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.SunDisc)));
        Assert.Equal(radii + 240, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Fog)));
        Assert.Equal(radii + 256, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.WhiteBalance)));
        Assert.Equal(radii + 320, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Curve)));
        Assert.Equal(radii + 336, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Volumetric)));
        Assert.Equal(radii + 352, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Puddles)));
        Assert.Equal(radii + 368, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Wind)));
        Assert.Equal(radii + 384, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.BakedProbeMin)));
        Assert.Equal(radii + 400, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.BakedProbeCounts)));
        Assert.Equal(radii + 416, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.BakedSpaceMin)));
        Assert.Equal(radii + 432, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.BakedSpaceMax)));
        Assert.Equal(radii + 448, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.BakedFade)));
        Assert.Equal(radii + 464, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.FlatIrradiance)));
        Assert.Equal(radii + 480, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.FlatReflection)));
        Assert.Equal(radii + 496, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.LightCount)));
        Assert.Equal(radii + 524, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.LocalLightCount)));
        Assert.Equal(radii + 528, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Off)));
        Assert.Equal(radii + 536, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.ProbeLightCount)));
        Assert.Equal(radii + 540, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.PreviousTime)));
        Assert.Equal(radii + 544, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Lights)));
    }

    [Fact]
    public void Post_settings_left_alone_use_agx_with_the_punchy_look()
    {
        var curve = PostGrade.Curve(default);

        Assert.Equal(new Vector4(0f, 1.35f, 1.4f, 0f), curve);
    }

    [Fact]
    public void The_base_agx_look_leaves_the_curve_alone()
    {
        var curve = PostGrade.Curve(new PostSettings { AgxLook = AgxLook.Base });

        Assert.Equal(new Vector4(0f, 1f, 1f, 0f), curve);
    }

    [Fact]
    public void Asking_for_aces_tells_the_shader_to_use_its_curve()
    {
        var curve = PostGrade.Curve(new PostSettings { Tonemapper = Tonemapper.Aces });

        Assert.Equal(1f, curve.X);
    }

    [Fact]
    public void The_bluest_temperature_turns_a_12000_kelvin_white_into_the_6500_kelvin_one()
    {
        // Worked by hand from the CIE table of black-body colours: 12000 K is at xy (0.2719, 0.2777)
        // and 6500 K at (0.3135, 0.3237). Each at Y = 1, through the Rec.709 matrix from XYZ.
        var bluish = new Vector3(0.8271f, 0.9944f, 1.5652f);
        var neutral = new Vector3(1.0425f, 0.9839f, 1.0348f);

        var balanced = Vector3.Transform(bluish, PostGrade.WhiteBalance(1f, 0f));

        Assert.Equal(neutral.X, balanced.X, 0.01f);
        Assert.Equal(neutral.Y, balanced.Y, 0.01f);
        Assert.Equal(neutral.Z, balanced.Z, 0.01f);
    }

    [Fact]
    public void Neutral_white_balance_is_identity()
    {
        var balance = PostGrade.WhiteBalance(0f, 0f);

        AssertNear(Matrix4x4.Identity, balance);
    }

    [Theory]
    [InlineData(0.08f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void A_warm_temperature_raises_red_over_blue_for_white(float temperature)
    {
        var white = Vector3.Transform(Vector3.One, PostGrade.WhiteBalance(temperature, 0f));

        Assert.True(white.X > white.Z, $"Red {white.X} should be above blue {white.Z}.");
    }

    [Fact]
    public void A_cool_temperature_raises_blue_over_red_for_white()
    {
        var white = Vector3.Transform(Vector3.One, PostGrade.WhiteBalance(-0.5f, 0f));

        Assert.True(white.Z > white.X, $"Blue {white.Z} should be above red {white.X}.");
    }

    [Fact]
    public void A_warmer_temperature_warms_more()
    {
        var gentle = Vector3.Transform(Vector3.One, PostGrade.WhiteBalance(0.2f, 0f));
        var strong = Vector3.Transform(Vector3.One, PostGrade.WhiteBalance(0.8f, 0f));

        Assert.True(strong.X / strong.Z > gentle.X / gentle.Z, $"{strong} should be warmer than {gentle}.");
    }

    [Fact]
    public void Tint_towards_magenta_lowers_green()
    {
        var white = Vector3.Transform(Vector3.One, PostGrade.WhiteBalance(0f, 0.5f));

        Assert.True(white.Y < white.X && white.Y < white.Z, $"Green should be below red and blue in {white}.");
    }

    [Fact]
    public void Tint_towards_green_raises_green()
    {
        var white = Vector3.Transform(Vector3.One, PostGrade.WhiteBalance(0f, -0.5f));

        Assert.True(white.Y > white.X && white.Y > white.Z, $"Green should be above red and blue in {white}.");
    }

    [Fact]
    public void Temperature_only_moves_colour_along_blue_to_orange_not_towards_green()
    {
        // A white balanced warmer has red up and blue down; green sits between them, so the shift
        // reads as orange, not magenta or green.
        var white = Vector3.Transform(Vector3.One, PostGrade.WhiteBalance(1f, 0f));

        Assert.InRange(white.Y, white.Z, white.X);
    }

    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(5f, 1f)]
    [InlineData(-5f, -1f)]
    public void Rendition_holds_a_white_balance_no_view_would_accept_at_a_value_it_would(float given, float heldAt)
    {
        AssertNear(PostGrade.WhiteBalance(heldAt, heldAt), PostGrade.WhiteBalance(given, given));
    }

    [Theory]
    [InlineData(1.5f, 0f, "temperature", "1.5")]
    [InlineData(-1.5f, 0f, "temperature", "-1.5")]
    [InlineData(float.NaN, 0f, "temperature", "NaN")]
    [InlineData(float.PositiveInfinity, 0f, "temperature", "Infinity")]
    [InlineData(0f, 2.5f, "tint", "2.5")]
    [InlineData(0f, -1.25f, "tint", "-1.25")]
    [InlineData(0f, float.NaN, "tint", "NaN")]
    [InlineData(0f, float.NegativeInfinity, "tint", "-Infinity")]
    public void A_temperature_or_tint_outside_minus_one_to_one_is_refused_by_name(float temperature, float tint, string name, string shown)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var view = new View();

            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => view.Finish(new PostSettings { Temperature = temperature, Tint = tint }));

            Assert.StartsWith($"The white balance {name} must be from -1 to 1, with 0 for no change, but it is {shown}.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_view_keeps_a_white_balance_at_the_ends_of_its_range()
    {
        var view = new View();

        view.Finish(new PostSettings { Temperature = -1f, Tint = 1f, Tonemapper = Tonemapper.Aces });

        Assert.Equal(-1f, view.Post.Temperature);
        Assert.Equal(1f, view.Post.Tint);
        Assert.Equal(Tonemapper.Aces, view.Post.Tonemapper);
    }

    private static PostSettings Graded() => new()
    {
        Exposure = 0.5f,
        Bloom = 0.2f,
        Vignette = 0.4f,
        Grain = 0.3f,
        Sharpen = 0.6f,
        Contrast = 0.25f,
        Saturation = -0.5f,
        Lift = new Vector3(0.01f, 0.02f, 0.03f),
        Gain = new Vector3(0.1f, 0.2f, 0.3f),
        Temperature = 0.7f,
        Tint = -0.4f,
        Tonemapper = Tonemapper.Aces,
        AgxLook = AgxLook.Base,
    };

    [Fact]
    public void Nothing_off_leaves_the_post_settings_as_they_are()
    {
        Assert.Equal(Graded(), PostGrade.Without(Graded(), RenderEffects.None));
    }

    [Fact]
    public void Each_finish_switch_clears_only_its_own_setting()
    {
        Assert.Equal(Graded() with { Vignette = 0f }, PostGrade.Without(Graded(), RenderEffects.Vignette));
        Assert.Equal(Graded() with { Grain = 0f }, PostGrade.Without(Graded(), RenderEffects.Grain));
        Assert.Equal(Graded() with { Sharpen = 0f }, PostGrade.Without(Graded(), RenderEffects.Sharpening));
    }

    [Fact]
    public void Colour_grading_off_makes_the_grade_neutral_and_keeps_the_exposure_bloom_and_tonemapper()
    {
        var post = PostGrade.Without(Graded(), RenderEffects.ColorGrading);

        Assert.Equal(
            Graded() with
            {
                Contrast = 0f,
                Saturation = 0f,
                Lift = Vector3.Zero,
                Gain = Vector3.Zero,
                Temperature = 0f,
                Tint = 0f,
            },
            post);
        Assert.Equal(new Vector4(1f, 1f, 1f, 0f), PostGrade.Tint(post).Gain);
        AssertNear(Matrix4x4.Identity, PostGrade.WhiteBalance(post.Temperature, post.Tint));
    }

    private static void AssertNear(Matrix4x4 expected, Matrix4x4 actual)
    {
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                Assert.Equal(expected[row, column], actual[row, column], 4);
            }
        }
    }
}
