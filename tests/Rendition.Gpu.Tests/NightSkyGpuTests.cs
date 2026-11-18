using System.Numerics;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// The moon and the stars are drawn per pixel over the sky's background, so these read the picture: an empty scene at
// night, looking up at the moon.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class NightSkyGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 256;

    // The sun 40 degrees down, below the stars' fade, and the moon 50 degrees up straight ahead.
    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0f, -0.64f, 0.77f));
    private static readonly Vector3 Moon = Vector3.Normalize(new Vector3(0f, 0.77f, -0.64f));

    [Fact]
    public void The_moon_shows_where_it_is_and_stars_show_round_it_unless_hidden()
    {
        int errors = gpu.ValidationErrors;

        var starry = Render(new AnalyticSky { Turbidity = 2.5f, Moon = Moon }, RenderEffects.None);
        var hidden = Render(new AnalyticSky { Turbidity = 2.5f, Moon = Moon, HideStars = true }, RenderEffects.None);
        var switchedOff = Render(new AnalyticSky { Turbidity = 2.5f, Moon = Moon }, RenderEffects.Stars);
        var moonless = Render(new AnalyticSky { Turbidity = 2.5f }, RenderEffects.None);

        float moon = Brightness(starry, Size / 2, Size / 2);
        float sky = Median(hidden);
        int stars = Points(starry, sky);
        output.WriteLine($"Moon centre {moon:F0}, sky {sky:F0}, stars {stars}, hidden {Points(hidden, sky)}, switched off {Points(switchedOff, sky)}.");

        Assert.True(moon > sky + 100f, $"The moon's centre is {moon:F0}, against a sky of {sky:F0}.");
        Assert.True(Brightness(moonless, Size / 2, Size / 2) < sky + 10f, "Without a moon the centre should be plain sky.");
        Assert.True(stars > 20, $"Only {stars} stars showed.");
        Assert.Equal(0, Points(hidden, sky));
        Assert.Equal(0, Points(switchedOff, sky));
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // A few frames first, then the one read back, without the effects that would add frame-to-frame noise.
    private byte[] Render(AnalyticSky sky, RenderEffects off)
    {
        var view = new FrameView
        {
            Width = Size,
            Height = Size,
            View = Matrix4x4.CreateLookAt(Vector3.Zero, Moon, Vector3.UnitY),
            Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
            CameraPosition = Vector3.Zero,
            Environment = new EnvironmentSettings { Intensity = 1f, ShowAsBackground = true, Sky = sky },
            Lights = [new DirectionalLight { Direction = Sun, Radiance = new Vector3(5f) }],
            Picture = new PictureSettings { Post = new PostSettings { Exposure = 3f } },
            Off = off | RenderEffects.AntiAliasing | RenderEffects.LightProbes | RenderEffects.Bloom,
        };
        for (int frame = 0; frame < 2; frame++)
        {
            gpu.Renderer.Render(view, []);
        }

        return gpu.Renderer.Capture(view, [], out _, out _);
    }

    private static float Brightness(byte[] pixels, int x, int y)
    {
        int at = (y * Size + x) * 4;
        return (pixels[at] + pixels[at + 1] + pixels[at + 2]) / 3f;
    }

    private static float Median(byte[] pixels)
    {
        var all = new float[Size * Size];
        for (int i = 0; i < all.Length; i++)
        {
            all[i] = Brightness(pixels, i % Size, i / Size);
        }

        Array.Sort(all);
        return all[all.Length / 2];
    }

    // Pixels well above the sky away from the moon, in the picture's outer half.
    private static int Points(byte[] pixels, float sky)
    {
        int count = 0;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                bool nearMoon = Math.Abs(x - Size / 2) < Size / 4 && Math.Abs(y - Size / 2) < Size / 4;
                if (!nearMoon && Brightness(pixels, x, y) > sky + 20f)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
