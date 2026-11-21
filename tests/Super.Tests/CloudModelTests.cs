using System.Globalization;
using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

// What the CPU works out for the clouds: whether and how a view draws them, which light is on them, where the wind has
// carried them, what the captures see, and which settings a sky may have.
public class CloudModelTests
{
    private static readonly Vector3 HighSun = Vector3.Normalize(new Vector3(0.3f, 0.9f, 0f));
    private static readonly Vector3 SetSun = Vector3.Normalize(new Vector3(0.3f, -0.5f, 0f));
    private static readonly Vector3 Moon = Vector3.Normalize(new Vector3(-0.4f, 0.6f, 0.2f));

    private static FrameView View(CloudSettings? clouds, float turbidity = 2.5f, Vector3 moon = default, params DirectionalLight[] lights) => new()
    {
        Environment = new EnvironmentSettings { Intensity = 1f, Sky = new AnalyticSky { Turbidity = turbidity, Moon = moon } },
        Lights = lights.Length > 0 ? lights : [new DirectionalLight { Direction = HighSun, Radiance = new Vector3(5f) }],
        Weather = new WeatherSettings { Clouds = clouds },
    };

    [Fact]
    public void Clouds_are_marched_flat_or_not_drawn_by_the_sky_the_settings_and_the_switches()
    {
        Assert.Equal(2, CloudModel.Tier(View(CloudSettings.Fair), RenderEffects.None));
        Assert.Equal(1, CloudModel.Tier(View(CloudSettings.Fair), RenderEffects.VolumetricClouds));
        Assert.Equal(0, CloudModel.Tier(View(CloudSettings.Fair), RenderEffects.Clouds));
        Assert.Equal(0, CloudModel.Tier(View(CloudSettings.Fair, turbidity: 0f), RenderEffects.None));
        Assert.Equal(0, CloudModel.Tier(View(CloudSettings.Clear), RenderEffects.None));
        Assert.Equal(0, CloudModel.Tier(View(null), RenderEffects.None));
        Assert.Equal(2, CloudModel.Tier(View(new CloudSettings { Cirrus = 0.5f }), RenderEffects.None));
    }

    [Fact]
    public void The_sun_lights_the_clouds_until_it_sets_and_then_the_moon_does()
    {
        var sunUp = View(CloudSettings.Fair, moon: Moon, lights: Lit(HighSun, Moon));
        Assert.Equal(0, CloudModel.KeyLight(sunUp, out var towards, out var radiance));
        Assert.Equal(HighSun, towards);
        Assert.Equal(new Vector3(5f), radiance);

        var night = View(CloudSettings.Fair, moon: Moon, lights: Lit(SetSun, Moon));
        Assert.Equal(1, CloudModel.KeyLight(night, out towards, out radiance));
        Assert.True(Vector3.Distance(Moon, towards) < 1e-5f, $"The clouds were lit from {towards}, not the moon at {Moon}.");
        Assert.Equal(new Vector3(0.1f), radiance);
    }

    [Fact]
    public void A_moon_with_no_light_aimed_at_it_leaves_the_night_clouds_lit_by_the_sky_alone()
    {
        var night = View(CloudSettings.Fair, moon: Moon, lights: [new DirectionalLight { Direction = SetSun, Radiance = new Vector3(5f) }]);

        Assert.Equal(-1, CloudModel.KeyLight(night, out var towards, out var radiance));
        Assert.True(Vector3.Distance(Moon, towards) < 1e-5f, $"The clouds were lit from {towards}, not the moon at {Moon}.");
        Assert.Equal(Vector3.Zero, radiance);
    }

    [Fact]
    public void Clouds_cast_shadows_from_their_light_unless_switched_off_or_only_cirrus()
    {
        var light = Pack(View(CloudSettings.Fair), RenderEffects.None).CloudShadow.W;
        var switchedOff = Pack(View(CloudSettings.Fair), RenderEffects.CloudShadows).CloudShadow.W;
        var cirrus = Pack(View(new CloudSettings { Cirrus = 0.5f }), RenderEffects.None).CloudShadow.W;

        Assert.Equal(0f, light);
        Assert.Equal(-1f, switchedOff);
        Assert.Equal(-1f, cirrus);
    }

    [Fact]
    public void The_shadow_map_is_centred_on_the_texel_nearest_the_camera()
    {
        // A texel is 10000 / 512 = 19.53125 metres, so 30 metres rounds to the texel at 39.0625 and -5 to the one at 0.
        var view = View(CloudSettings.Fair) with { CameraPosition = new Vector3(30f, 100f, -5f) };

        var shadow = Pack(view, RenderEffects.None).CloudShadow;

        Assert.Equal(9.0625f, shadow.X, 4);
        Assert.Equal(5f, shadow.Y, 4);
    }

    [Fact]
    public void The_wind_carries_the_clouds_for_months_without_losing_precision()
    {
        // A metre a second for ten million and a quarter seconds, near four months, is 20000 tiles of 500 metres and
        // a quarter metre more, which a float can't hold; the origin then stands a quarter metre short of a tile's end.
        var drifted = CloudModel.Drifted(Vector2.Zero, new Vector2(1f, 0f), 10_000_000.25, 500f);

        Assert.Equal(499.75f, drifted.X, 3);
        Assert.Equal(0f, drifted.Y);
    }

    [Fact]
    public void The_captures_draw_no_clouds_when_the_view_draws_none()
    {
        Assert.Equal(default, CloudModel.Capture(View(CloudSettings.Fair), RenderEffects.Clouds, 1f, Vector3.Zero));
        Assert.Equal(default, CloudModel.Capture(View(CloudSettings.Clear), RenderEffects.None, 1f, Vector3.Zero));
    }

    [Fact]
    public void The_captures_move_the_clouds_on_in_steps_and_light_them_before_the_environment_scales_them()
    {
        var still = View(CloudSettings.Fair with { Wind = Vector2.Zero }) with { CameraPosition = new Vector3(1100f, 0f, 120f) };

        var clouds = CloudModel.Capture(still, RenderEffects.None, 2f, Vector3.Zero);

        // 1100 metres rounds to the step at 1000, 120 to the one at 0; the cirrus is read the same way.
        Assert.Equal(new Vector4(1000f, 0f, 1000f, 0f), clouds.Weather);
        Assert.Equal(new Vector3(2.5f), new Vector3(clouds.Radiance.X, clouds.Radiance.Y, clouds.Radiance.Z));
    }

    [Theory]
    [InlineData(1.5f, 0f, 0f, 0f, 0f, "1.5, 0, 0, 0, 0")]
    [InlineData(0.5f, -0.1f, 0f, 0f, 0f, "0.5, -0.1, 0, 0, 0")]
    [InlineData(0.5f, 0f, 4.5f, 0f, 0f, "0.5, 0, 4.5, 0, 0")]
    [InlineData(0.5f, 0f, 1f, 9000f, 0f, "0.5, 0, 1, 9000, 0")]
    [InlineData(0.5f, 0f, 1f, 0f, 100f, "0.5, 0, 1, 0, 100")]
    public void Clouds_out_of_range_are_refused_with_what_they_may_be(
        float coverage, float cirrus, float density, float altitude, float thickness, string values)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var view = new View();
            var clouds = new CloudSettings
            {
                Coverage = coverage, Cirrus = cirrus, Density = density, Altitude = altitude, Thickness = thickness,
                Wind = new Vector2(8.5f, 3f),
            };

            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => view.Surroundings.Sky(new SkySettings { Turbidity = 2.5f, Clouds = clouds }));

            Assert.Equal("settings", error.ParamName);
            Assert.StartsWith(
                "The clouds' coverage and cirrus must be from 0 to 1, their density from 0 to 4, their altitude from 0 "
                + "to 8000 metres, their thickness 0 or from 200 to 4000 metres and their wind a number, but they are "
                + values + " and <8.5, 3>.",
                error.Message);
            Assert.Null(view.Surroundings.SkySettings);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_wind_that_is_not_a_number_is_refused()
    {
        var view = new View();
        var clouds = CloudSettings.Fair with { Wind = new Vector2(float.NaN, 0f) };

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => view.Surroundings.Sky(new SkySettings { Turbidity = 2.5f, Clouds = clouds }));

        Assert.Contains("and <NaN, 0>.", error.Message);
    }

    [Fact]
    public void Every_preset_is_kept()
    {
        foreach (var clouds in new[] { CloudSettings.Clear, CloudSettings.Fair, CloudSettings.Cloudy, CloudSettings.Overcast })
        {
            var view = new View();

            view.Surroundings.Sky(new SkySettings { Turbidity = 2.5f, Clouds = clouds });

            Assert.Equal(clouds, view.Surroundings.SkySettings!.Value.Clouds);
        }
    }

    [Fact]
    public void A_sky_that_names_no_clouds_gets_fair_weather_and_no_sky_gets_none()
    {
        Assert.Equal(CloudSettings.Fair, FrameMaker.FrameClouds(new SkySettings { Turbidity = 2.5f }));
        Assert.Equal(CloudSettings.Overcast, FrameMaker.FrameClouds(new SkySettings { Turbidity = 2.5f, Clouds = CloudSettings.Overcast }));
        Assert.Equal(CloudSettings.Clear, FrameMaker.FrameClouds(new SkySettings { Turbidity = 2.5f, Clouds = CloudSettings.Clear }));
        Assert.Null(FrameMaker.FrameClouds(null));
    }

    // A sun towards sun and a moonlight aimed at the moon, a fiftieth as bright.
    private static DirectionalLight[] Lit(Vector3 sun, Vector3 moon) =>
    [
        new DirectionalLight { Direction = sun, Radiance = new Vector3(5f) },
        new DirectionalLight { Direction = moon, Radiance = new Vector3(0.1f) },
    ];

    private static FrameCloudBlock Pack(in FrameView view, RenderEffects off) =>
        CloudModel.Pack(view, off, Matrix4x4.Identity, (0, false), Vector3.Zero);
}
