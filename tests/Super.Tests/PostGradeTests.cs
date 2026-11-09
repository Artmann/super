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
        // light probes' two, the clear sky's sun and its disc, the fog, the white balance mat4, then
        // eight ints, the last the local light count, then the effects left out, the mip bias, the
        // probe capture flag and one int of padding, so the lights start on a 16 byte boundary.
        int radii = (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.ShadowRadii));
        Assert.Equal(radii + 16, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.ShadowFade)));
        Assert.Equal(radii + 32, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.ShadowDepthScales)));
        Assert.Equal(radii + 48, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Clusters)));
        Assert.Equal(radii + 64, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Screen)));
        Assert.Equal(radii + 80, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Occlusion)));
        Assert.Equal(radii + 96, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.LightProbeMin)));
        Assert.Equal(radii + 112, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.LightProbeCounts)));
        Assert.Equal(radii + 128, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.SkySun)));
        Assert.Equal(radii + 144, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.SunDisc)));
        Assert.Equal(radii + 160, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Fog)));
        Assert.Equal(radii + 176, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.WhiteBalance)));
        Assert.Equal(radii + 240, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.LightCount)));
        Assert.Equal(radii + 240 + 28, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.LocalLightCount)));
        Assert.Equal(radii + 240 + 32, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Off)));
        Assert.Equal(radii + 240 + 40, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.ProbeCapture)));
        Assert.Equal(radii + 240 + 48, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Lights)));
    }

    [Fact]
    public void Post_settings_left_alone_use_agx()
    {
        var (_, gain) = PostGrade.Tint(default);

        Assert.Equal(Tonemapper.Agx, default(PostSettings).Tonemapper);
        Assert.Equal(0f, gain.W);
    }

    [Fact]
    public void Asking_for_aces_tells_the_shader_to_use_its_curve()
    {
        var (_, gain) = PostGrade.Tint(new PostSettings { Tonemapper = Tonemapper.Aces });

        Assert.Equal(1f, gain.W);
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
