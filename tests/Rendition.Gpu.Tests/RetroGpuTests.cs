using System.Numerics;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// The retro pixels, read from a wall of grey noise facing the camera with every other effect off. Big pixels show as
// runs of one shade side by side, and fewer levels as only a few shades in the whole picture. The window may hold more
// pixels than asked for, so places are read as parts of the picture.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class RetroGpuTests : IDisposable
{
    private const int Size = 540;

    // Two pixels a texel across the picture, so neighbouring pixels differ unless something makes them alike.
    private const int NoiseSize = 270;

    private static readonly float HalfWide = MathF.Tan(MathF.PI / 6f);

    private readonly ITestOutputHelper _output;
    private readonly GpuFixture _gpu = new(disableRayTracing: false, size: Size);
    private readonly MeshHandle _quad;
    private readonly TextureHandle _texture;
    private readonly MaterialHandle _material;
    private int _wide = Size;

    public RetroGpuTests(ITestOutputHelper output)
    {
        _output = output;
        var (vertices, indices) = TestMeshes.Quad();
        _quad = _gpu.Renderer.Assets.CreateMesh(vertices, indices);
        var random = new Random(7);
        var rgba = new byte[NoiseSize * NoiseSize * 4];
        for (int i = 0; i < NoiseSize * NoiseSize; i++)
        {
            byte value = (byte)random.Next(40, 216);
            rgba[i * 4] = value;
            rgba[i * 4 + 1] = value;
            rgba[i * 4 + 2] = value;
            rgba[i * 4 + 3] = 255;
        }

        _texture = _gpu.Renderer.Assets.CreateTexture(TextureData.FromPixels(NoiseSize, NoiseSize, TextureKind.Color, rgba));
        var plain = new Material { BaseColor = Vector4.One, Roughness = 1f };
        _material = _gpu.Renderer.Assets.CreateMaterial(plain with { Textures = plain.Textures with { BaseColor = _texture } });
    }

    [Fact]
    public void Big_pixels_show_one_shade_across_each_block()
    {
        int errors = _gpu.ValidationErrors;

        float plain = Alike(Render(default));
        // 16 pixels of a 1080 picture are 8 of this one, so 7 of every 8 neighbours share a block.
        float big = Alike(Render(new RetroSettings { PixelSize = 16f }));
        _output.WriteLine($"Neighbours alike: {plain:P0} plain, {big:P0} with big pixels.");

        Assert.True(plain < 0.2f, $"Plain, {plain:P0} of neighbours are alike, so the wall isn't noisy enough to test.");
        Assert.True(big > 0.8f, $"With big pixels only {big:P0} of neighbours are alike.");
        Assert.Equal(errors, _gpu.ValidationErrors);
    }

    [Fact]
    public void Fewer_levels_leave_only_that_many_shades()
    {
        int errors = _gpu.ValidationErrors;

        int plain = Shades(Render(default));
        int four = Shades(Render(new RetroSettings { Levels = 4 }));
        int dithered = Shades(Render(new RetroSettings { Levels = 4, Dither = 1f }));
        _output.WriteLine($"Shades: {plain} plain, {four} at 4 levels and {dithered} at 4 levels dithered.");

        Assert.True(plain > 40, $"Plain, the picture has only {plain} shades.");
        Assert.InRange(four, 2, 4);
        Assert.InRange(dithered, 2, 4);
        Assert.Equal(errors, _gpu.ValidationErrors);
    }

    // The picture's dither moves each pixel by up to two steps from one frame to the next, so a pixel counts as changed
    // when it moves by more than that.
    [Fact]
    public void The_switch_off_leaves_every_pixel_as_it_was()
    {
        int errors = _gpu.ValidationErrors;
        var retro = new RetroSettings { PixelSize = 16f, Levels = 4, Dither = 1f };

        var plain = Render(default);
        int off = Changed(plain, Render(retro, RenderEffects.Retro));
        int on = Changed(plain, Render(retro));
        _output.WriteLine($"Pixels changed: {off} switched off and {on} on.");

        Assert.Equal(0, off);
        Assert.True(on > 10000, $"Retro pixels changed only {on} pixels.");
        Assert.Equal(errors, _gpu.ValidationErrors);
    }

    public void Dispose()
    {
        _gpu.Renderer.Assets.DestroyMesh(_quad);
        _gpu.Renderer.Assets.DestroyMaterial(_material);
        _gpu.Renderer.Assets.DestroyTexture(_texture);
        _gpu.Dispose();
    }

    // A few frames first, so the texture is uploaded, then the one read back. Every effect but the retro pixels and the
    // materials is off, and a sun behind the camera lights the wall, which fills the view.
    private byte[] Render(in RetroSettings retro, RenderEffects off = RenderEffects.None)
    {
        float wide = 2f * HalfWide * 4f;
        var wall = new DrawItem
        {
            Mesh = _quad,
            IndexCount = 6,
            Material = _material,
            Transform = Matrix4x4.CreateScale(wide, wide, 1f) * Matrix4x4.CreateTranslation(0f, 0f, -4f),
        };
        var view = TestSky.View(Size, -Vector3.UnitZ, Vector3.Normalize(new Vector3(0.2f, 0.3f, 1f))) with
        {
            Picture = new PictureSettings { Retro = retro },
            Off = off | (RenderEffects.All & ~(RenderEffects.Retro | RenderEffects.Materials)),
        };
        return TestSky.Capture(_gpu, view, [wall], 4, out _wide);
    }

    // The share of pixels in the middle half of the picture within the dither's two steps of the one on their right.
    private float Alike(byte[] pixels)
    {
        int alike = 0;
        int count = 0;
        Middle((x, y) =>
        {
            if (Math.Abs(Shade(pixels, x, y) - Shade(pixels, x + 1, y)) <= 2f)
            {
                alike++;
            }

            count++;
        });
        return alike / (float)count;
    }

    // How many different green values the middle half of the picture holds.
    private int Shades(byte[] pixels)
    {
        var seen = new HashSet<byte>();
        Middle((x, y) => seen.Add(pixels[(y * _wide + x) * 4 + 1]));
        return seen.Count;
    }

    private int Changed(byte[] a, byte[] b) => PictureBand.Changed(a, b, _wide, 0.25f, 0.75f);

    private void Middle(Action<int, int> each) => PictureBand.Each(_wide, 0.25f, 0.75f, each);

    private float Shade(byte[] pixels, int x, int y) => PictureBand.Shade(pixels, _wide, x, y);
}
