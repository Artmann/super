using System.Numerics;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// The clouds of a physical sky, read from the picture: in the sky above, as shadows on the ground, and in the light the
// sky gives a wall turned away from the sun. The camera stands at the origin, over a wide grey floor, with the sun high
// to the west.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class CloudsGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 128;

    // The clouds are marched a quarter of a pixel in four each frame, so they need a few frames to fill in.
    private const int Frames = 6;

    // 50 degrees up in the west, out of sight of the view up into the sky.
    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(-0.64f, 0.77f, 0f));

    private static readonly Vector3 Up = Vector3.Normalize(new Vector3(0f, 1f, -0.5f));
    private static readonly Vector3 Down = Vector3.Normalize(new Vector3(0f, -1f, -0.6f));
    private static readonly Vector3 West = -Vector3.UnitX;

    // The effects whose noise from frame to frame would blur the comparisons.
    private const RenderEffects Steady =
        RenderEffects.AntiAliasing | RenderEffects.Bloom | RenderEffects.LightProbes | RenderEffects.AmbientOcclusion
        | RenderEffects.AutoExposure;

    [Fact]
    public void Clouds_show_in_the_sky_flat_or_marched_and_their_switch_clears_them()
    {
        int errors = gpu.ValidationErrors;

        var clear = Render(Up, CloudSettings.Clear, RenderEffects.None, []);
        var marched = Render(Up, CloudSettings.Cloudy, RenderEffects.None, []);
        var flat = Render(Up, CloudSettings.Cloudy, RenderEffects.VolumetricClouds, []);
        var switchedOff = Render(Up, CloudSettings.Cloudy, RenderEffects.Clouds, []);

        float marchedShare = Changed(clear, marched, 12f);
        float flatShare = Changed(clear, flat, 12f);
        float offShare = Changed(clear, switchedOff, 2f);
        output.WriteLine($"Against a clear sky: marched clouds change {marchedShare:P0} of the pixels, flat ones {flatShare:P0}, switched off {offShare:P1}.");

        Assert.True(marchedShare > 0.1f, $"The marched clouds changed only {marchedShare:P0} of the sky.");
        Assert.True(flatShare > 0.1f, $"The flat clouds changed only {flatShare:P0} of the sky.");
        Assert.True(offShare == 0f, $"With the clouds switched off, {offShare:P1} of the sky still differed from a clear one.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Cloud_shadows_darken_the_sunlit_ground_until_switched_off()
    {
        int errors = gpu.ValidationErrors;
        using var floor = Floor();

        var shaded = TestPixels.Around(Render(Down, CloudSettings.Overcast, RenderEffects.None, floor.Draws), _wide, 0.5f, 0.5f);
        var sunlit = TestPixels.Around(Render(Down, CloudSettings.Overcast, RenderEffects.CloudShadows, floor.Draws), _wide, 0.5f, 0.5f);
        output.WriteLine($"The ground under an overcast sky is {TestPixels.Show(shaded)} with cloud shadows and {TestPixels.Show(sunlit)} without.");

        Assert.True(
            TestPixels.Brightness(shaded) < 0.85f * TestPixels.Brightness(sunlit),
            $"The ground was {TestPixels.Show(shaded)} under the clouds' shadow and {TestPixels.Show(sunlit)} without it.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void An_overcast_sky_greys_the_light_on_a_wall_turned_from_the_sun()
    {
        int errors = gpu.ValidationErrors;
        using var wall = new TestWall(gpu, Matrix4x4.CreateScale(1f, 20f, 20f) * Matrix4x4.CreateTranslation(-3f, 0f, 0f));

        var clear = TestPixels.Around(Render(West, CloudSettings.Clear, RenderEffects.None, wall.Draws), _wide, 0.5f, 0.35f);
        var overcast = TestPixels.Around(Render(West, CloudSettings.Overcast, RenderEffects.None, wall.Draws), _wide, 0.5f, 0.35f);
        output.WriteLine($"The wall in the shade is {TestPixels.Show(clear)} under a clear sky and {TestPixels.Show(overcast)} under an overcast one.");

        Assert.True(Blueness(clear) > 0.1f, $"Under a clear sky the shaded wall was {TestPixels.Show(clear)}, not lit blue by the sky.");
        Assert.True(
            Blueness(overcast) < Blueness(clear) * 0.8f,
            $"The shaded wall was {TestPixels.Show(clear)} under a clear sky and still {TestPixels.Show(overcast)} under an overcast one.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // A look easing in and out clears the sky and clouds it over again, so the targets must stay, and while they're off
    // the flare must see the sun however the last clouds covered it. The sun is low in front, at (0.3, 0.3) on the
    // screen, and the flare's one ghost lands at (0.7, 0.7).
    [Fact]
    public void A_sky_that_clears_and_clouds_over_again_keeps_the_clouds_and_flares_as_a_clear_one()
    {
        int errors = gpu.ValidationErrors;
        var stage = gpu.Renderer.FrameParts.SceneLighting.CloudStage;

        var clearFirst = Flared(CloudSettings.Clear);
        var overcast = Flared(CloudSettings.Overcast);
        int builds = stage.Builds;
        var clearAfter = Flared(CloudSettings.Clear);
        Flared(CloudSettings.Overcast);
        int rebuilt = stage.Builds - builds;

        var first = TestPixels.Around(clearFirst, _wide, 0.7f, 0.7f);
        var dimmed = TestPixels.Around(overcast, _wide, 0.7f, 0.7f);
        var after = TestPixels.Around(clearAfter, _wide, 0.7f, 0.7f);
        output.WriteLine($"The ghost's place is {TestPixels.Show(first)} under a clear sky, {TestPixels.Show(dimmed)} overcast, "
            + $"{TestPixels.Show(after)} clear again; the targets were made {rebuilt} more times.");

        Assert.True(builds > 0, "The overcast sky never made the clouds' targets.");
        Assert.Equal(0, rebuilt);
        Assert.True(
            TestPixels.Brightness(dimmed) < TestPixels.Brightness(first) - 10f,
            $"The overcast sky left the ghost at {TestPixels.Show(dimmed)} against {TestPixels.Show(first)}, so the clouds don't dim it.");
        Assert.True(
            Vector3.Distance(first, after) < 1f,
            $"Clear again, the ghost's place was {TestPixels.Show(after)}, not {TestPixels.Show(first)} as before the clouds.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private byte[] Flared(CloudSettings clouds)
    {
        var sun = Vector3.Normalize(new Vector3(-0.4f * MathF.Tan(MathF.PI / 6f), 0.4f * MathF.Tan(MathF.PI / 6f), -1f));
        LensFlareElement[] ghost = [new(LensFlareShape.Ghost, 2f, 0.12f, new Vector3(0.5f))];
        var view = TestSky.View(Size, -Vector3.UnitZ, sun) with
        {
            Weather = new WeatherSettings { Clouds = clouds },
            Picture = new PictureSettings { LensFlare = new LensFlareSettings { Elements = ghost } },
            Off = Steady,
        };
        return TestSky.Capture(gpu, view, [], Frames, out _wide);
    }

    // How many pixels across the last picture read back is.
    private int _wide = Size;

    private byte[] Render(Vector3 look, CloudSettings clouds, RenderEffects off, DrawItem[] draws)
    {
        var view = TestSky.View(Size, look, Sun) with
        {
            Weather = new WeatherSettings { Clouds = clouds },
            Off = off | Steady,
        };
        return TestSky.Capture(gpu, view, draws, Frames, out _wide);
    }

    // A floor of grey a kilometre across, its top a metre under the camera.
    private TestWall Floor() =>
        new(gpu, Matrix4x4.CreateScale(1000f, 1f, 1000f) * Matrix4x4.CreateTranslation(0f, -1.5f, 0f));

    // How much bluer than red a colour is, against its brightness.
    private static float Blueness(Vector3 colour) => (colour.Z - colour.X) / Math.Max(TestPixels.Brightness(colour), 1f);

    // The share of the pixels whose brightness differs by more than threshold steps between the two pictures.
    private float Changed(byte[] first, byte[] second, float threshold)
    {
        int changed = 0;
        int count = _wide * _wide;
        for (int i = 0; i < count; i++)
        {
            int at = i * 4;
            float a = (first[at] + first[at + 1] + first[at + 2]) / 3f;
            float b = (second[at] + second[at + 1] + second[at + 2]) / 3f;
            if (Math.Abs(a - b) > threshold)
            {
                changed++;
            }
        }

        return changed / (float)count;
    }
}
