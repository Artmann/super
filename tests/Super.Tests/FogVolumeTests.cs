using System.Globalization;
using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>Fog volumes: what a view accepts and what it hands the renderer.</summary>
public class FogVolumeTests
{
    private static readonly FogVolumeSettings Mist = new(0.3f);

    [Fact]
    public void A_fog_volume_starts_white_with_a_soft_edge_and_no_noise()
    {
        var settings = new FogVolumeSettings(0.5f);

        Assert.Equal(0.5f, settings.Density);
        Assert.Equal(Vector3.One, settings.Color);
        Assert.Equal(0.25f, settings.EdgeFade);
        Assert.Equal(0f, settings.Noise);
        Assert.Equal(2f, settings.NoiseSize);
        Assert.Equal(Vector3.Zero, settings.Wind);
    }

    [Fact]
    public void Volumes_placed_reach_the_renderer_in_order()
    {
        var view = new View();
        var low = Matrix4x4.CreateScale(4f, 0.4f, 4f);
        var tall = Matrix4x4.CreateScale(1f, 3f, 1f) * Matrix4x4.CreateTranslation(2f, 0f, 0f);

        view.FogVolume(7, FogShape.Box, low, Mist);
        view.FogVolume(2, FogShape.Cone, tall, Mist with { Noise = 0.5f });

        Assert.Equal(
            [new FogVolumeDraw(FogShape.Box, low, Mist), new FogVolumeDraw(FogShape.Cone, tall, Mist with { Noise = 0.5f })],
            view.DrawnFogVolumes.ToArray());
    }

    [Fact]
    public void A_new_frame_starts_with_no_volumes()
    {
        var view = new View();
        view.FogVolume(1, FogShape.Ellipsoid, Matrix4x4.Identity, Mist);

        view.Begin(1f);
        view.FogVolume(1, FogShape.Ellipsoid, Matrix4x4.Identity, Mist);

        Assert.Equal(1, view.DrawnFogVolumes.Length);
    }

    [Fact]
    public void A_key_placed_twice_in_a_frame_is_refused()
    {
        var view = new View();
        view.FogVolume(3, FogShape.Box, Matrix4x4.Identity, Mist);

        var error = Assert.Throws<ArgumentException>(() => view.FogVolume(3, FogShape.Cylinder, Matrix4x4.Identity, Mist));

        Assert.StartsWith("Fog volume 3 was already placed this frame. Give each fog volume its own key.", error.Message);
        Assert.Equal("key", error.ParamName);
    }

    [Fact]
    public void A_65th_volume_is_refused()
    {
        var view = new View();
        for (int i = 0; i < 64; i++)
        {
            view.FogVolume(i, FogShape.Box, Matrix4x4.Identity, Mist);
        }

        var error = Assert.Throws<InvalidOperationException>(() => view.FogVolume(64, FogShape.Box, Matrix4x4.Identity, Mist));

        Assert.Equal("At most 64 fog volumes fit in a frame.", error.Message);
        Assert.Equal(64, view.DrawnFogVolumes.Length);
    }

    [Fact]
    public void A_shape_that_is_not_one_of_the_four_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new View().FogVolume(1, (FogShape)9, Matrix4x4.Identity, Mist));

        Assert.StartsWith("A fog volume can be a Box, an Ellipsoid, a Cylinder or a Cone.", error.Message);
        Assert.Equal("shape", error.ParamName);
    }

    [Fact]
    public void A_transform_squashed_flat_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(
            () => new View().FogVolume(1, FogShape.Box, Matrix4x4.CreateScale(2f, 0f, 2f), Mist));

        Assert.StartsWith(
            "The fog volume's transform can't be inverted: it is squashed flat on some axis, or holds a number that isn't finite. "
            + "Give it a width, a height and a depth.",
            error.Message);
        Assert.Equal("transform", error.ParamName);
    }

    [Fact]
    public void A_transform_with_a_number_that_is_not_finite_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(
            () => new View().FogVolume(1, FogShape.Box, Matrix4x4.CreateTranslation(float.NaN, 0f, 0f), Mist));

        Assert.StartsWith("The fog volume's transform can't be inverted", error.Message);
    }

    [Theory]
    [InlineData(-0.5f, "-0.5")]
    [InlineData(float.NaN, "NaN")]
    [InlineData(float.PositiveInfinity, "Infinity")]
    public void A_density_that_is_negative_or_not_finite_is_refused(float density, string shown)
    {
        var error = ThrowsUnderSwedish(new FogVolumeSettings(density));

        Assert.StartsWith($"The fog volume's density is {shown}, but it must be 0 or more.", error.Message);
    }

    [Fact]
    public void A_colour_channel_outside_0_to_1_is_refused()
    {
        var error = ThrowsUnderSwedish(Mist with { Color = new Vector3(1f, 1.5f, 0.25f) });

        Assert.StartsWith("The fog volume's colour is <1, 1.5, 0.25>, but each channel must be from 0 to 1.", error.Message);
    }

    [Theory]
    [InlineData(nameof(FogVolumeSettings.EdgeFade), 1.5f, "1.5")]
    [InlineData(nameof(FogVolumeSettings.EdgeFade), -0.25f, "-0.25")]
    [InlineData(nameof(FogVolumeSettings.Noise), 2f, "2")]
    [InlineData(nameof(FogVolumeSettings.Noise), float.NaN, "NaN")]
    public void A_fade_or_noise_outside_0_to_1_is_refused(string field, float value, string shown)
    {
        var settings = field == nameof(FogVolumeSettings.EdgeFade) ? Mist with { EdgeFade = value } : Mist with { Noise = value };

        var error = ThrowsUnderSwedish(settings);

        Assert.StartsWith($"The fog volume's {field} is {shown}, but it must be from 0 to 1.", error.Message);
    }

    [Theory]
    [InlineData(0f, "0")]
    [InlineData(-1.5f, "-1.5")]
    [InlineData(float.PositiveInfinity, "Infinity")]
    public void A_noise_size_that_is_not_a_finite_positive_number_is_refused(float size, string shown)
    {
        var error = ThrowsUnderSwedish(Mist with { NoiseSize = size });

        Assert.StartsWith($"The fog volume's NoiseSize is {shown}, but it must be more than 0 metres.", error.Message);
    }

    [Fact]
    public void A_wind_that_is_not_finite_is_refused()
    {
        var error = ThrowsUnderSwedish(Mist with { Wind = new Vector3(0.5f, float.NaN, 0f) });

        Assert.StartsWith("The fog volume's wind is <0.5, NaN, 0>, but it must be finite.", error.Message);
    }

    [Fact]
    public void A_refused_volume_is_not_placed_and_its_key_stays_free()
    {
        var view = new View();
        Assert.Throws<ArgumentException>(() => view.FogVolume(1, FogShape.Box, Matrix4x4.Identity, new FogVolumeSettings(-1f)));

        view.FogVolume(1, FogShape.Box, Matrix4x4.Identity, Mist);

        Assert.Equal(1, view.DrawnFogVolumes.Length);
    }

    [Fact]
    public void A_box_is_full_inside_and_empty_outside()
    {
        var box = new FogVolumeDraw(FogShape.Box, Matrix4x4.CreateScale(4f) * Matrix4x4.CreateTranslation(10f, 0f, 0f), Mist);

        Assert.Equal(0.3f, FogVolumePack.Density(box, new Vector3(10f, 0f, 0f), 0f), 5);
        Assert.Equal(0.3f, FogVolumePack.Density(box, new Vector3(10.5f, -0.5f, 0.5f), 0f), 5);
        Assert.Equal(0f, FogVolumePack.Density(box, new Vector3(12.1f, 0f, 0f), 0f));
        Assert.Equal(0f, FogVolumePack.Density(box, new Vector3(10f, 0f, 0f) + new Vector3(0f, 2.5f, 0f), 0f));
    }

    [Fact]
    public void Edge_fade_rises_from_nothing_at_the_surface()
    {
        // The box's surface is at x 0.5 and its centre at 0, so a quarter of the way in is x 0.375. With a fade over half
        // the way in, that point is halfway up the fade: half the density.
        var box = new FogVolumeDraw(FogShape.Box, Matrix4x4.Identity, new FogVolumeSettings(0.8f) { EdgeFade = 0.5f });

        Assert.Equal(0.4f, FogVolumePack.Density(box, new Vector3(0.375f, 0f, 0f), 0f), 4);
        Assert.Equal(0f, FogVolumePack.Density(box, new Vector3(0.5f, 0f, 0f), 0f), 4);
        Assert.Equal(0.8f, FogVolumePack.Density(box, new Vector3(0.25f, 0f, 0f), 0f), 4);
    }

    [Fact]
    public void A_hard_edge_is_full_right_up_to_the_surface()
    {
        var ball = new FogVolumeDraw(FogShape.Ellipsoid, Matrix4x4.Identity, new FogVolumeSettings(0.8f) { EdgeFade = 0f });

        Assert.Equal(0.8f, FogVolumePack.Density(ball, new Vector3(0.49f, 0f, 0f), 0f));
        Assert.Equal(0f, FogVolumePack.Density(ball, new Vector3(0.36f, 0.36f, 0f), 0f));
    }

    [Fact]
    public void A_cylinder_is_round_across_and_flat_at_its_ends()
    {
        var cylinder = new FogVolumeDraw(FogShape.Cylinder, Matrix4x4.Identity, new FogVolumeSettings(1f) { EdgeFade = 0f });

        Assert.Equal(1f, FogVolumePack.Density(cylinder, new Vector3(0f, 0.49f, 0f), 0f));
        Assert.Equal(1f, FogVolumePack.Density(cylinder, new Vector3(0.3f, 0f, 0.3f), 0f));
        Assert.Equal(0f, FogVolumePack.Density(cylinder, new Vector3(0.4f, 0f, 0.4f), 0f));
        Assert.Equal(0f, FogVolumePack.Density(cylinder, new Vector3(0f, 0.51f, 0f), 0f));
    }

    [Fact]
    public void A_cone_is_empty_beside_its_apex()
    {
        // At y 0.45 the cone is 0.025 across from its axis, so 0.3 out is well outside, while its middle is full.
        var cone = new FogVolumeDraw(FogShape.Cone, Matrix4x4.Identity, new FogVolumeSettings(1f) { EdgeFade = 0f });

        Assert.Equal(0f, FogVolumePack.Density(cone, new Vector3(0.3f, 0.45f, 0f), 0f));
        Assert.Equal(1f, FogVolumePack.Density(cone, new Vector3(0f, 0.45f, 0f), 0f));
        Assert.Equal(1f, FogVolumePack.Density(cone, new Vector3(0.4f, -0.45f, 0f), 0f));
    }

    [Fact]
    public void A_volume_packs_its_inverse_and_a_sphere_that_holds_it()
    {
        var transform = Matrix4x4.CreateScale(2f, 6f, 4f) * Matrix4x4.CreateTranslation(1f, 2f, 3f);
        var draw = new FogVolumeDraw(FogShape.Cylinder, transform,
            new FogVolumeSettings(0.5f) { Color = new Vector3(0.2f, 0.4f, 0.6f), EdgeFade = 0.1f, Noise = 0.3f, NoiseSize = 4f, Wind = new Vector3(1f, 0f, -1f) });
        var packed = new GpuFogVolume[1];

        FogVolumePack.Pack([draw], packed);

        var gpu = packed[0];
        Assert.Equal(new Vector3(0f, 0f, 0f), Vector3.Transform(new Vector3(1f, 2f, 3f), gpu.WorldToLocal), new Vector3Comparer(1e-5f));
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 0.5f), gpu.ColorDensity);
        Assert.Equal(new Vector4((float)FogShape.Cylinder, 0.1f, 0.3f, 0.25f), gpu.ShapeFadeNoise);
        Assert.Equal(new Vector3(1f, 0f, -1f), new Vector3(gpu.WindBounds.X, gpu.WindBounds.Y, gpu.WindBounds.Z));

        // The unit shapes fit in a sphere of radius √3 / 2, stretched by the longest axis, here 6.
        Assert.Equal(6f * MathF.Sqrt(3f) / 2f, gpu.WindBounds.W, 4);
        Assert.Equal(new Vector4(1f, 2f, 3f, 0f), gpu.Centre);
    }

    [Fact]
    public void Fog_volumes_need_no_global_fog()
    {
        var clear = new FogSettings(0f);

        Assert.Equal(Vector4.Zero, VolumetricFog.Pack(clear, 0, RenderEffects.None, false, false));
        Assert.Equal(new Vector4(48f, 1f, 0.6f, 0f), VolumetricFog.Pack(clear, 1, RenderEffects.None, false, false));

        // The scene's fog switched off leaves the volumes.
        Assert.Equal(new Vector4(48f, 1f, 0.6f, 0f), VolumetricFog.Pack(new FogSettings(0.01f), 1, RenderEffects.Fog, false, false));
    }

    [Theory]
    [InlineData(RenderEffects.FogVolumes)]
    [InlineData(RenderEffects.VolumetricFog)]
    public void Fog_volumes_off_leaves_clear_air_clear(RenderEffects off)
    {
        Assert.Equal(Vector4.Zero, VolumetricFog.Pack(new FogSettings(0f), 3, off, false, false));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Fog_volumes_are_not_drawn_into_probes_or_under_water(bool capturingProbe, bool underwater)
    {
        Assert.Equal(Vector4.Zero, VolumetricFog.Pack(new FogSettings(0f), 3, RenderEffects.None, capturingProbe, underwater));
    }

    private sealed class Vector3Comparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= tolerance;

        public int GetHashCode(Vector3 value) => 0;
    }

    /// <summary>Places a volume with these settings under a Swedish culture, which writes decimals with a comma, and returns the error.</summary>
    private static ArgumentException ThrowsUnderSwedish(FogVolumeSettings settings)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var error = Assert.Throws<ArgumentException>(() => new View().FogVolume(1, FogShape.Box, Matrix4x4.Identity, settings));
            Assert.Equal("settings", error.ParamName);
            return error;
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
