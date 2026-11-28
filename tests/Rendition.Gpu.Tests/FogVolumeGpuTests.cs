using System.Numerics;
using static Rendition.Gpu.Tests.TestPixels;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// Fog volumes are drawn into the volumetric fog's grid, so these check the picture: a thick box between the camera and a
// wall hides the wall where it stands and nowhere else, and a coloured box lit by the sun takes its colour.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class FogVolumeGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 256;

    // Half the width of each square averaged: one at the picture's centre, inside the box, and one in a corner, beside it.
    private const int SampleRadius = 16;

    // Two captures of the same frame differ by a step or two at most.
    private const float SameTolerance = 4f;

    // The fog keeps 90% of last frame's cells, so it takes this many frames to settle.
    private const int SettleFrames = 40;

    // A box 2 metres across, 5 metres from the camera, so it covers the middle half of the picture.
    private static readonly Matrix4x4 BoxAt = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(0f, 0f, -2f);

    private static readonly DirectionalLight[] Frontlight =
    [
        new() { Direction = Vector3.Normalize(new Vector3(0f, 0.2f, 1f)), Radiance = new Vector3(3f) },
    ];

    // Behind the box, shining at the camera, so the fog scatters it forwards. Dim enough that the tone mapping
    // keeps the fog's hue rather than bleaching it towards white.
    private static readonly DirectionalLight[] Backlight =
    [
        new() { Direction = Vector3.Normalize(new Vector3(0f, 0.2f, -1f)), Radiance = new Vector3(1.5f) },
    ];

    [Fact]
    public void A_fog_volume_darkens_what_is_behind_it()
    {
        WithWall(new Vector4(0.8f, 0.8f, 0.8f, 1f), wall =>
        {
            FogVolumeDraw[] box = [new(FogShape.Box, BoxAt, new FogVolumeSettings(2f) { EdgeFade = 0f })];

            var clear = Render(View(Frontlight, []), wall);
            var fogged = Render(View(Frontlight, box), wall);
            var switchedOff = Render(View(Frontlight, box, RenderEffects.FogVolumes), wall);
            output.WriteLine($"Centre: clear {Show(Centre(clear))}, fogged {Show(Centre(fogged))}, fog volumes off {Show(Centre(switchedOff))}.");
            output.WriteLine($"Corner: clear {Show(Corner(clear))}, fogged {Show(Corner(fogged))}.");

            float clearLight = Brightness(Centre(clear));
            Assert.True(clearLight > 60f, $"The lit wall's centre is only {Show(Centre(clear))}, so the check proves nothing.");
            Assert.True(
                Brightness(Centre(fogged)) < clearLight * 0.5f,
                $"Through 2 metres of fog the wall's centre was {Show(Centre(fogged))}, not half as bright as {Show(Centre(clear))}.");
            Assert.True(
                Vector3.Distance(Corner(fogged), Corner(clear)) <= SameTolerance,
                $"Beside the box the wall was {Show(Corner(fogged))}, but with no fog it is {Show(Corner(clear))}.");
            Assert.True(
                Vector3.Distance(Centre(switchedOff), Centre(clear)) <= SameTolerance,
                $"With fog volumes off the centre was {Show(Centre(switchedOff))}, but with no fog it is {Show(Centre(clear))}.");
        });
    }

    [Fact]
    public void A_coloured_fog_volume_takes_its_colour()
    {
        WithWall(new Vector4(0.02f, 0.02f, 0.02f, 1f), wall =>
        {
            FogVolumeDraw[] green = [new(FogShape.Box, BoxAt, new FogVolumeSettings(1f) { Color = new Vector3(0.1f, 1f, 0.1f) })];

            var clear = Centre(Render(View(Backlight, []), wall));
            var lit = Centre(Render(View(Backlight, green), wall));
            output.WriteLine($"Centre: clear {Show(clear)}, green fog {Show(lit)}.");

            Assert.True(lit.Y > clear.Y + 30f, $"The sunlit green fog's centre was {Show(lit)}, hardly lighter than {Show(clear)}.");
            Assert.True(lit.Y > lit.X * 2f, $"The sunlit green fog's centre was {Show(lit)}, not much greener than it is red.");
        });
    }

    // Runs the body with a wall that fills the picture behind where the box goes, then destroys it and checks the
    // validation errors did not grow.
    private void WithWall(Vector4 colour, Action<DrawItem> body)
    {
        int errors = gpu.ValidationErrors;
        var quad = TestMeshes.Quad();
        var material = gpu.Renderer.Assets.CreateMaterial(new Material { BaseColor = colour, Roughness = 0.9f });
        var wall = gpu.Renderer.Assets.CreateDrawItem(quad.Vertices, quad.Indices, material)
            with { Transform = Matrix4x4.CreateScale(20f) * Matrix4x4.CreateTranslation(0f, 0f, -4f) };
        try
        {
            body(wall);
        }
        finally
        {
            gpu.Renderer.Assets.DestroyMesh(wall.Mesh);
            gpu.Renderer.Assets.DestroyMaterial(material);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_settled_frame_with_volumetric_fog_allocates_no_more_than_one_without()
    {
        const int Frames = 60;
        WithWall(new Vector4(0.8f, 0.8f, 0.8f, 1f), wall =>
        {
            DrawItem[] draws = [wall];
            FogVolumeDraw[] box = [new(FogShape.Box, BoxAt, new FogVolumeSettings(2f))];
            var foggy = View(Frontlight, box);
            var clear = View(Frontlight, box, RenderEffects.VolumetricFog);

            // The first frames of each make what the renderer keeps, so both run once before either is measured.
            Allocated(clear);
            Allocated(foggy);
            long without = Allocated(clear);
            long with = Allocated(foggy);
            output.WriteLine($"{Frames} frames allocated {with} bytes with volumetric fog, {without} without.");

            Assert.True(with <= without, $"{Frames} frames allocated {with} bytes with volumetric fog, {without} without.");

            long Allocated(FrameView view)
            {
                // Warm up, so lists that grow to the frame's size have grown.
                for (int frame = 0; frame < 20; frame++)
                {
                    gpu.Renderer.Render(view, draws);
                }

                long before = GC.GetTotalAllocatedBytes(precise: true);
                for (int frame = 0; frame < Frames; frame++)
                {
                    gpu.Renderer.Render(view, draws);
                }

                return GC.GetTotalAllocatedBytes(precise: true) - before;
            }
        });
    }

    // The fixture's view lit by the sun given, with no fog of the scene's own and the effects that add frame-to-frame
    // noise or change the exposure left off.
    private FrameView View(DirectionalLight[] sun, FogVolumeDraw[] volumes, RenderEffects off = RenderEffects.None)
    {
        var view = gpu.View(DebugView.None);
        view.Off = off | RenderEffects.AntiAliasing | RenderEffects.Reflections | RenderEffects.LightProbes
            | RenderEffects.SunShadows | RenderEffects.AutoExposure | RenderEffects.Bloom;
        view.Lights = sun;
        view.Weather.Fog = new FogSettings();
        view.Extras.FogVolumes = volumes;
        return view;
    }

    private byte[] Render(FrameView view, DrawItem wall)
    {
        DrawItem[] draws = [wall];
        for (int frame = 0; frame < SettleFrames; frame++)
        {
            gpu.Renderer.Render(view, draws);
        }

        return gpu.Renderer.Capture(view, draws, out _, out _);
    }

    private static Vector3 Centre(byte[] pixels) => Mean(pixels, Size, Size / 2, Size / 2, SampleRadius);

    private static Vector3 Corner(byte[] pixels) => Mean(pixels, Size, SampleRadius + 4, SampleRadius + 4, SampleRadius);
}
