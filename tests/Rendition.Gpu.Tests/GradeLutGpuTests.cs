using System.Numerics;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// The grade and the tonemap are baked into a table that the tonemap pass reads, with an artist's table laid in at the
// end. These read the picture of a plain sky looking towards the horizon, which runs from pale near the ground to deep
// blue above, so every check sees a range of colours.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class GradeLutGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 128;

    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0.3f, 0.5f, 0.8f));

    [Fact]
    public void A_neutral_artist_table_leaves_the_picture_within_one_step()
    {
        int errors = gpu.ValidationErrors;
        var neutral = gpu.Renderer.CreateColorLut(ColorLutData.Neutral(32));

        var plain = Render(new PostSettings());
        var through = Render(new PostSettings { Grade = new() { Lut = neutral, LutBlend = 1f } });

        // The finish dithers each frame by up to a step, so single pixels of two pictures differ by up to two even
        // with nothing changed. Means over 4 by 4 squares average the dither out and keep a step's error in sight.
        float most = MostBlockDifference(plain, through);
        output.WriteLine($"The neutral table moved a 4 by 4 square's mean by at most {most:F2} steps.");
        Assert.True(most <= 1f, $"The neutral table moved a 4 by 4 square's mean by {most:F2} steps.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void An_artist_table_shows_by_its_blend_and_not_with_its_switch_off()
    {
        int errors = gpu.ValidationErrors;
        var redOnly = gpu.Renderer.CreateColorLut(ColorLutData.From(16, color => color with { Y = 0f, Z = 0f }));

        var plain = TestPixels.Mean(Render(new PostSettings()), Size, Size / 2, Size / 4, 8);
        var half = TestPixels.Mean(Render(new PostSettings { Grade = new() { Lut = redOnly, LutBlend = 0.5f } }), Size, Size / 2, Size / 4, 8);
        var full = TestPixels.Mean(Render(new PostSettings { Grade = new() { Lut = redOnly, LutBlend = 1f } }), Size, Size / 2, Size / 4, 8);
        var none = TestPixels.Mean(Render(new PostSettings { Grade = new() { Lut = redOnly, LutBlend = 0f } }), Size, Size / 2, Size / 4, 8);
        var off = TestPixels.Mean(Render(new PostSettings { Grade = new() { Lut = redOnly, LutBlend = 1f } }, RenderEffects.ColorLut), Size, Size / 2, Size / 4, 8);
        output.WriteLine($"Plain {TestPixels.Show(plain)}, half {TestPixels.Show(half)}, full {TestPixels.Show(full)}, none {TestPixels.Show(none)}, off {TestPixels.Show(off)}.");

        Assert.True(plain.Z > 60f, $"The sky is {TestPixels.Show(plain)}, too dark to show the table.");
        Assert.True(full.Z < 2f && full.Y < 2f, $"With all of a red-only table the sky is {TestPixels.Show(full)}.");
        Assert.InRange(half.Z, plain.Z * 0.5f - 2f, plain.Z * 0.5f + 2f);
        Assert.InRange(full.X, plain.X - 1f, plain.X + 1f);
        Assert.True(Vector3.Distance(plain, none) < 0.5f, $"A blend of 0 gave {TestPixels.Show(none)}, not {TestPixels.Show(plain)}.");
        Assert.True(Vector3.Distance(plain, off) < 0.5f, $"With the switch off the table still gave {TestPixels.Show(off)}.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void The_table_is_baked_again_only_when_the_grade_changes()
    {
        var grade = gpu.Renderer.FrameParts.FrameStages.PostStage.Grade;
        var settings = new PostSettings { Contrast = 0.1f };
        Render(settings);

        int before = grade.Bakes;
        Render(settings);
        int unchanged = grade.Bakes - before;

        Render(settings with { Contrast = 0.2f });
        int changed = grade.Bakes - before;

        // Exposure is worked out a pixel at a time, so it never needs the table baked.
        Render(settings with { Contrast = 0.2f, Exposure = 1f });
        int exposed = grade.Bakes - before;

        output.WriteLine($"Bakes: {unchanged} unchanged, {changed} after a change, {exposed} after an exposure change.");
        Assert.Equal(0, unchanged);
        Assert.Equal(1, changed);
        Assert.Equal(1, exposed);
    }

    [Fact]
    public void The_new_grades_move_the_sky_the_way_they_say()
    {
        int errors = gpu.ValidationErrors;
        var plain = Mean(new PostSettings());

        // Turning the colours 120 degrees takes blue to red.
        var turned = Mean(new PostSettings { Grade = new() { HueShift = 120f } });

        // The mixer adds the blue light to the red.
        var mixed = Mean(new PostSettings { Grade = new() { RedFrom = new Vector3(0f, 0f, 0.5f) } });

        // The sky is a mid-tone, so a red mid-tone tint shows on it and a red highlight tint doesn't.
        var midtones = Mean(new PostSettings { Grade = new() { Midtones = new Vector3(0.2f, 0f, 0f) } });
        var highlights = Mean(new PostSettings { Grade = new() { Highlights = new Vector3(0.2f, 0f, 0f) } });

        // With the balance at 1 most of the picture counts as shade, so a green shade leans the sky green.
        var split = Mean(new PostSettings { Grade = new() { SplitShadows = new Vector3(0f, 0.2f, 0f), SplitBalance = 1f } });
        output.WriteLine($"Plain {TestPixels.Show(plain)}, turned {TestPixels.Show(turned)}, mixed {TestPixels.Show(mixed)}, "
            + $"midtones {TestPixels.Show(midtones)}, highlights {TestPixels.Show(highlights)}, split {TestPixels.Show(split)}.");

        Assert.True(plain.Z > plain.X + 20f, $"The sky is {TestPixels.Show(plain)}, not blue enough to check against.");
        Assert.True(turned.X > turned.Z + 20f, $"Turned 120 degrees the sky is {TestPixels.Show(turned)}, not red.");
        Assert.True(mixed.X > plain.X + 10f, $"Mixing blue into red gave {TestPixels.Show(mixed)} from {TestPixels.Show(plain)}.");
        Assert.True(midtones.X > plain.X + 5f, $"Red mid-tones gave {TestPixels.Show(midtones)} from {TestPixels.Show(plain)}.");
        Assert.True(Vector3.Distance(highlights, plain) < 1f, $"Red highlights moved the mid-tone sky to {TestPixels.Show(highlights)} from {TestPixels.Show(plain)}.");
        Assert.True(split.Y - split.X > plain.Y - plain.X + 4f, $"Green split toning gave {TestPixels.Show(split)} from {TestPixels.Show(plain)}.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private Vector3 Mean(in PostSettings post) => TestPixels.Mean(Render(post), Size, Size / 2, Size / 4, 8);

    // A couple of frames first, then the one read back, with every other effect off so only the grade changes it.
    private byte[] Render(in PostSettings post, RenderEffects off = RenderEffects.None)
    {
        var view = new FrameView
        {
            Width = Size,
            Height = Size,
            View = Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0f, 0.3f, -1f), Vector3.UnitY),
            Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 3f, 1f, 0.1f),
            CameraPosition = Vector3.Zero,
            Environment = new EnvironmentSettings { Intensity = 1f, ShowAsBackground = true, Sky = new AnalyticSky { Turbidity = 2.5f } },
            Lights = [new DirectionalLight { Direction = Sun, Radiance = new Vector3(5f) }],
            Picture = new PictureSettings { Post = post },
            Off = off | (RenderEffects.All & ~(RenderEffects.ColorGrading | RenderEffects.ColorLut)),
        };
        for (int frame = 0; frame < 2; frame++)
        {
            gpu.Renderer.Render(view, []);
        }

        return gpu.Renderer.Capture(view, [], out _, out _);
    }

    // The most the mean of any 4 by 4 square of one channel differs between two pictures, in steps of 255.
    private static float MostBlockDifference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        float most = 0f;
        for (int top = 0; top < Size; top += 4)
        {
            for (int left = 0; left < Size; left += 4)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    int difference = 0;
                    for (int y = top; y < top + 4; y++)
                    {
                        for (int x = left; x < left + 4; x++)
                        {
                            int at = (y * Size + x) * 4 + channel;
                            difference += a[at] - b[at];
                        }
                    }

                    most = MathF.Max(most, MathF.Abs(difference / 16f));
                }
            }
        }

        return most;
    }
}
