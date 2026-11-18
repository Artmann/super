using System.Globalization;
using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>
/// Volumetric fog: the froxel grid's size, its settings and what the shaders are given.
/// </summary>
public class VolumetricFogTests
{
    [Fact]
    public void Grid_follows_the_scene_size_not_the_window()
    {
        Assert.Equal((200, 113, 64), VolumetricFog.GridSize(1600, 900));

        var (width, height) = Renderer.SceneSize(1600, 900, 2f);

        Assert.Equal((400, 225, 64), VolumetricFog.GridSize(width, height));
    }

    [Theory]
    [InlineData(0.95f, "0.95")]
    [InlineData(-1f, "-1")]
    [InlineData(float.NaN, "NaN")]
    public void Anisotropy_outside_range_is_refused(float anisotropy, string shown)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var view = new View();

            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => view.Sky(new SkySettings { Fog = new FogSettings(0.01f) { Anisotropy = anisotropy } }));

            Assert.StartsWith(
                $"The fog's anisotropy must be from -0.9 to 0.9, where 0 scatters light evenly and more scatters it onwards, but it is {shown}.",
                error.Message);
            Assert.Null(view.SkySettings);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(-1f, "-1")]
    [InlineData(float.PositiveInfinity, "Infinity")]
    public void A_volumetric_distance_below_zero_or_endless_is_refused(float distance, string shown)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var view = new View();

            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => view.Sky(new SkySettings { Fog = new FogSettings(0.01f) { VolumetricDistance = distance } }));

            Assert.StartsWith(
                $"The fog's volumetric distance must be 0 or more, in metres, and 0 is the plain fog only, but it is {shown}.",
                error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Fog_settings_default_to_forward_scattering_over_48_metres()
    {
        var fog = new FogSettings(0.004f);

        Assert.Equal(0.6f, fog.Anisotropy);
        Assert.Equal(48f, fog.VolumetricDistance);
        Assert.Equal(0.6f, new FogSettings().Anisotropy);
        Assert.Equal(48f, new FogSettings().VolumetricDistance);
    }

    [Fact]
    public void Volumetric_fog_off_is_the_analytic_fog()
    {
        var fog = new FogSettings(0.004f, 1.5f, 0.15f, 0.6f);

        Assert.Equal(RenderEffects.VolumetricFog, new Graphics { VolumetricFog = false }.Off);
        Assert.False(Graphics.Fastest().VolumetricFog);

        // On, the volume reaches 48 m, starts its slices at 1 m and scatters forwards.
        Assert.Equal(new Vector4(48f, 1f, 0.6f, 0f), VolumetricFog.Pack(fog, 0, RenderEffects.None, null));

        // Off, it reaches nowhere, which every shader takes as the analytic fog, and the analytic
        // fog still gets its own settings.
        Assert.Equal(Vector4.Zero, VolumetricFog.Pack(fog, 0, RenderEffects.VolumetricFog, null));
        Assert.Equal(new Vector4(0.004f, 1.5f, 0.15f, 0.6f), Fog.Pack(fog, RenderEffects.VolumetricFog, false));
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(0.004f, true)]
    public void No_volume_without_fog_or_with_the_fog_off(float density, bool fogOff)
    {
        var fog = new FogSettings(density);
        var off = fogOff ? RenderEffects.Fog : RenderEffects.None;

        Assert.Equal(Vector4.Zero, VolumetricFog.Pack(fog, 0, off, null));
    }

    [Fact]
    public void Under_water_the_volume_reaches_the_shaft_distance()
    {
        // The water itself fills the volume, so it needs no fog, and a shorter reach starts its
        // slices closer as above water.
        var clear = new FogSettings();

        Assert.Equal(new Vector4(30f, 1f, 0.6f, 0f), VolumetricFog.Pack(clear, 0, RenderEffects.None, 30f));
        Assert.Equal(new Vector4(1.5f, 0.75f, 0.6f, 0f), VolumetricFog.Pack(clear, 0, RenderEffects.None, 1.5f));
    }

    [Theory]
    [InlineData(RenderEffects.VolumetricFog, 30f)]
    [InlineData(RenderEffects.None, 0f)]
    public void Under_water_with_volumetric_fog_off_there_is_no_volume(RenderEffects off, float reach)
    {
        // Volumetric fog off, or no shaft distance: the water is drawn in closed form, with no shafts.
        Assert.Equal(Vector4.Zero, VolumetricFog.Pack(new FogSettings(0.004f), 2, off, reach));
    }

    [Theory]
    [InlineData(RenderEffects.None, null, false)]
    [InlineData(RenderEffects.Fog, null, false)]
    [InlineData(RenderEffects.Fog | RenderEffects.FogVolumes, 30f, false)]
    [InlineData(RenderEffects.Fog | RenderEffects.FogVolumes, null, true)]
    [InlineData(RenderEffects.VolumetricFog, 30f, true)]
    public void The_volumes_are_freed_only_when_no_frame_like_this_can_draw_them(RenderEffects off, float? underwaterReach, bool freed)
    {
        // Under water with the fog and the fog volumes off, the water still fills the volume, so
        // freeing it there would remake it the next frame, and free it again, every frame.
        Assert.Equal(freed, VolumetricFog.CanFreeTargets(off, underwaterReach));
    }

    [Fact]
    public void A_volumetric_distance_of_zero_is_the_analytic_fog_only()
    {
        var fog = new FogSettings(0.004f) { VolumetricDistance = 0f };

        Assert.Equal(Vector4.Zero, VolumetricFog.Pack(fog, 0, RenderEffects.None, null));
    }

    [Fact]
    public void A_short_volume_starts_its_slices_closer()
    {
        // Under 2 m the first slice ends halfway, so the slices still cover the whole volume.
        var fog = new FogSettings(0.004f) { VolumetricDistance = 1f };

        Assert.Equal(new Vector4(1f, 0.5f, 0.6f, 0f), VolumetricFog.Pack(fog, 0, RenderEffects.None, null));
    }

    [Fact]
    public void Lights_add_to_the_volume_at_full_strength_unless_told_otherwise()
    {
        var view = new View();

        view.PointLight(1, Vector3.Zero, Vector3.One, 4f);
        view.PointLight(2, Vector3.Zero, Vector3.One, 4f, volumetric: 0.25f);

        var lights = view.DrawnLights.ToArray();
        Assert.Equal(1f, lights[0].Volumetric);
        Assert.Equal(0.25f, lights[1].Volumetric);
    }

    [Theory]
    [InlineData(-0.5f)]
    [InlineData(float.NaN)]
    public void A_negative_volumetric_strength_is_refused(float volumetric)
    {
        var view = new View();

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => view.SpotLight(1, Vector3.Zero, -Vector3.UnitY, Vector3.One, 4f, 0.2f, 0.4f, volumetric: volumetric));

        Assert.StartsWith("A light's volumetric strength must be 0 or more, and 0 leaves it out of the fog.", error.Message);
        Assert.Equal("volumetric", error.ParamName);
    }
}
