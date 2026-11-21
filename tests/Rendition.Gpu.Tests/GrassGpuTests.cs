using System.Numerics;
using static Rendition.Gpu.Tests.TestPixels;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// Grass is scattered on the GPU over its ground, so these check the picture: a grey floor seen from above turns green
// where blades stand on it, and stays grey with the switch off or the density at 0.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class GrassGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 256;

    // Half the width of the square averaged at the picture's centre, which lands on the floor a few metres ahead.
    private const int SampleRadius = 24;

    // Two captures of the same frame differ by a step or two at most.
    private const float SameTolerance = 4f;

    // How many times its red and its blue the green of grass must be: the meadow's is about 1.3 times its red, grey's 1.
    private const float Greener = 1.15f;

    // A few frames, so the scatter's counts and the history have settled.
    private const int Frames = 6;

    private static readonly Vector3 Eye = new(0f, 1.5f, 3f);

    private static readonly DirectionalLight[] Sun =
    [
        new() { Direction = Vector3.Normalize(new Vector3(0.3f, 1f, 0.4f)), Radiance = new Vector3(3f) },
    ];

    [Fact]
    public void Grass_on_a_grey_floor_turns_it_green()
    {
        WithFloor(floor =>
        {
            var bare = Centre(Render(View([]), floor));
            var grown = Centre(Render(View([new GrassDraw(floor, new GrassSettings())]), floor));
            output.WriteLine($"Centre: bare {Show(bare)}, with grass {Show(grown)}.");

            Assert.True(Brightness(bare) > 40f, $"The bare floor's centre is only {Show(bare)}, so the check proves nothing.");
            Assert.True(MathF.Abs(bare.Y - bare.X) < 8f, $"The bare grey floor's centre was {Show(bare)}, already coloured.");
            // As a share, not a gap: dense grass is dark at its roots, so its channels are all low.
            Assert.True(
                grown.Y > grown.X * Greener && grown.Y > grown.Z * Greener,
                $"With grass the floor's centre was {Show(grown)}, not clearly greener than red and blue; bare it is {Show(bare)}.");
        });
    }

    [Fact]
    public void Grass_switched_off_or_at_no_density_leaves_the_floor_bare()
    {
        WithFloor(floor =>
        {
            var bare = Centre(Render(View([]), floor));
            var switchedOff = Centre(Render(View([new GrassDraw(floor, new GrassSettings())], RenderEffects.Grass), floor));
            var none = Centre(Render(View([new GrassDraw(floor, new GrassSettings { Density = 0f })]), floor));
            output.WriteLine($"Centre: bare {Show(bare)}, grass off {Show(switchedOff)}, density 0 {Show(none)}.");

            Assert.True(
                Vector3.Distance(switchedOff, bare) <= SameTolerance,
                $"With grass switched off the floor's centre was {Show(switchedOff)}, but bare it is {Show(bare)}.");
            Assert.True(
                Vector3.Distance(none, bare) <= SameTolerance,
                $"With grass at density 0 the floor's centre was {Show(none)}, but bare it is {Show(bare)}.");
        });
    }

    [Fact]
    public void Thicker_grass_covers_more_of_the_ground()
    {
        WithFloor(floor =>
        {
            float thin = Covered(Render(Above([new GrassDraw(floor, new GrassSettings { Density = 0.3f })]), floor));
            float half = Covered(Render(Above([new GrassDraw(floor, new GrassSettings { Density = 0.6f })]), floor));
            float full = Covered(Render(Above([new GrassDraw(floor, new GrassSettings { Density = 1f })]), floor));
            output.WriteLine($"Ground covered from above: density 0.3 {thin:P0}, 0.6 {half:P0}, 1 {full:P0}.");

            Assert.True(thin > 0.03f, $"Grass at density 0.3 covered only {thin:P0} of the ground, as if none stood.");
            Assert.True(half > thin * 1.3f, $"Grass at density 0.6 covered {half:P0}, not clearly more than {thin:P0} at 0.3.");
            Assert.True(full > half * 1.15f, $"Grass at density 1 covered {full:P0}, not clearly more than {half:P0} at 0.6.");
        });
    }

    [Fact]
    public void Thin_grass_grows_in_tufts_with_bare_ground_between()
    {
        WithFloor(floor =>
        {
            var pixels = Render(Above([new GrassDraw(floor, new GrassSettings { Density = 0.3f })]), floor);
            float unevenness = BlockUnevenness(pixels);
            output.WriteLine($"Thin grass from above covers {Covered(pixels):P0}, its blocks' coverage varying by {unevenness:F2} of the mean.");

            Assert.True(Covered(pixels) > 0.03f, $"Thin grass covered only {Covered(pixels):P0} of the ground, so the check proves nothing.");
            Assert.True(
                unevenness > TuftUnevenness,
                $"Thin grass's coverage varies by {unevenness:F2} of the mean from block to block, under {TuftUnevenness}: sprinkled evenly, not in tufts.");
        });
    }

    // How much the share of ground covered in each block of the picture varies, over its mean, for grass in tufts at
    // least: sprinkled evenly at the same density it varies by about 0.7, in tufts by about 1.1.
    private const float TuftUnevenness = 0.9f;

    // The block of the picture a coverage share is counted over: about 40 centimetres of ground from above.
    private const int Block = 16;

    // The share of the picture's pixels that are grass rather than the grey floor.
    private static float Covered(byte[] pixels)
    {
        int covered = 0;
        for (int i = 0; i < Size * Size; i++)
        {
            if (IsGrass(pixels, i))
            {
                covered++;
            }
        }

        return covered / (float)(Size * Size);
    }

    // The spread of the share of grass pixels across the picture's blocks, over its mean.
    private static float BlockUnevenness(byte[] pixels)
    {
        int blocks = Size / Block;
        var shares = new float[blocks * blocks];
        for (int by = 0; by < blocks; by++)
        {
            for (int bx = 0; bx < blocks; bx++)
            {
                int count = 0;
                for (int y = 0; y < Block; y++)
                {
                    for (int x = 0; x < Block; x++)
                    {
                        if (IsGrass(pixels, (by * Block + y) * Size + bx * Block + x))
                        {
                            count++;
                        }
                    }
                }

                shares[by * blocks + bx] = count / (float)(Block * Block);
            }
        }

        float mean = shares.Average();
        float spread = MathF.Sqrt(shares.Select(share => (share - mean) * (share - mean)).Average());
        return spread / mean;
    }

    // Grass, green or dry, is yellower than the grey floor: its green is well above its blue.
    private static bool IsGrass(byte[] pixels, int index) => pixels[4 * index + 1] > pixels[4 * index + 2] + 12;

    // Runs the body with a level grey floor 40 metres across under the camera, then destroys it and checks the
    // validation errors did not grow.
    private void WithFloor(Action<DrawItem> body)
    {
        int errors = gpu.ValidationErrors;
        var quad = TestMeshes.Quad();
        var material = gpu.Renderer.Assets.CreateMaterial(new Material { BaseColor = new Vector4(0.5f, 0.5f, 0.5f, 1f), Roughness = 0.9f });
        var floor = gpu.Renderer.Assets.CreateDrawItem(quad.Vertices, quad.Indices, material)
            with { Transform = Matrix4x4.CreateScale(40f) * Matrix4x4.CreateRotationX(-MathF.PI / 2f) };
        try
        {
            body(floor);
        }
        finally
        {
            gpu.Renderer.Assets.DestroyMesh(floor.Mesh);
            gpu.Renderer.Assets.DestroyMaterial(material);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // The fixture's view moved up and looking down at the floor ahead, with the effects that add frame-to-frame noise or
    // change the exposure left off.
    private FrameView View(GrassDraw[] grass, RenderEffects off = RenderEffects.None)
    {
        var view = gpu.View(DebugView.None);
        view.View = Matrix4x4.CreateLookAt(Eye, new Vector3(0f, 0f, -1f), Vector3.UnitY);
        view.CameraPosition = Eye;
        view.Off = off | RenderEffects.AntiAliasing | RenderEffects.Reflections | RenderEffects.LightProbes
            | RenderEffects.SunShadows | RenderEffects.AutoExposure | RenderEffects.Bloom;
        view.Lights = Sun;
        view.Extras.Grass = grass;
        return view;
    }

    // The same view from six metres straight above the floor, with the wind still, so the blades are seen from their
    // tips and none sway between frames.
    private FrameView Above(GrassDraw[] grass)
    {
        var view = View(grass, RenderEffects.Wind);
        var eye = new Vector3(0f, 6f, 0f);
        view.View = Matrix4x4.CreateLookAt(eye, Vector3.Zero, -Vector3.UnitZ);
        view.CameraPosition = eye;
        return view;
    }

    private byte[] Render(FrameView view, DrawItem floor)
    {
        DrawItem[] draws = [floor];
        for (int frame = 0; frame < Frames; frame++)
        {
            gpu.Renderer.Render(view, draws);
        }

        return gpu.Renderer.Capture(view, draws, out _, out _);
    }

    private static Vector3 Centre(byte[] pixels) => Mean(pixels, Size, Size / 2, Size / 2, SampleRadius);
}
