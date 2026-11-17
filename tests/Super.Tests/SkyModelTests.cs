using System.Globalization;
using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

public class SkyModelTests
{
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

    // A sky with three colours of its own, so its bands can be told from the studio's and the water's.
    private static readonly SkySettings Gradient = new()
    {
        Zenith = new Vector3(0.2f, 0.38f, 0.8f),
        Horizon = new Vector3(0.75f, 0.8f, 0.85f),
        Ground = new Vector3(0.2f, 0.17f, 0.14f),
    };

    // Water with its own shallow and deep colours, to tell its bands from the sky's.
    private static readonly UnderwaterSettings Lake = new()
    {
        Absorption = new Vector3(0.4f, 0.1f, 0.05f),
        ShallowColor = new Vector3(0.1f, 0.5f, 0.6f),
        DeepColor = new Vector3(0f, 0.1f, 0.2f),
    };

    [Fact]
    public void Turbidity_zero_keeps_the_gradient()
    {
        var view = new View();
        view.Sky(Gradient);

        var (ceiling, horizon, floor) = SceneRenderer.SurroundingColors(view, RenderEffects.None);

        Assert.Equal(Gradient.Zenith, ceiling);
        Assert.Equal(Gradient.Horizon, horizon);
        Assert.Equal(Gradient.Ground, floor);
        Assert.Equal(0f, SceneRenderer.FrameSky(view, RenderEffects.None).Turbidity);
    }

    [Fact]
    public void A_clear_sky_reaches_the_frame_and_water_takes_it_over()
    {
        var view = new View();
        view.Sky(new SkySettings { Ground = new Vector3(0.2f, 0.17f, 0.14f), Turbidity = 2.5f });

        Assert.Equal(2.5f, SceneRenderer.FrameSky(view, RenderEffects.None).Turbidity);
        Assert.Equal(new Vector3(0.2f, 0.17f, 0.14f), SceneRenderer.SurroundingColors(view, RenderEffects.None).Floor);

        view.Water(new UnderwaterSettings { Absorption = new Vector3(0.4f, 0.1f, 0.05f) });

        Assert.Equal(0f, SceneRenderer.FrameSky(view, RenderEffects.None).Turbidity);
    }

    // The gradient sky with a clear-sky turbidity, under the lake.
    private static View UnderTheLake()
    {
        var sky = Gradient;
        sky.Turbidity = 2.5f;
        var view = new View();
        view.Sky(sky);
        view.Water(Lake);
        return view;
    }

    [Fact]
    public void With_underwater_off_a_view_under_water_keeps_the_sky_bands_and_turbidity()
    {
        var view = UnderTheLake();

        var (ceiling, horizon, floor) = SceneRenderer.SurroundingColors(view, RenderEffects.Underwater);

        Assert.Equal(Gradient.Zenith, ceiling);
        Assert.Equal(Gradient.Horizon, horizon);
        Assert.Equal(Gradient.Ground, floor);
        Assert.Equal(2.5f, SceneRenderer.FrameSky(view, RenderEffects.Underwater).Turbidity);
    }

    [Fact]
    public void With_underwater_on_a_view_under_water_takes_the_water_bands_and_no_sky()
    {
        var view = UnderTheLake();

        var (ceiling, _, floor) = SceneRenderer.SurroundingColors(view, RenderEffects.None);

        Assert.Equal(Lake.ShallowColor, ceiling);
        Assert.Equal(Lake.DeepColor, floor);
        Assert.Equal(0f, SceneRenderer.FrameSky(view, RenderEffects.None).Turbidity);
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
    public void A_low_sun_is_tinted_warmer_and_dimmer_and_its_disc_follows_while_other_lights_stay()
    {
        float low = 3f * MathF.PI / 180f;
        var lamp = new DirectionalLight { Direction = Vector3.UnitY, Radiance = new Vector3(1f) };
        var lights = new[]
        {
            lamp,
            new DirectionalLight { Direction = new Vector3(MathF.Cos(low), MathF.Sin(low), 0f), Radiance = new Vector3(4f), CastsShadow = true },
        };
        var view = new FrameView { Lights = lights, Sky = new AnalyticSky { Turbidity = 3f } };
        view.Shadows.LightAngle = 0.01f;
        var buffer = Array.Empty<DirectionalLight>();

        var tinted = SkyModel.Tinted(view, ref buffer);

        var sun = tinted.Lights[1];
        Assert.True(sun.Radiance.Y < 2f, $"The low sun is {sun.Radiance}.");
        Assert.True(sun.Radiance.X > 2f * sun.Radiance.Z, $"The low sun is {sun.Radiance}.");
        Assert.Equal(lamp.Radiance, tinted.Lights[0].Radiance);
        Assert.Equal(new Vector3(4f), lights[1].Radiance);
        Assert.Equal(sun.Radiance.X / sun.Radiance.Z,
            SkyModel.Sun(tinted).DiscRadiance.X / SkyModel.Sun(tinted).DiscRadiance.Z, 1e-3f);
    }

    [Fact]
    public void A_sun_60_degrees_up_keeps_its_colour_and_a_gradient_sky_tints_nothing()
    {
        float up = MathF.PI / 3f;
        var lights = new[]
        {
            new DirectionalLight { Direction = new Vector3(MathF.Cos(up), MathF.Sin(up), 0f), Radiance = new Vector3(3f, 2f, 1f) },
        };
        var buffer = Array.Empty<DirectionalLight>();

        var clear = SkyModel.Tinted(new FrameView { Lights = lights, Sky = new AnalyticSky { Turbidity = 3f } }, ref buffer);
        var gradient = SkyModel.Tinted(new FrameView { Lights = lights }, ref buffer);

        Assert.True(Vector3.Distance(new Vector3(3f, 2f, 1f), clear.Lights[0].Radiance) < 1e-4f, $"The sun is {clear.Lights[0].Radiance}.");
        Assert.True(gradient.Lights == lights.AsSpan(), "A gradient sky should keep the view's own lights.");
    }

    [Theory]
    [InlineData(0.5f, 1.7f)]
    [InlineData(1.2f, 1.7f)]
    [InlineData(4f, 4f)]
    [InlineData(25f, 10f)]
    [InlineData(float.PositiveInfinity, 10f)]
    public void Rendition_draws_a_turbidity_out_of_range_at_the_nearest_end(float turbidity, float drawn)
    {
        var lights = new[] { new DirectionalLight { Direction = Vector3.UnitY, Radiance = new Vector3(8f), CastsShadow = true } };
        var view = new FrameView { Lights = lights, Sky = new AnalyticSky { Turbidity = turbidity } };

        Assert.Equal(drawn, SkyModel.Sun(view).Turbidity);
        Assert.Equal(drawn, SkyModel.Sun(view with { Lights = default }).Turbidity);
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
