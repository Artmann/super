using System.Numerics;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// The lens flare, read from a plain sky with the sun up and to the left of the middle and every other effect off. The
// flare is one bright ghost, twice as far from the sun as the middle is, so it lands down and to the right, away from
// the sun and from the wall that covers it in some tests. The window may hold more pixels than asked for, so places
// are read as parts of the picture.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class LensFlareGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 128;

    // At (0.3, 0.3) on the screen: 0.4 of the way from the middle to the edges, through a 60 degree view.
    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(-0.4f * MathF.Tan(MathF.PI / 6f), 0.4f * MathF.Tan(MathF.PI / 6f), -1f));

    // Twice as far from the sun as the middle: at (0.7, 0.7).
    private static readonly LensFlareElement[] Ghost = [new(LensFlareShape.Ghost, 2f, 0.12f, new Vector3(0.5f))];

    private static readonly LensFlareSettings Flare = new() { Elements = Ghost };

    // How many pixels across and down the last picture read back has.
    private int _wide = Size;

    [Fact]
    public void A_sun_in_sight_flares_and_one_behind_a_wall_does_not()
    {
        int errors = gpu.ValidationErrors;
        using var wall = Wall();

        float seen = Ghosted(Flare, []);
        int sources = gpu.Renderer.FrameParts.FrameStages.PostStage.Flare.SourceCount;
        float covered = Ghosted(Flare, wall.Draws);
        output.WriteLine($"The ghost brightens its place by {seen:F1} steps with the sun in sight and {covered:F1} with it behind the wall.");

        Assert.True(seen > 25f, $"The ghost brightened its place by only {seen:F1} steps with the sun in sight.");
        Assert.Equal(1, sources);
        // Still a flaring light to the CPU: only the GPU's depth taps see the wall.
        Assert.Equal(1, gpu.Renderer.FrameParts.FrameStages.PostStage.Flare.SourceCount);
        Assert.True(covered <= 1f, $"The ghost brightened its place by {covered:F1} steps with the sun behind the wall.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Intensity_0_and_the_switch_off_draw_no_flare()
    {
        int errors = gpu.ValidationErrors;

        float none = Ghosted(Flare with { Intensity = 0f }, []);
        int noneSources = gpu.Renderer.FrameParts.FrameStages.PostStage.Flare.SourceCount;
        float off = Difference(Render(Flare, [], RenderEffects.LensFlare), Render(default, [], RenderEffects.LensFlare));
        float doubled = Ghosted(Flare with { Intensity = 2f }, []);
        float once = Ghosted(Flare, []);
        output.WriteLine($"The ghost brightens its place by {none:F1} at intensity 0, {once:F1} at 1, {doubled:F1} at 2; switched off {off:F1}.");

        Assert.True(none <= 1f, $"At intensity 0 the ghost brightened its place by {none:F1} steps.");
        Assert.Equal(0, noneSources);
        Assert.True(doubled > once + 5f, $"At intensity 2 the ghost brightened its place by {doubled:F1} steps, and at 1 by {once:F1}.");
        Assert.True(off <= 1f, $"With the switch off the flare settings moved the ghost's place by {off:F1} steps.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Under_anti_aliasing_a_covered_sun_flares_on_for_three_frames_unless_the_flare_starts_afresh()
    {
        int errors = gpu.ValidationErrors;
        using var wall = Wall();
        var plain = At(Render(default, wall.Draws, RenderEffects.None, frames: 6), 0.7f, 0.7f);

        // In sight for long enough to fill the held frames, then behind the wall: the most of the last four frames is
        // still the sun in sight for three frames, then nothing.
        Render(Flare, [], RenderEffects.None, frames: 6);
        var held = new float[5];
        for (int i = 0; i < held.Length; i++)
        {
            held[i] = TestPixels.Brightness(At(Render(Flare, wall.Draws, RenderEffects.None, frames: 0), 0.7f, 0.7f)) - TestPixels.Brightness(plain);
        }

        // A second light, hidden behind the wall too, changes how many lights flare, which starts the held frames afresh.
        var hidden = Flare with { Lights = new[] { new LensFlareLight(new Vector3(-1.2f, 1.2f, -8f), Vector3.One, 0.1f) } };
        Render(Flare, [], RenderEffects.None, frames: 6);
        float afresh = TestPixels.Brightness(At(Render(hidden, wall.Draws, RenderEffects.None, frames: 0), 0.7f, 0.7f)) - TestPixels.Brightness(plain);
        output.WriteLine($"Covered, frame by frame the ghost brightens its place by {string.Join(", ", held.Select(h => h.ToString("F1")))}; started afresh {afresh:F1}.");

        for (int i = 0; i < 3; i++)
        {
            Assert.True(held[i] > 25f, $"Covered frame {i + 1} brightened the ghost's place by only {held[i]:F1} steps.");
        }

        Assert.True(held[3] <= 1f && held[4] <= 1f, $"Covered frames 4 and 5 brightened the ghost's place by {held[3]:F1} and {held[4]:F1} steps.");
        Assert.True(afresh <= 1f, $"Started afresh behind the wall, the ghost brightened its place by {afresh:F1} steps.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // How much brighter, in steps of 255, the ghost's place is with the flare given than with none, without anti-aliasing.
    private float Ghosted(in LensFlareSettings flare, DrawItem[] draws)
    {
        var without = Render(new LensFlareSettings { Intensity = 0f }, draws, RenderEffects.AntiAliasing);
        var with = Render(flare, draws, RenderEffects.AntiAliasing);
        return TestPixels.Brightness(At(with, 0.7f, 0.7f)) - TestPixels.Brightness(At(without, 0.7f, 0.7f));
    }

    private float Difference(byte[] a, byte[] b) => MathF.Abs(TestPixels.Brightness(At(a, 0.7f, 0.7f)) - TestPixels.Brightness(At(b, 0.7f, 0.7f)));

    private Vector3 At(byte[] pixels, float across, float down) => TestPixels.Around(pixels, _wide, across, down);

    // A few frames first, then the one read back, with every effect but the lens flare and anti-aliasing off.
    private byte[] Render(in LensFlareSettings flare, DrawItem[] draws, RenderEffects off, int frames = 2)
    {
        var view = TestSky.View(Size, -Vector3.UnitZ, Sun) with
        {
            Picture = new PictureSettings { LensFlare = flare },
            Off = off | (RenderEffects.All & ~(RenderEffects.LensFlare | RenderEffects.AntiAliasing)),
        };
        return TestSky.Capture(gpu, view, draws, frames, out _wide);
    }

    // A grey wall up and to the left of the middle, in front of the sun and clear of the ghost.
    private TestWall Wall() => new(gpu, Matrix4x4.CreateScale(3f, 3f, 0.1f) * Matrix4x4.CreateTranslation(-1.2f, 1.2f, -5f));
}
