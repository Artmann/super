using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>Height fog: how much of a surface's light gets through the air to the camera.</summary>
public class FogTests
{
    private const float Tolerance = 1e-4f;

    [Fact]
    public void No_density_means_full_transmittance()
    {
        float kept = Fog.Transmittance(0f, 0f, 0.1f, new Vector3(0f, 1f, 0f), new Vector3(300f, -40f, 200f));

        Assert.Equal(1f, kept);
    }

    [Fact]
    public void Level_fog_follows_beer_lambert()
    {
        // 0.1 per metre over 10 m at the fog's height is an optical depth of 1.
        float kept = Fog.Transmittance(0.1f, 0f, 0.15f, Vector3.Zero, new Vector3(10f, 0f, 0f));

        Assert.Equal(0.367879f, kept, Tolerance);
    }

    [Fact]
    public void Fog_thins_with_height()
    {
        float atHeight = Fog.Transmittance(0.1f, 0f, 0.15f, Vector3.Zero, new Vector3(10f, 0f, 0f));
        float above = Fog.Transmittance(0.1f, 0f, 0.15f, new Vector3(0f, 20f, 0f), new Vector3(10f, 20f, 0f));

        // 20 m up the density is 0.1 * e^-3 = 0.004979, so 10 m keeps e^-0.04979.
        Assert.True(above > atHeight);
        Assert.Equal(0.951431f, above, Tolerance);
    }

    [Fact]
    public void A_ray_climbing_through_the_fog_integrates_the_density_on_its_way()
    {
        // Straight up 10 m with falloff 0.1: the integral of 0.1 e^(-0.1 y) from 0 to 10 is 1 - e^-1.
        float up = Fog.Transmittance(0.1f, 0f, 0.1f, Vector3.Zero, new Vector3(0f, 10f, 0f));
        float down = Fog.Transmittance(0.1f, 0f, 0.1f, new Vector3(0f, 10f, 0f), Vector3.Zero);

        Assert.Equal(0.531464f, up, Tolerance);
        Assert.Equal(0.531464f, down, Tolerance);
    }

    [Fact]
    public void A_camera_high_above_the_fog_looking_straight_down_into_it_sees_through_its_whole_depth()
    {
        // From 120 m down to the fog's height with falloff 1: the integral of 0.1 e^-y from 0 to 120
        // is 0.1 (1 - e^-120), which is 0.1 to well within a float.
        float kept = Fog.Transmittance(0.1f, 0f, 1f, new Vector3(0f, 120f, 0f), Vector3.Zero);

        Assert.True(float.IsFinite(kept));
        Assert.Equal(0.904837f, kept, Tolerance);
    }

    [Fact]
    public void Far_below_the_fog_height_the_air_hides_everything_instead_of_breaking()
    {
        // 95 to 100 m under the fog's height with falloff 1 the density is 0.1 e^95 and more, far
        // past what a float holds, so nothing gets through.
        float kept = Fog.Transmittance(0.1f, 0f, 1f, new Vector3(0f, -100f, 0f), new Vector3(0f, -95f, 0f));

        Assert.Equal(0f, kept);
    }

    [Fact]
    public void The_fog_height_is_where_the_density_is_as_given()
    {
        // The same level ray as the Beer-Lambert one, with the fog and the ray both 5 m up.
        float kept = Fog.Transmittance(0.1f, 5f, 0.15f, new Vector3(0f, 5f, 0f), new Vector3(0f, 5f, 10f));

        Assert.Equal(0.367879f, kept, Tolerance);
    }

    [Fact]
    public void No_falloff_is_fog_of_one_density_everywhere()
    {
        // 0.05 per metre over a 30-40-50 slope of 50 m is an optical depth of 2.5.
        float kept = Fog.Transmittance(0.05f, 0f, 0f, Vector3.Zero, new Vector3(30f, 40f, 0f));

        Assert.Equal(0.082085f, kept, Tolerance);
    }

    [Fact]
    public void Packs_density_height_falloff_and_sun_scatter()
    {
        var packed = Fog.Pack(new FogSettings(0.004f, 1.5f, 0.15f, 0.6f));

        Assert.Equal(new Vector4(0.004f, 1.5f, 0.15f, 0.6f), packed);
    }

    [Fact]
    public void With_the_fog_switch_off_the_frame_gets_no_density()
    {
        var packed = Fog.Pack(new FogSettings(0.004f, 1.5f, 0.15f, 0.6f), RenderEffects.Fog, capturingProbe: false, underwater: false);

        Assert.Equal(Vector4.Zero, packed);
    }

    [Fact]
    public void Other_switches_leave_the_fog_alone()
    {
        var packed = Fog.Pack(new FogSettings(0.004f, 1.5f, 0.15f, 0.6f), RenderEffects.All & ~RenderEffects.Fog, capturingProbe: false, underwater: false);

        Assert.Equal(0.004f, packed.X);
    }

    [Fact]
    public void Probe_captures_see_no_fog()
    {
        var packed = Fog.Pack(new FogSettings(0.004f, 1.5f, 0.15f, 0.6f), RenderEffects.None, capturingProbe: true, underwater: false);

        Assert.Equal(Vector4.Zero, packed);
    }

    [Fact]
    public void Under_water_there_is_no_fog()
    {
        var packed = Fog.Pack(new FogSettings(0.004f, 1.5f, 0.15f, 0.6f), RenderEffects.None, capturingProbe: false, underwater: true);

        Assert.Equal(Vector4.Zero, packed);
    }

    [Fact]
    public void A_view_in_the_open_air_gets_the_fog_it_asks_for()
    {
        var packed = Fog.Pack(new FogSettings(0.004f, 1.5f, 0.15f, 0.6f), RenderEffects.None, capturingProbe: false, underwater: false);

        Assert.Equal(new Vector4(0.004f, 1.5f, 0.15f, 0.6f), packed);
    }

    [Fact]
    public void New_settings_have_no_density_a_falloff_of_0_1_and_a_sun_scatter_of_0_5()
    {
        var settings = new FogSettings();

        Assert.Equal(0f, settings.Density);
        Assert.Equal(0f, settings.Height);
        Assert.Equal(0.1f, settings.Falloff);
        Assert.Equal(0.5f, settings.SunScatter);
    }

    [Fact]
    public void A_sky_without_fog_settings_has_no_fog()
    {
        Assert.Equal(0f, SceneRenderer.FrameFog(new SkySettings()).Density);
        Assert.Equal(0f, SceneRenderer.FrameFog(null).Density);
        Assert.Equal(0.004f, SceneRenderer.FrameFog(new SkySettings { Fog = new FogSettings(0.004f) }).Density);
    }

    [Theory]
    [InlineData(-0.01f, 0.1f, 0.5f, "density")]
    [InlineData(0.01f, -0.1f, 0.5f, "falloff")]
    [InlineData(0.01f, 0.1f, -0.5f, "sun scatter")]
    [InlineData(float.NaN, 0.1f, 0.5f, "density")]
    public void Negative_density_or_falloff_is_refused(float density, float falloff, float sunScatter, string named)
    {
        var view = new View();

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => view.Sky(new SkySettings { Fog = new FogSettings(density, 0f, falloff, sunScatter) }));

        Assert.Contains($"The fog's {named} must be 0 or more", error.Message);
        Assert.Null(view.SkySettings);
    }

    [Fact]
    public void A_fog_height_that_is_not_a_number_is_refused()
    {
        var view = new View();

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => view.Sky(new SkySettings { Fog = new FogSettings(0.01f, float.PositiveInfinity) }));

        Assert.Contains("The fog's height must be a number of metres", error.Message);
    }
}
