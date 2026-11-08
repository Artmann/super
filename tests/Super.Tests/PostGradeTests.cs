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
        // light probes' two, then eight ints, the last the local light count, then the effects left
        // out, the mip bias, the probe capture flag and one int of padding, so the lights start on
        // a 16 byte boundary.
        int radii = (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.ShadowRadii));
        Assert.Equal(radii + 16, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.ShadowFade)));
        Assert.Equal(radii + 32, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.ShadowDepthScales)));
        Assert.Equal(radii + 48, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Clusters)));
        Assert.Equal(radii + 64, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Screen)));
        Assert.Equal(radii + 80, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Occlusion)));
        Assert.Equal(radii + 96, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.LightProbeMin)));
        Assert.Equal(radii + 112, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.LightProbeCounts)));
        Assert.Equal(radii + 128, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.LightCount)));
        Assert.Equal(radii + 128 + 28, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.LocalLightCount)));
        Assert.Equal(radii + 128 + 32, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Off)));
        Assert.Equal(radii + 128 + 40, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.ProbeCapture)));
        Assert.Equal(radii + 128 + 48, (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.Lights)));
    }
}
