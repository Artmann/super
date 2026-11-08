using System.Globalization;
using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

public class SkyModelTests
{
    [Fact]
    public void Zenith_luminance_for_turbidity_2_with_the_sun_60_degrees_up_matches_preetham()
    {
        // Preetham, Shirley and Smits 1999, appendix: Yz = (4.0453 T - 4.9710) tan(chi) - 0.2155 T + 2.4192,
        // with chi = (4/9 - T/120)(pi - 2 thetaS), in kcd/m2.
        // T = 2 and the sun 60 degrees up, so thetaS = 30 degrees = pi/6.
        //   chi = (0.444444 - 0.016667)(3.141593 - 1.047198) = 0.427778 * 2.094395 = 0.895936
        //   tan(0.895936) = 1.249693
        //   Yz = (8.0906 - 4.9710) * 1.249693 - 0.4310 + 2.4192 = 3.1196 * 1.249693 + 1.9882 = 5.8867
        var zenith = SkyModel.Zenith(2f, MathF.PI / 6f);

        Assert.Equal(5.8867f, zenith.X, 5.8867f * 0.01f);
    }

    [Fact]
    public void A_clearer_sky_is_bluer_at_the_zenith()
    {
        float sunZenithAngle = MathF.PI / 6f;

        var clear = SkyModel.Zenith(2f, sunZenithAngle);
        var hazy = SkyModel.Zenith(6f, sunZenithAngle);

        // Chromaticity y, the green-blue axis, is lower for a bluer sky: about 0.256 against 0.276.
        Assert.True(clear.Z < hazy.Z, $"y at turbidity 2 is {clear.Z}, at 6 it is {hazy.Z}.");

        // The paper's y matrix at T = 2, with thetaS = 0.523599, thetaS^2 = 0.274156, thetaS^3 = 0.143548:
        //   T^2 row: 0.00275 * 0.143548 - 0.00610 * 0.274156 + 0.00317 * 0.523599 = 0.000382
        //   T row: -0.04214 * 0.143548 + 0.08970 * 0.274156 - 0.04153 * 0.523599 + 0.00516 = 0.001958
        //   1 row: 0.15346 * 0.143548 - 0.26756 * 0.274156 + 0.06670 * 0.523599 + 0.26688 = 0.250480
        //   y = 4 * 0.000382 + 2 * 0.001958 + 0.250480 = 0.255924
        Assert.Equal(0.255924f, clear.Z, 1e-4f);
    }

    [Fact]
    public void Perez_coefficients_follow_the_papers_table()
    {
        // The appendix's luminance row at T = 3: A = 0.1787 * 3 - 1.4630 = -0.9269,
        // B = -0.3554 * 3 + 0.4275 = -0.6387, C = -0.0227 * 3 + 5.3251 = 5.2570,
        // D = 0.1206 * 3 - 2.5771 = -2.2153, E = -0.0670 * 3 + 0.3703 = 0.1693.
        var (a, b, c, d, e) = SkyModel.Perez(3f);

        Assert.Equal(-0.9269f, a.X, 1e-4f);
        Assert.Equal(-0.6387f, b.X, 1e-4f);
        Assert.Equal(5.2570f, c.X, 1e-4f);
        Assert.Equal(-2.2153f, d.X, 1e-4f);
        Assert.Equal(0.1693f, e.X, 1e-4f);
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(10.5f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void Turbidity_outside_1_7_to_10_is_refused(float turbidity)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var view = new View();

            var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.Sky(new SkySettings { Turbidity = turbidity }));

            Assert.Equal("settings", error.ParamName);
            Assert.StartsWith(
                "The sky's turbidity must be 0 for the gradient sky, or from 1.7 to 10 for a clear one, but it is "
                + turbidity.ToString(CultureInfo.InvariantCulture) + ".",
                error.Message);
            Assert.Null(view.SkySettings);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.7f)]
    [InlineData(2.5f)]
    [InlineData(10f)]
    public void Turbidity_of_0_or_from_1_7_to_10_is_kept(float turbidity)
    {
        var view = new View();

        view.Sky(new SkySettings { Turbidity = turbidity });

        Assert.Equal(turbidity, view.SkySettings!.Value.Turbidity);
    }

    [Fact]
    public void Turbidity_zero_keeps_the_gradient()
    {
        var view = new View();
        var sky = new SkySettings
        {
            Zenith = new Vector3(0.2f, 0.38f, 0.8f),
            Horizon = new Vector3(0.75f, 0.8f, 0.85f),
            Ground = new Vector3(0.2f, 0.17f, 0.14f),
        };
        view.Sky(sky);

        var (ceiling, horizon, floor) = SceneRenderer.SurroundingColors(view);

        Assert.Equal(sky.Zenith, ceiling);
        Assert.Equal(sky.Horizon, horizon);
        Assert.Equal(sky.Ground, floor);
        Assert.Equal(0f, SceneRenderer.FrameSky(view).Turbidity);
    }

    [Fact]
    public void A_clear_sky_reaches_the_frame_and_water_takes_it_over()
    {
        var view = new View();
        view.Sky(new SkySettings { Ground = new Vector3(0.2f, 0.17f, 0.14f), Turbidity = 2.5f });

        Assert.Equal(2.5f, SceneRenderer.FrameSky(view).Turbidity);
        Assert.Equal(new Vector3(0.2f, 0.17f, 0.14f), SceneRenderer.SurroundingColors(view).Floor);

        view.Water(new UnderwaterSettings { Absorption = new Vector3(0.4f, 0.1f, 0.05f) });

        Assert.Equal(0f, SceneRenderer.FrameSky(view).Turbidity);
    }

    [Fact]
    public void The_sun_disc_spreads_the_suns_light_over_its_size()
    {
        // LightAngle is how wide the sun looks, so the disc's radius is half of it: 0.01 radians.
        // Its solid angle is 2 pi (1 - cos 0.01) = 2 pi * 0.0000499996 = 0.000314157 sr, and a sun
        // giving 3 over that is 3 / 0.000314157 = 9549.3 bright.
        var lights = new[]
        {
            new DirectionalLight { Direction = Vector3.UnitX, Radiance = new Vector3(9f) },
            new DirectionalLight { Direction = Vector3.Normalize(new Vector3(0f, 1f, 1f)), Radiance = new Vector3(3f, 2f, 1f), CastsShadow = true },
        };
        var view = new FrameView { Lights = lights, Sky = new AnalyticSky { Turbidity = 2.5f } };
        view.Shadows.LightAngle = 0.02f;

        var sun = SkyModel.Sun(view);

        Assert.Equal(2.5f, sun.Turbidity);
        Assert.True(Vector3.Distance(lights[1].Direction, sun.Direction) < 1e-6f, $"The sun points {sun.Direction}.");
        Assert.Equal(9549.3f, sun.DiscRadiance.X, 1f);
        Assert.Equal(9549.3f / 3f, sun.DiscRadiance.Z, 1f);
        Assert.Equal(MathF.Cos(0.01f), sun.DiscCosine, 1e-6f);
    }

    [Fact]
    public void A_sun_too_small_for_half_floats_is_held_at_their_limit_and_a_gradient_sky_has_no_sun()
    {
        var lights = new[] { new DirectionalLight { Direction = Vector3.UnitY, Radiance = new Vector3(8f), CastsShadow = true } };
        var tiny = new FrameView { Lights = lights, Sky = new AnalyticSky { Turbidity = 2.5f } };
        tiny.Shadows.LightAngle = 0.0005f;
        var gradient = tiny;
        gradient.Sky = default;

        // 8 over a disc 0.00025 radians in radius is 8 / (pi * 0.00025^2) = 40.7 million, past a half float's 65504.
        Assert.Equal(SkyModel.MaxDiscRadiance, SkyModel.Sun(tiny).DiscRadiance.X);
        Assert.True(SkyModel.MaxDiscRadiance < 65504f);
        Assert.Equal(default, SkyModel.Sun(gradient));
    }
}
