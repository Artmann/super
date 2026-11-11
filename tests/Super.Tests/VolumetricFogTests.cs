using System.Globalization;
using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>
/// Volumetric fog: the froxel grid's shape, its slices, the phase function and the sum
/// the integrate pass makes along each column.
/// </summary>
public class VolumetricFogTests
{
    private const float Tolerance = 1e-4f;

    [Fact]
    public void Slices_cover_near_to_far_and_invert()
    {
        const int slices = 64;
        const float near = 1f;
        const float far = 48f;

        Assert.Equal(near, VolumetricFog.SliceDepth(0, slices, near, far), Tolerance);
        Assert.Equal(far, VolumetricFog.SliceDepth(slices, slices, near, far), 1e-3f);

        float previous = 0f;
        for (int slice = 0; slice <= slices; slice++)
        {
            float depth = VolumetricFog.SliceDepth(slice, slices, near, far);

            Assert.True(depth > previous, $"Slice {slice} at {depth} m isn't past the one before it at {previous} m.");
            Assert.Equal(slice, VolumetricFog.SliceOf(depth, slices, near, far), 1e-3f);
            previous = depth;
        }

        // Exponential: halfway through the slices is the geometric mean of near and far, sqrt(48).
        Assert.Equal(6.928203f, VolumetricFog.SliceDepth(32, slices, near, far), 1e-3f);
    }

    [Fact]
    public void Isotropic_phase_is_one_over_four_pi()
    {
        Assert.Equal(0.0795775f, VolumetricFog.Phase(1f, 0f), 1e-6f);
        Assert.Equal(0.0795775f, VolumetricFog.Phase(0f, 0f), 1e-6f);
        Assert.Equal(0.0795775f, VolumetricFog.Phase(-1f, 0f), 1e-6f);
    }

    [Theory]
    [InlineData(0.6f)]
    [InlineData(-0.6f)]
    [InlineData(0f)]
    public void Phase_integrates_to_one_over_the_sphere(float anisotropy)
    {
        // The integral over the sphere is 2π times the integral of p(cos θ) sin θ dθ from 0 to π.
        const int steps = 20000;
        double sum = 0;
        for (int i = 0; i < steps; i++)
        {
            double theta = (i + 0.5) * Math.PI / steps;
            sum += VolumetricFog.Phase((float)Math.Cos(theta), anisotropy) * Math.Sin(theta);
        }

        double total = 2 * Math.PI * sum * Math.PI / steps;

        Assert.InRange(total, 0.99, 1.01);
    }

    [Fact]
    public void Forward_scattering_is_brighter_towards_the_light()
    {
        // By hand, with g 0.6: (1 - 0.36) / (4π (1 + 0.36 - 1.2)^1.5) = 0.64 / (4π 0.064) = 0.795775 straight on,
        // and 0.64 / (4π 1.36^1.5) = 0.0321112 across.
        float towards = VolumetricFog.Phase(1f, 0.6f);
        float across = VolumetricFog.Phase(0f, 0.6f);

        Assert.True(towards > across);
        Assert.Equal(0.795775f, towards, Tolerance);
        Assert.Equal(0.0321112f, across, Tolerance);
    }

    [Fact]
    public void A_uniform_unlit_medium_matches_beer_lambert()
    {
        // 0.1 per metre over 10 m, cut by the real exponential slices, is an optical depth of 1.
        const int slices = 64;
        var cells = new Vector4[slices];
        var lengths = new float[slices];
        for (int i = 0; i < slices; i++)
        {
            cells[i] = new Vector4(0f, 0f, 0f, 0.1f);
            float start = i == 0 ? 0f : VolumetricFog.SliceDepth(i, slices, 0.5f, 10f);
            lengths[i] = VolumetricFog.SliceDepth(i + 1, slices, 0.5f, 10f) - start;
        }

        var result = VolumetricFog.Integrate(cells, lengths);

        Assert.Equal(0.367879f, result.W, Tolerance);
        Assert.Equal(Vector3.Zero, new Vector3(result.X, result.Y, result.Z));
    }

    [Fact]
    public void A_lit_uniform_medium_glows_by_what_it_hides()
    {
        // Light of 2 scattered towards the eye over an optical depth of 1 adds 2 (1 - e^-1) = 1.264241,
        // the same as the analytic fog's colour times what it hides.
        var cells = new Vector4[10];
        var lengths = new float[10];
        Array.Fill(cells, new Vector4(2f, 2f, 2f, 0.1f));
        Array.Fill(lengths, 1f);

        var result = VolumetricFog.Integrate(cells, lengths);

        Assert.Equal(1.264241f, result.X, Tolerance);
        Assert.Equal(0.367879f, result.W, Tolerance);
    }

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
        Assert.Equal(new Vector4(48f, 1f, 0.6f, 0f), VolumetricFog.Pack(fog, RenderEffects.None, false, false));

        // Off, it reaches nowhere, which every shader takes as the analytic fog, and the analytic
        // fog still gets its own settings.
        Assert.Equal(Vector4.Zero, VolumetricFog.Pack(fog, RenderEffects.VolumetricFog, false, false));
        Assert.Equal(new Vector4(0.004f, 1.5f, 0.15f, 0.6f), Fog.Pack(fog, RenderEffects.VolumetricFog, false, false));
    }

    [Theory]
    [InlineData(0f, false, false, false)]
    [InlineData(0.004f, true, false, false)]
    [InlineData(0.004f, false, true, false)]
    [InlineData(0.004f, false, false, true)]
    public void No_volume_without_fog_in_probes_under_water_or_with_the_fog_off(
        float density, bool fogOff, bool capturingProbe, bool underwater)
    {
        var fog = new FogSettings(density);
        var off = fogOff ? RenderEffects.Fog : RenderEffects.None;

        Assert.Equal(Vector4.Zero, VolumetricFog.Pack(fog, off, capturingProbe, underwater));
    }

    [Fact]
    public void A_volumetric_distance_of_zero_is_the_analytic_fog_only()
    {
        var fog = new FogSettings(0.004f) { VolumetricDistance = 0f };

        Assert.Equal(Vector4.Zero, VolumetricFog.Pack(fog, RenderEffects.None, false, false));
    }

    [Fact]
    public void A_short_volume_starts_its_slices_closer()
    {
        // Under 2 m the first slice ends halfway, so the slices still cover the whole volume.
        var fog = new FogSettings(0.004f) { VolumetricDistance = 1f };

        Assert.Equal(new Vector4(1f, 0.5f, 0.6f, 0f), VolumetricFog.Pack(fog, RenderEffects.None, false, false));
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
