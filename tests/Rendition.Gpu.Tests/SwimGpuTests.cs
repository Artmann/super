using System.Numerics;

namespace Rendition.Gpu.Tests;

// Three copies of one quad, half a metre wide, one above the other and three metres from the camera. They look the
// same, so they are one instanced draw, and each has a swim of its own. A quad lies at z = 0 in its own space, a quarter
// of the way into the stroke, so its swim moves the whole of it sideways by amplitude × 0.25 × sin(phase - 2.5).
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class SwimGpuTests(GpuFixture gpu)
{
    // An amplitude of 1.6 at the top of the stroke moves a quad 0.4 of its own width, which is 0.2 m for one scaled
    // to half a metre. The 256 pixels of the picture span 2.49 m at the quads' distance, so that is 21 pixels.
    private const float Amplitude = 1.6f;
    private const float TopOfTheStroke = 2.5f + MathF.PI / 2f;
    private const float BottomOfTheStroke = 2.5f - MathF.PI / 2f;
    private const float Moved = 21f;

    [Fact]
    public void Copies_in_one_draw_each_swim_at_their_own_point_in_the_stroke()
    {
        int errors = gpu.ValidationErrors;
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        try
        {
            var quad = new DrawItem { Mesh = mesh, IndexCount = (uint)indices.Length, Material = gpu.Matte };
            DrawItem[] shoal =
            [
                quad with { Transform = Placed(0.7f), Swim = new SwimMotion(Amplitude, 1f, TopOfTheStroke) },
                quad with { Transform = Placed(0f) },
                quad with { Transform = Placed(-0.7f), Swim = new SwimMotion(Amplitude, 1f, BottomOfTheStroke) },
            ];

            byte[] empty = Capture([]);
            byte[] swimming = Capture(shoal);

            // The copy 0.7 m up is centred on line 56, the middle one on 128 and the one 0.7 m down on 200.
            Assert.Equal(128f + Moved, Centre(swimming, empty, line: 56), 2f);
            Assert.Equal(128f, Centre(swimming, empty, line: 128), 2f);
            Assert.Equal(128f - Moved, Centre(swimming, empty, line: 200), 2f);
        }
        finally
        {
            gpu.Renderer.DestroyMesh(mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private static Matrix4x4 Placed(float y) => Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation(0f, y, 0f);

    private byte[] Capture(DrawItem[] draws)
    {
        var view = gpu.View(DebugView.None);
        // No anti-aliasing, so one frame is the whole picture.
        view.Off = RenderEffects.AntiAliasing;
        view.EnvironmentIntensity = 1f;
        return gpu.Renderer.Capture(view, draws, out _, out _);
    }

    // The middle column of what the draws cover on a line: the pixels that differ from the picture of nothing.
    private static float Centre(byte[] pixels, byte[] empty, int line)
    {
        float sum = 0f;
        int covered = 0;
        for (int x = 0; x < 256; x++)
        {
            int at = (line * 256 + x) * 4;
            int differs = Math.Abs(pixels[at] - empty[at]) + Math.Abs(pixels[at + 1] - empty[at + 1])
                + Math.Abs(pixels[at + 2] - empty[at + 2]);
            if (differs > 24)
            {
                sum += x + 0.5f;
                covered++;
            }
        }

        Assert.True(covered is > 40 and < 64, $"Line {line} has {covered} pixels of a quad that should be 51 wide.");
        return sum / covered;
    }
}
