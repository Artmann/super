using System.Numerics;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// Debug lines drawn over a plain sky looking down -z, with a grey wall covering the right half of the picture five
// metres away. Lines are thick, so the squares read around a point lie wholly inside them. Places are parts of the
// picture, since the window may hold more pixels than asked for.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class LineGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 128;
    private const float Thick = 40f;

    private static readonly Vector4 Red = new(1f, 0f, 0f, 1f);

    private int _wide = Size;

    [Fact]
    public void A_line_shows_in_full_over_the_sky_and_a_quarter_of_it_behind_a_wall()
    {
        int errors = gpu.ValidationErrors;
        using var wall = TestWall.RightHalf(gpu);

        // Ten metres away, so behind the wall on the right. At y = 0 it crosses the middle of the picture.
        LineDraw[] lines = [Line(new Vector3(-6f, 0f, -10f), new Vector3(6f, 0f, -10f), hidden: 0.25f)];
        var plain = Render(wall.Draws, []);
        var drawn = Render(wall.Draws, lines);

        var sky = At(drawn, 0.2f, 0.5f);
        var behind = At(drawn, 0.8f, 0.5f);
        var bare = At(plain, 0.8f, 0.5f);
        output.WriteLine($"Over the sky {TestPixels.Show(sky)}, behind the wall {TestPixels.Show(behind)}, the wall alone {TestPixels.Show(bare)}.");

        TestPixels.AssertNear(new Vector3(255f, 0f, 0f), sky, 2f, "a red line over the sky");
        // A quarter of red laid over the wall in linear light.
        var expected = ToScreen((ToLinear(bare) * 0.75f) + new Vector3(0.25f, 0f, 0f));
        TestPixels.AssertNear(expected, behind, 4f, "a red line behind the wall");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_line_in_front_of_the_wall_and_one_on_top_show_in_full()
    {
        int errors = gpu.ValidationErrors;
        using var wall = TestWall.RightHalf(gpu);

        // One three metres away, in front of the wall, along the middle; one behind it but on top, lower down.
        LineDraw[] lines =
        [
            Line(new Vector3(-2f, 0f, -3f), new Vector3(2f, 0f, -3f), hidden: 0.25f),
            Line(new Vector3(-6f, -2.9f, -10f), new Vector3(6f, -2.9f, -10f), hidden: 1f),
        ];
        var drawn = Render(wall.Draws, lines);

        // -2.9 at ten metres is half of the half height of 5.77 down: three quarters of the way down.
        var inFront = At(drawn, 0.8f, 0.5f);
        var onTop = At(drawn, 0.8f, 0.75f);
        output.WriteLine($"In front {TestPixels.Show(inFront)}, on top {TestPixels.Show(onTop)}.");

        TestPixels.AssertNear(new Vector3(255f, 0f, 0f), inFront, 2f, "a line in front of the wall");
        TestPixels.AssertNear(new Vector3(255f, 0f, 0f), onTop, 2f, "a line on top behind the wall");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_line_running_behind_the_camera_still_shows_where_it_is_in_front()
    {
        int errors = gpu.ValidationErrors;

        // From five metres behind the camera to ten in front, a metre to the left and half a metre down. Four metres
        // in front it shows at x = -1 / (4 tan 30) and y = -0.5 / (4 tan 30): across 0.283 and down 0.608.
        LineDraw[] lines = [Line(new Vector3(-1f, -0.5f, 5f), new Vector3(-1f, -0.5f, -10f), hidden: 0.25f)];
        var drawn = Render([], lines);

        var onLine = At(drawn, 0.283f, 0.608f);
        output.WriteLine($"On the line {TestPixels.Show(onLine)}.");
        TestPixels.AssertNear(new Vector3(255f, 0f, 0f), onLine, 2f, "the line four metres in front");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private static LineDraw Line(Vector3 from, Vector3 to, float hidden) =>
        new() { From = from, To = to, Width = Thick, Color = Red, Hidden = hidden };

    private Vector3 At(byte[] pixels, float across, float down) => TestPixels.Around(pixels, _wide, across, down);

    // A screen colour of 0 to 255 in the sRGB curve, as linear light from 0 to 1, and back.
    private static Vector3 ToLinear(Vector3 screen) => new(Linear(screen.X / 255f), Linear(screen.Y / 255f), Linear(screen.Z / 255f));

    private static Vector3 ToScreen(Vector3 linear) => new Vector3(Encoded(linear.X), Encoded(linear.Y), Encoded(linear.Z)) * 255f;

    private static float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float Encoded(float c) => c <= 0.0031308f ? c * 12.92f : (1.055f * MathF.Pow(c, 1f / 2.4f)) - 0.055f;

    // A couple of frames first, then the one read back, with every effect off so nothing moves the lines' colour.
    private byte[] Render(DrawItem[] draws, LineDraw[] lines)
    {
        var view = TestSky.Bare(Size);
        view.Extras.Lines = lines;
        return TestSky.Capture(gpu, view, draws, 2, out _wide);
    }
}
