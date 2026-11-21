using System.Numerics;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// The oil-paint look, read from walls facing the camera with every other effect off: one covered in grey noise and
// one dark on the left and light on the right. Paint turns noise into flat strokes and keeps edges hard, where a blur
// would soften them. The brush grows with the window, and the shared one is too small for strokes to span many
// texels, so these tests draw on a large window of their own. It may hold more pixels than asked for, so places are
// read as parts of the picture.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class PaintGpuTests : IDisposable
{
    private const int Size = 1024;

    // Four pixels a texel across the picture: finer than a stroke, coarser than the half-size picture paint works at.
    private const int NoiseSize = 256;
    private const int EdgeSize = 1024;

    // Half the width of what a 60 degree view sees, per metre away.
    private static readonly float HalfWide = MathF.Tan(MathF.PI / 6f);

    private static readonly PaintSettings Paint = new() { Amount = 1f, BrushSize = PaintSettings.MaxBrushSize };

    private readonly ITestOutputHelper _output;
    private readonly GpuFixture _gpu = new(disableRayTracing: false, size: Size);
    private readonly MeshHandle _quad;
    private readonly List<TextureHandle> _textures = [];
    private readonly List<MaterialHandle> _materials = [];

    // How many pixels across and down the last picture read back has.
    private int _wide = Size;

    public PaintGpuTests(ITestOutputHelper output)
    {
        _output = output;
        var (vertices, indices) = TestMeshes.Quad();
        _quad = _gpu.Renderer.Assets.CreateMesh(vertices, indices);
    }

    [Fact]
    public void Paint_flattens_noise_into_strokes_but_keeps_its_brightness()
    {
        int errors = _gpu.ValidationErrors;
        var draws = new[] { Wall(Noisy(), distance: 4f, left: false) };

        var plain = Render(default, draws);
        var painted = Render(Paint, draws);
        float before = Spread(plain, 0.2f, 0.8f);
        float after = Spread(painted, 0.2f, 0.8f);
        float plainLight = Light(plain, 0.2f, 0.8f);
        float paintedLight = Light(painted, 0.2f, 0.8f);
        _output.WriteLine($"Within a stroke shades stray by {before:F1} steps unpainted and {after:F1} painted; the light is {plainLight:F3} and {paintedLight:F3}.");

        Assert.True(before > 20f, $"Unpainted, shades stray by only {before:F1} steps, so the wall isn't noisy enough to test.");
        // The picture at half size, laid back unfiltered, still strays by about four fifths as much as the noise.
        Assert.True(after < before * 0.6f, $"Painted, shades stray by {after:F1} steps, against {before:F1} unpainted.");
        Assert.True(MathF.Abs(paintedLight / plainLight - 1f) < 0.05f, $"Painting moved the light from {plainLight:F3} to {paintedLight:F3}.");
        Assert.Equal(errors, _gpu.ValidationErrors);
    }

    [Fact]
    public void Paint_keeps_an_edge_hard()
    {
        int errors = _gpu.ValidationErrors;
        var draws = new[] { Wall(Edge(), distance: 4f, left: false) };

        float plain = EdgeWidth(Render(default, draws));
        float painted = EdgeWidth(Render(Paint, draws));
        _output.WriteLine($"The edge is {plain:F1} pixels wide unpainted and {painted:F1} painted.");

        // Across an edge the brush reaches about 7 pixels, so a blur that far spreads the edge's middle half over about 7.
        Assert.True(painted < plain + 3f, $"Painted, the edge is {painted:F1} pixels wide, against {plain:F1} unpainted.");
        Assert.Equal(errors, _gpu.ValidationErrors);
    }

    // The picture's dither moves each pixel by up to two steps from one frame to the next, so a pixel counts as changed
    // when it moves by more than that.
    [Fact]
    public void Amount_0_and_the_switch_off_leave_every_pixel_as_it_was()
    {
        int errors = _gpu.ValidationErrors;
        var draws = new[] { Wall(Noisy(), distance: 4f, left: false) };

        var plain = Render(default, draws);
        int none = Changed(plain, Render(Paint with { Amount = 0f }, draws), 0f, 1f);
        int off = Changed(plain, Render(Paint, draws, RenderEffects.Paint), 0f, 1f);
        int painted = Changed(plain, Render(Paint, draws), 0f, 1f);
        _output.WriteLine($"Pixels changed: {none} at amount 0, {off} switched off and {painted} painted.");

        Assert.Equal(0, none);
        Assert.Equal(0, off);
        Assert.True(painted > 10000, $"Painting changed only {painted} pixels.");
        Assert.Equal(errors, _gpu.ValidationErrors);
    }

    [Fact]
    public void Paint_given_a_distance_leaves_what_is_near_and_paints_what_is_far()
    {
        int errors = _gpu.ValidationErrors;
        // A near wall over the left half of the picture, in front of a far one that fills it.
        var noisy = Noisy();
        var draws = new[] { Wall(noisy, distance: 3f, left: true), Wall(noisy, distance: 100f, left: false) };
        var far = Paint with { Near = 20f, Far = 50f };

        var plain = Render(default, draws);
        var painted = Render(far, draws);
        float nearBefore = Spread(plain, 0.05f, 0.4f);
        float farBefore = Spread(plain, 0.6f, 0.95f);
        float farAfter = Spread(painted, 0.6f, 0.95f);
        int changed = Changed(plain, painted, 0.05f, 0.4f);
        _output.WriteLine($"Near, shades stray by {nearBefore:F1} steps and {changed} pixels changed; far, by {farBefore:F1} unpainted and {farAfter:F1} painted.");

        Assert.True(nearBefore > 20f, $"Unpainted, the near wall's shades stray by only {nearBefore:F1} steps.");
        Assert.Equal(0, changed);
        Assert.True(farAfter < farBefore * 0.5f, $"Painted, the far wall's shades stray by {farAfter:F1} steps, against {farBefore:F1} unpainted.");
        Assert.Equal(errors, _gpu.ValidationErrors);
    }

    public void Dispose()
    {
        _gpu.Renderer.Assets.DestroyMesh(_quad);
        foreach (var material in _materials)
        {
            _gpu.Renderer.Assets.DestroyMaterial(material);
        }

        foreach (var texture in _textures)
        {
            _gpu.Renderer.Assets.DestroyTexture(texture);
        }

        _gpu.Dispose();
    }

    // Grey noise, each texel its own shade, from a fixed seed so every run sees the same.
    private MaterialHandle Noisy()
    {
        var random = new Random(7);
        return Textured(NoiseSize, NoiseSize, _ => (byte)random.Next(40, 216));
    }

    // Dark on the left half, light on the right, with the edge between them a pixel wide.
    private MaterialHandle Edge() => Textured(EdgeSize, 8, i => i % EdgeSize < EdgeSize / 2 ? (byte)40 : (byte)210);

    private MaterialHandle Textured(int width, int height, Func<int, byte> shade)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            byte value = shade(i);
            rgba[i * 4] = value;
            rgba[i * 4 + 1] = value;
            rgba[i * 4 + 2] = value;
            rgba[i * 4 + 3] = 255;
        }

        var texture = _gpu.Renderer.Assets.CreateTexture(TextureData.FromPixels(width, height, TextureKind.Color, rgba));
        _textures.Add(texture);
        var plain = new Material { BaseColor = Vector4.One, Roughness = 1f };
        var material = _gpu.Renderer.Assets.CreateMaterial(plain with { Textures = plain.Textures with { BaseColor = texture } });
        _materials.Add(material);
        return material;
    }

    // A wall facing the camera this far away, as wide as the view there, so its texture is as fine at any distance.
    // Left moves it over to cover only the left half of the picture.
    private DrawItem Wall(MaterialHandle material, float distance, bool left)
    {
        float wide = 2f * HalfWide * distance;
        float shift = left ? -wide / 2f : 0f;
        return new DrawItem
        {
            Mesh = _quad,
            IndexCount = 6,
            Material = material,
            Transform = Matrix4x4.CreateScale(wide, wide, 1f) * Matrix4x4.CreateTranslation(shift, 0f, -distance),
        };
    }

    // A few frames first, so the textures are uploaded, then the one read back. Every effect but the paint and the
    // materials is off, and a sun behind the camera lights the walls.
    private byte[] Render(in PaintSettings paint, DrawItem[] draws, RenderEffects off = RenderEffects.None)
    {
        var view = TestSky.View(Size, -Vector3.UnitZ, Vector3.Normalize(new Vector3(0.2f, 0.3f, 1f))) with
        {
            Picture = new PictureSettings { Paint = paint },
            Off = off | (RenderEffects.All & ~(RenderEffects.Paint | RenderEffects.Materials)),
        };
        return TestSky.Capture(_gpu, view, draws, 4, out _wide);
    }

    // How far shades stray from their own block's mean, in steps of 255, over the band of the picture from across to
    // until, in the middle half of its height. A block is 16 pixels across at 1024, about a stroke: a stroke is flat,
    // while noise, or noise only blurred a little, still varies inside one.
    private float Spread(byte[] pixels, float across, float until)
    {
        int block = Math.Max(_wide / 64, 2);
        double sum = 0;
        int count = 0;
        for (int top = _wide / 4; top + block <= _wide * 3 / 4; top += block)
        {
            for (int left = (int)(across * _wide); left + block <= (int)(until * _wide); left += block)
            {
                double total = 0;
                double squares = 0;
                for (int y = top; y < top + block; y++)
                {
                    for (int x = left; x < left + block; x++)
                    {
                        float shade = Shade(pixels, x, y);
                        total += shade;
                        squares += shade * shade;
                    }
                }

                double mean = total / (block * block);
                sum += Math.Sqrt(Math.Max(squares / (block * block) - mean * mean, 0));
                count++;
            }
        }

        return (float)(sum / count);
    }

    // The mean light over the band, back out of the display's curve, since paint averages light rather than shades.
    private float Light(byte[] pixels, float across, float until)
    {
        double sum = 0;
        int count = 0;
        Band(across, until, (x, y) =>
        {
            sum += MathF.Pow(Shade(pixels, x, y) / 255f, 2.2f);
            count++;
        });
        return (float)(sum / count);
    }

    // How many pixels across the edge between dark and light is, from a quarter of the way up to three quarters, on
    // average over the middle half of the rows.
    private float EdgeWidth(byte[] pixels)
    {
        float dark = Shade(pixels, _wide / 4, _wide / 2);
        float light = Shade(pixels, _wide * 3 / 4, _wide / 2);
        float low = dark + (light - dark) * 0.25f;
        float high = dark + (light - dark) * 0.75f;
        int between = 0;
        int rows = 0;
        for (int y = _wide / 4; y < _wide * 3 / 4; y++)
        {
            for (int x = _wide / 4; x < _wide * 3 / 4; x++)
            {
                float shade = Shade(pixels, x, y);
                if (shade > low && shade < high)
                {
                    between++;
                }
            }

            rows++;
        }

        return between / (float)rows;
    }

    private int Changed(byte[] a, byte[] b, float across, float until) => PictureBand.Changed(a, b, _wide, across, until);

    private void Band(float across, float until, Action<int, int> each) => PictureBand.Each(_wide, across, until, each);

    private float Shade(byte[] pixels, int x, int y) => PictureBand.Shade(pixels, _wide, x, y);
}
