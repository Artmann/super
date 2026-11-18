using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>Ambient occlusion: how far it looks for nearby surfaces and how dark they make a point.</summary>
public class OcclusionTests
{
    [Fact]
    public void Packs_the_occlusion_radius_and_strength_then_the_bounce_strength_and_radius() =>
        Assert.Equal(new Vector4(0.8f, 0.5f, 1.5f, 3f),
            Occlusion.Pack(new OcclusionSettings(0.8f, 0.5f), new BounceSettings(1.5f, 3f), RenderEffects.None, historyValid: true));

    [Theory]
    [InlineData(RenderEffects.ScreenBounce, true)]
    [InlineData(RenderEffects.AmbientOcclusion, true)]
    [InlineData(RenderEffects.None, false)]
    public void The_bounce_strength_is_zero_when_it_is_off_its_occlusion_pass_is_off_or_there_is_no_last_frame(
        RenderEffects off, bool historyValid)
    {
        var packed = Occlusion.Pack(new OcclusionSettings(), new BounceSettings(1f, 2f), off, historyValid);

        Assert.Equal(0f, packed.Z);
        Assert.Equal(2f, packed.W);
    }

    [Theory, InlineData(0f, 1f), InlineData(-1f, 1f), InlineData(0.6f, 1.5f)]
    public void Refuses_a_radius_that_is_not_above_zero_or_a_strength_outside_0_to_1(float radius, float strength)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new View().Indirect.AmbientOcclusion(radius, strength));
        Assert.Contains(radius <= 0f ? "radius" : "strength", error.Message);
    }

    [Fact]
    public void Defaults_match_the_renderer_without_a_call() => Assert.Equal(new OcclusionSettings(), new View().Indirect.Occlusion);

    [Fact]
    public void A_frame_that_does_not_set_occlusion_gets_a_radius_of_0_6_metres_and_a_strength_of_0_9()
    {
        var frame = new FrameView();

        Assert.Equal(new OcclusionSettings(0.6f, 0.9f), frame.ScreenSpace.Occlusion);
    }

    [Theory, InlineData(-0.1f, 2f, "strength"), InlineData(2.1f, 2f, "strength"), InlineData(1f, 0f, "radius"), InlineData(1f, 8.5f, "radius")]
    public void Refuses_a_bounce_strength_outside_0_to_2_or_a_radius_outside_0_to_8(float strength, float radius, string named)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new View().Indirect.ScreenBounce(strength, radius));
        Assert.Contains($"The screen bounce {named} must be", error.Message);
    }

    [Fact]
    public void A_view_keeps_the_bounce_it_is_given_until_the_next_frame()
    {
        var view = new View();
        view.Begin(0f);

        view.Indirect.ScreenBounce(0.5f, 4f);
        Assert.Equal(new BounceSettings(0.5f, 4f), view.Indirect.Bounce);

        view.Begin(0f);
        Assert.Equal(new BounceSettings(1f, 2f), view.Indirect.Bounce);
    }

    [Fact]
    public void A_view_keeps_what_it_is_given_until_the_next_frame()
    {
        var view = new View();
        view.Begin(0f);

        view.Indirect.AmbientOcclusion(1.2f, 0.4f);
        Assert.Equal(new OcclusionSettings(1.2f, 0.4f), view.Indirect.Occlusion);

        view.Begin(0f);
        Assert.Equal(new OcclusionSettings(), view.Indirect.Occlusion);
    }
}
