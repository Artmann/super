using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>Ambient occlusion: how far it looks for nearby surfaces and how dark they make a point.</summary>
public class OcclusionTests
{
    [Fact]
    public void Packs_radius_and_strength() => Assert.Equal(new Vector4(0.8f, 0.5f, 0f, 0f), Occlusion.Pack(new OcclusionSettings(0.8f, 0.5f)));

    [Theory, InlineData(0f, 1f), InlineData(-1f, 1f), InlineData(0.6f, 1.5f)]
    public void Refuses_a_radius_that_is_not_above_zero_or_a_strength_outside_0_to_1(float radius, float strength)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new View().AmbientOcclusion(radius, strength));
        Assert.Contains(radius <= 0f ? "radius" : "strength", error.Message);
    }

    [Fact]
    public void Defaults_match_the_renderer_without_a_call() => Assert.Equal(new OcclusionSettings(), new View().Occlusion);

    [Fact]
    public void A_frame_that_does_not_set_occlusion_gets_a_radius_of_0_6_metres_and_a_strength_of_0_9()
    {
        var frame = new FrameView();

        Assert.Equal(new OcclusionSettings(0.6f, 0.9f), frame.Occlusion);
    }

    [Fact]
    public void A_view_keeps_what_it_is_given_until_the_next_frame()
    {
        var view = new View();
        view.Begin(0f);

        view.AmbientOcclusion(1.2f, 0.4f);
        Assert.Equal(new OcclusionSettings(1.2f, 0.4f), view.Occlusion);

        view.Begin(0f);
        Assert.Equal(new OcclusionSettings(), view.Occlusion);
    }
}
