using System.Globalization;
using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>Depth of field: the thin lens circle the GPU blurs by, and what the shaders are told for a frame.</summary>
public class ThinLensTests
{
    [Fact]
    public void A_point_at_the_focus_distance_has_no_circle()
    {
        Assert.Equal(0f, ThinLens.CircleOfConfusion(depth: 10f, focusDistance: 10f, aperture: 2.8f, focalLengthMm: 50f));
    }

    [Fact]
    public void Circle_matches_the_thin_lens_formula()
    {
        // c = (f / N) * |S2 - S1| / S2 * f / (S1 - f), with f = 0.05 m, N = 2.8, S1 = 10 m and S2 = 5 m:
        // f / N = 0.017857 m, |5 - 10| / 5 = 1, f / (S1 - f) = 0.05 / 9.95 = 0.0050251,
        // so c = 0.017857 * 0.0050251 = 0.0000897 m, 0.0897 mm on the sensor.
        float circle = ThinLens.CircleOfConfusion(depth: 5f, focusDistance: 10f, aperture: 2.8f, focalLengthMm: 50f);

        Assert.Equal(0.0897e-3f, circle, 0.0005e-3f);
    }

    [Fact]
    public void A_point_twice_as_far_as_the_focus_blurs_half_as_much_as_the_horizon()
    {
        // At S2 = 20 m, |20 - 10| / 20 = 0.5, so 0.0897 mm halves to 0.0449 mm. At the horizon the ratio is 1.
        float twiceAsFar = ThinLens.CircleOfConfusion(depth: 20f, focusDistance: 10f, aperture: 2.8f, focalLengthMm: 50f);
        float horizon = ThinLens.CircleOfConfusion(float.PositiveInfinity, focusDistance: 10f, aperture: 2.8f, focalLengthMm: 50f);

        Assert.Equal(0.0449e-3f, twiceAsFar, 0.0005e-3f);
        Assert.Equal(0.0897e-3f, horizon, 0.0005e-3f);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(3f)]
    [InlineData(10f)]
    [InlineData(1000f)]
    [InlineData(float.PositiveInfinity)]
    public void Aperture_zero_turns_it_off(float depth)
    {
        Assert.Equal(0f, ThinLens.CircleOfConfusion(depth, focusDistance: 10f, aperture: 0f, focalLengthMm: 50f));
    }

    [Fact]
    public void Negative_focus_distance_is_refused()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => ThinLens.CircleOfConfusion(depth: 5f, focusDistance: -2.5f, aperture: 2.8f, focalLengthMm: 50f));

            Assert.Equal("The focus distance must be 0 or more metres, but it is -2.5. (Parameter 'focusDistance')", error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_focus_inside_the_focal_length_is_refused_once_the_aperture_opens()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => ThinLens.CircleOfConfusion(depth: 5f, focusDistance: 0.025f, aperture: 2.8f, focalLengthMm: 50f));

            Assert.Equal(
                "The focus distance must be further than the focal length of 50 mm, but it is 0.025 m. (Parameter 'focusDistance')",
                error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_thousandth_of_the_sensor_is_a_thousandth_of_the_image()
    {
        // 0.036 mm on a 36 mm sensor is 1/1000 of it, so 1.6 pixels of a 1600 pixel image.
        Assert.Equal(1.6f, ThinLens.InPixels(0.036e-3f, 1600), 1e-4f);
    }

    [Fact]
    public void The_frame_is_told_the_circle_at_the_horizon_in_the_scene_s_pixels()
    {
        var post = new PostSettings { FocusDistance = 10f, Aperture = 2.8f, FocalLength = 50f };

        var lens = DepthOfFieldConstants.For(post, RenderEffects.None, sceneWidth: 1600);

        // 0.0897 mm of 36 mm is 1/401.3 of the width: 3.99 of 1600 pixels.
        Assert.NotNull(lens);
        Assert.Equal(10f, lens.Value.FocusDistance);
        Assert.Equal(3.99f, lens.Value.PixelsAtInfinity, 0.01f);
    }

    [Fact]
    public void Half_the_render_scale_halves_the_circle_in_pixels()
    {
        var post = new PostSettings { FocusDistance = 10f, Aperture = 2.8f, FocalLength = 50f };

        var full = DepthOfFieldConstants.For(post, RenderEffects.None, sceneWidth: 1600);
        var half = DepthOfFieldConstants.For(post, RenderEffects.None, sceneWidth: 800);

        Assert.NotNull(full);
        Assert.NotNull(half);
        Assert.Equal(full.Value.PixelsAtInfinity / 2f, half.Value.PixelsAtInfinity, 1e-4f);
    }

    [Fact]
    public void A_focal_length_of_zero_means_a_35_mm_lens()
    {
        // f = 0.035 m, N = 8, S1 = 11 m: 0.004375 * 0.035 / 10.965 = 0.00001396 m, 0.01396 mm,
        // which is 0.6206 of 1600 pixels across 36 mm.
        var lens = DepthOfFieldConstants.For(new PostSettings { FocusDistance = 11f, Aperture = 8f }, RenderEffects.None, 1600);

        Assert.NotNull(lens);
        Assert.Equal(0.6206f, lens.Value.PixelsAtInfinity, 0.001f);
    }

    [Fact]
    public void Nothing_is_drawn_with_the_aperture_at_zero_the_switch_off_or_a_lens_a_view_would_refuse()
    {
        var open = new PostSettings { FocusDistance = 10f, Aperture = 2.8f };

        Assert.NotNull(DepthOfFieldConstants.For(open, RenderEffects.None, 1600));
        Assert.Null(DepthOfFieldConstants.For(open with { Aperture = 0f }, RenderEffects.None, 1600));
        Assert.Null(DepthOfFieldConstants.For(open, RenderEffects.DepthOfField, 1600));
        Assert.Null(DepthOfFieldConstants.For(open with { FocusDistance = -1f }, RenderEffects.None, 1600));
        Assert.Null(DepthOfFieldConstants.For(open with { FocusDistance = 0f }, RenderEffects.None, 1600));
    }

    [Fact]
    public void A_view_refuses_a_negative_aperture_with_how_to_turn_it_off()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var view = new View();

            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => view.Finish(new PostSettings { FocusDistance = 4f, Aperture = -1.4f }));

            Assert.Equal(
                "The aperture must be an f-number such as 2.8, or 0 for no depth of field, but it is -1.4. (Parameter 'settings')",
                error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_view_accepts_a_focus_distance_of_zero_while_the_aperture_is_shut()
    {
        var view = new View();

        var error = Record.Exception(() => view.Finish(new PostSettings { FocusDistance = 0f, Aperture = 0f }));

        Assert.Null(error);
        Assert.Equal(0f, view.Post.FocusDistance);
    }

    [Theory]
    [InlineData(11f, 8f, 35f, true)]
    [InlineData(0f, 0f, 35f, true)]
    [InlineData(0.035f, 2.8f, 35f, false)]
    [InlineData(0.036f, 2.8f, 35f, true)]
    [InlineData(-1f, 0f, 35f, false)]
    [InlineData(float.NaN, 2.8f, 35f, false)]
    [InlineData(10f, -2.8f, 35f, false)]
    [InlineData(10f, float.PositiveInfinity, 35f, false)]
    [InlineData(10f, 2.8f, 0f, false)]
    [InlineData(10f, 2.8f, float.NaN, false)]
    public void IsValid_says_whether_Check_would_throw(float focusDistance, float aperture, float focalLengthMm, bool valid)
    {
        var error = Record.Exception(() => ThinLens.Check(focusDistance, aperture, focalLengthMm, "lens"));

        Assert.Equal(valid, ThinLens.IsValid(focusDistance, aperture, focalLengthMm));
        if (valid)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.IsType<ArgumentOutOfRangeException>(error);
        }
    }

    [Fact]
    public void Changing_the_focus_does_not_capture_the_probes_again()
    {
        var sun = new[] { new DirectionalLight { Direction = Vector3.UnitY, Radiance = new Vector3(3f), CastsShadow = true } };
        var sharp = new FrameView { Lights = sun, EnvironmentIntensity = 1f };
        var blurred = sharp;
        blurred.Post = new PostSettings { FocusDistance = 3f, Aperture = 1.4f, FocalLength = 85f };
        blurred.Off = RenderEffects.DepthOfField;

        Assert.Equal(Renderer.LightingHash(sharp, 1), Renderer.LightingHash(blurred, 1));
        Assert.Equal(Renderer.ReflectionLightingHash(sharp, 1), Renderer.ReflectionLightingHash(blurred, 1));
    }
}
