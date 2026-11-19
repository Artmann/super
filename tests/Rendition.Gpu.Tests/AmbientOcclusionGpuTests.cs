using System.Numerics;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// A wall four metres wide meets a floor a metre behind the origin, and the camera looks at the corner between them. The
// same frame is drawn with ambient occlusion and without, and the two are compared a line of pixels at a time. Turned a
// quarter of the way round the camera's aim, the same corner stands upright, for what works along columns.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class AmbientOcclusionGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    // Twice the frames the history averages, so it has filled.
    private const int SettledFrames = 16;

    // The corner is 0.5 metres off the camera's aim and 4 metres away, which puts it on line 166 of 256. Four lines
    // nearer the middle is five centimetres of wall beside it.
    private const int BesideTheCorner = 162;
    private const int OpenWall = 30;

    [Theory]
    [InlineData(OcclusionResolution.Half)]
    [InlineData(OcclusionResolution.Full)]
    public void A_corner_is_darker_with_occlusion_and_the_open_wall_is_not(OcclusionResolution resolution)
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            DrawItem[] room = Room(quad, upright: false);

            byte[] with = Capture(room, RenderEffects.None, resolution);
            byte[] without = Capture(room, RenderEffects.AmbientOcclusion, resolution);

            float corner = LineBrightness(with, BesideTheCorner);
            float cornerUnoccluded = LineBrightness(without, BesideTheCorner);
            float open = LineBrightness(with, OpenWall);
            float openUnoccluded = LineBrightness(without, OpenWall);
            Assert.True(cornerUnoccluded > 20f, $"The wall reads {cornerUnoccluded} unoccluded, too dark to show occlusion.");
            Assert.True(corner < cornerUnoccluded * 0.93f,
                $"By the corner the wall reads {corner} with occlusion and {cornerUnoccluded} without.");
            Assert.True(MathF.Abs(open - openUnoccluded) < openUnoccluded * 0.03f,
                $"The open wall reads {open} with occlusion and {openUnoccluded} without.");
        }
        finally
        {
            gpu.Renderer.Assets.DestroyMesh(quad.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // Every pixel of a line beside the corner is the same distance from it, so each has the same occlusion. What is left
    // between neighbours is the grain of the few directions each pixel looks in, which the blur is there to average
    // away. A level corner shows what it does across the picture, and an upright one what it does down it.
    [Theory]
    [InlineData(false, OcclusionResolution.Half)]
    [InlineData(true, OcclusionResolution.Half)]
    [InlineData(false, OcclusionResolution.Full)]
    [InlineData(true, OcclusionResolution.Full)]
    public void Occlusion_is_smooth_along_a_corner_level_or_upright(bool upright, OcclusionResolution resolution)
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            byte[] with = Capture(Room(quad, upright), RenderEffects.None, resolution);
            byte[] without = Capture(Room(quad, upright), RenderEffects.AmbientOcclusion, resolution);

            float occluded = LineBrightness(with, BesideTheCorner, upright);
            float unoccluded = LineBrightness(without, BesideTheCorner, upright);
            Assert.True(occluded < unoccluded * 0.93f,
                $"By the corner the wall reads {occluded} with occlusion and {unoccluded} without, so there is no grain to smooth.");

            float roughest = 0f;
            for (int along = 65; along < 192; along++)
            {
                float here = Brightness(with, along, BesideTheCorner, upright);
                float before = Brightness(with, along - 1, BesideTheCorner, upright);
                roughest = MathF.Max(roughest, MathF.Abs(here - before));
            }

            Assert.True(roughest <= 3f, $"Neighbours along the corner differ by up to {roughest} of 255.");
        }
        finally
        {
            gpu.Renderer.Assets.DestroyMesh(quad.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // At half resolution one pixel in four works out its occlusion and the rest take theirs from those, so the corner
    // must come out as dark as when every pixel works out its own.
    [Fact]
    public void Half_resolution_darkens_the_corner_as_much_as_full()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            DrawItem[] room = Room(quad, upright: false);

            float unoccluded = LineBrightness(Capture(room, RenderEffects.AmbientOcclusion, OcclusionResolution.Full), BesideTheCorner);
            float half = LineBrightness(Capture(room, RenderEffects.None, OcclusionResolution.Half), BesideTheCorner);
            float full = LineBrightness(Capture(room, RenderEffects.None, OcclusionResolution.Full), BesideTheCorner);

            Assert.True(full < unoccluded * 0.93f, $"By the corner the wall reads {full} at full resolution and {unoccluded} unoccluded.");
            Assert.True(MathF.Abs(half - full) < (unoccluded - full) * 0.1f,
                $"By the corner the wall reads {half} at half resolution, {full} at full and {unoccluded} unoccluded.");
        }
        finally
        {
            gpu.Renderer.Assets.DestroyMesh(quad.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // A panel hangs two metres in front of the corner, too far to shade it or be shaded by it, and covers the left half
    // of the picture up to column 128. At half resolution a pixel beside the panel's edge takes its occlusion from the
    // pixels round it that were worked out, some of them across the edge, and those must not count: counting them pulls
    // the two columns at the edge towards each other. So the step from the panel's last column to the wall beside it
    // must stay about as tall as when every pixel works out its own. The six lines of wall just above the corner are
    // the darkest behind the panel, and a column's mean over them evens out the grain.
    [Fact]
    public void At_half_resolution_occlusion_does_not_spill_across_the_edge_of_a_panel_in_front_of_the_corner()
    {
        const int Edge = 128;
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var panel = quad with { Transform = Matrix4x4.CreateTranslation(-0.5f, 0f, 1f) };
            DrawItem[] scene = [.. Room(quad, upright: false), panel];

            byte[] half = Capture(scene, RenderEffects.None, OcclusionResolution.Half);
            byte[] full = Capture(scene, RenderEffects.None, OcclusionResolution.Full);

            float stepAtFull = AboveTheCorner(full, Edge - 1) - AboveTheCorner(full, Edge);
            float stepAtHalf = AboveTheCorner(half, Edge - 1) - AboveTheCorner(half, Edge);
            Assert.True(stepAtFull >= 3f,
                $"The panel's last column is {stepAtFull} brighter than the wall beside it, too alike to tell one spilling onto the other.");
            Assert.True(stepAtHalf >= stepAtFull * 0.75f,
                $"Across the panel's edge the picture steps by {stepAtHalf} at half resolution and {stepAtFull} at full.");
        }
        finally
        {
            gpu.Renderer.Assets.DestroyMesh(quad.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // Each frame looks in new directions, so a pixel by the corner changes from one frame to the next by its grain.
    // Averaged over the frames before, it changes much less. Measured at half resolution, where the blur is as wide
    // with the history as without, so the history is all that differs. The picture's dither changes every frame too,
    // so what the wall changes by with no occlusion at all is taken off both.
    [Fact]
    public void With_history_the_corner_changes_less_from_frame_to_frame()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            DrawItem[] room = Room(quad, upright: false);

            float dither = ChangeBetweenFrames(room, RenderEffects.AmbientOcclusion);
            float steady = ChangeBetweenFrames(room, RenderEffects.None) - dither;
            float grainy = ChangeBetweenFrames(room, RenderEffects.OcclusionHistory) - dither;
            string changes = $"From one frame to the next the occlusion by the corner changes by {steady} with history and {grainy} without, "
                + $"over the {dither} the dither changes by.";
            output.WriteLine(changes);

            Assert.True(grainy > 0.2f, changes);
            Assert.True(steady < grainy * 0.3f, changes);
        }
        finally
        {
            gpu.Renderer.Assets.DestroyMesh(quad.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // The history only averages, so the corner must come out as dark as the mean of many frames without it.
    [Theory]
    [InlineData(OcclusionResolution.Half)]
    [InlineData(OcclusionResolution.Full)]
    public void With_history_the_corner_is_as_dark_as_without(OcclusionResolution resolution)
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            DrawItem[] room = Room(quad, upright: false);

            // With the bounce, whose light the history averages too. It reads the last frame's picture, so the frames
            // without history also follow one another.
            float unoccluded = LineBrightness(Capture(room, RenderEffects.AmbientOcclusion, resolution, bounce: true), BesideTheCorner);
            Capture(room, RenderEffects.OcclusionHistory, resolution, SettledFrames, bounce: true);
            float without = 0f;
            for (int frame = 0; frame < SettledFrames; frame++)
            {
                byte[] pixels = Capture(room, RenderEffects.OcclusionHistory, resolution, carryOn: true, bounce: true);
                without += LineBrightness(pixels, BesideTheCorner) / SettledFrames;
            }

            float with = LineBrightness(Capture(room, RenderEffects.None, resolution, SettledFrames, bounce: true), BesideTheCorner);
            output.WriteLine($"By the corner the wall reads {with} with history, {without} without and {unoccluded} unoccluded.");

            // Less than a single frame darkens it, since the floor's bounced light has had frames to build up.
            Assert.True(without < unoccluded - 2f, $"By the corner the wall reads {without} with occlusion and {unoccluded} without.");
            Assert.True(MathF.Abs(with - without) < (unoccluded - without) * 0.1f,
                $"By the corner the wall reads {with} with history, {without} without and {unoccluded} unoccluded.");
        }
        finally
        {
            gpu.Renderer.Assets.DestroyMesh(quad.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // A panel hangs a metre in front of the corner for long enough to fill the history, and is then taken away. The
    // wall it hid has no history of its own, and must not take the panel's, which was hardly shaded: the first frame
    // without the panel must show the corner as dark as it is when the panel never hung there.
    [Fact]
    public void A_wall_uncovered_this_frame_does_not_keep_the_occlusion_of_what_covered_it()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            DrawItem[] room = Room(quad, upright: false);
            var panel = quad with { Transform = Matrix4x4.CreateScale(4f) * Matrix4x4.CreateTranslation(0f, 0f, 0f) };

            float never = LineBrightness(Capture(room, RenderEffects.None, OcclusionResolution.Half, SettledFrames), BesideTheCorner);
            float unoccluded = LineBrightness(Capture(room, RenderEffects.AmbientOcclusion, OcclusionResolution.Half), BesideTheCorner);
            Capture([.. room, panel], RenderEffects.None, OcclusionResolution.Half, SettledFrames);
            float uncovered = LineBrightness(Capture(room, RenderEffects.None, OcclusionResolution.Half, carryOn: true), BesideTheCorner);
            output.WriteLine($"By the corner the wall reads {uncovered} just uncovered, {never} never covered and {unoccluded} unoccluded.");

            Assert.True(never < unoccluded - 2f, $"By the corner the wall reads {never} with occlusion and {unoccluded} without.");
            Assert.True(MathF.Abs(uncovered - never) < (unoccluded - never) * 0.2f,
                $"By the corner the wall reads {uncovered} just uncovered, {never} never covered and {unoccluded} unoccluded.");
        }
        finally
        {
            gpu.Renderer.Assets.DestroyMesh(quad.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // How much the pixels of the lines above the corner change between two frames drawn one after the other, on
    // average, once the history has had frames enough to fill. The lines stop short of the corner on line 166: at half
    // resolution a block of four pixels that lies across it is traced for the wall one frame and the floor the next,
    // so what it shows changes with the frame whatever the history holds.
    private float ChangeBetweenFrames(DrawItem[] room, RenderEffects off)
    {
        byte[] first = Capture(room, off, OcclusionResolution.Half, SettledFrames);
        byte[] second = Capture(room, off, OcclusionResolution.Half, carryOn: true);
        float change = 0f;
        int pixels = 0;
        for (int line = 150; line <= 161; line++)
        {
            for (int along = 64; along < 192; along++)
            {
                change += MathF.Abs(Brightness(first, along, line, upright: false) - Brightness(second, along, line, upright: false));
                pixels++;
            }
        }

        return change / pixels;
    }

    // The mean of a column over the six lines above the corner, which is on line 166.
    private static float AboveTheCorner(byte[] pixels, int column)
    {
        float sum = 0f;
        for (int line = 160; line <= 165; line++)
        {
            sum += Brightness(pixels, column, line, upright: false);
        }

        return sum / 6f;
    }

    // Draws the frames asked for and returns the last. More than one gives the occlusion's history time to fill.
    // The history starts empty unless it is to carry on from the frames drawn before.
    // The bounce is left out unless asked for: its light comes from the last frame's picture, whatever that showed.
    private byte[] Capture(
        DrawItem[] draws, RenderEffects off, OcclusionResolution resolution, int frames = 1, bool carryOn = false, bool bounce = false)
    {
        var view = gpu.View(DebugView.None);
        // No anti-aliasing, so one frame is the whole picture, and no light probes, which shade a corner themselves.
        off |= RenderEffects.AntiAliasing | RenderEffects.LightProbes | (bounce ? RenderEffects.None : RenderEffects.ScreenBounce);
        view.Off = off;
        // Lit by the environment alone, which is the light occlusion takes away.
        view.Environment.Intensity = 1f;
        gpu.Renderer.Settings.OcclusionResolution = resolution;
        try
        {
            if (!carryOn)
            {
                // A frame without occlusion drops its history, so no test reads what the one before it left.
                view.Off |= RenderEffects.AmbientOcclusion;
                gpu.Renderer.Capture(view, draws, out _, out _);
                view.Off = off;
            }

            for (int frame = 1; frame < frames; frame++)
            {
                gpu.Renderer.Render(view, draws);
            }

            return gpu.Renderer.Capture(view, draws, out _, out _);
        }
        finally
        {
            gpu.Renderer.Settings.OcclusionResolution = OcclusionResolution.Half;
        }
    }

    // The mean of the middle half of a row, or of a column when the corner is upright, clear of the picture's edges.
    private static float LineBrightness(byte[] pixels, int line, bool upright = false)
    {
        float sum = 0f;
        for (int along = 64; along < 192; along++)
        {
            sum += Brightness(pixels, along, line, upright);
        }

        return sum / 128f;
    }

    private static float Brightness(byte[] pixels, int along, int line, bool upright)
    {
        int at = upright ? (along * 256 + line) * 4 : (line * 256 + along) * 4;
        return (pixels[at] + pixels[at + 1] + pixels[at + 2]) / 3f;
    }

    // Upright, the floor becomes a side wall on the right, and the corner runs down column 166 where it ran along row 166.
    private static DrawItem[] Room(DrawItem quad, bool upright)
    {
        var turn = Matrix4x4.CreateRotationZ(upright ? MathF.PI / 2f : 0f);
        var wall = quad with { Transform = Matrix4x4.CreateScale(4f) * Matrix4x4.CreateTranslation(0f, 0f, -1f) * turn };
        var floor = quad with
        {
            Transform = Matrix4x4.CreateScale(4f) * Matrix4x4.CreateRotationX(-MathF.PI / 2f)
                * Matrix4x4.CreateTranslation(0f, -0.5f, 1f) * turn,
        };

        return [wall, floor];
    }

    private DrawItem CreateQuad()
    {
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.Assets.CreateMesh(vertices, indices);
        return new DrawItem
        {
            Mesh = mesh,
            IndexCount = (uint)indices.Length,
            Material = gpu.Matte,
            Transform = Matrix4x4.Identity,
        };
    }
}
