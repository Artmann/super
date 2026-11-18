using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition.Gpu.Tests;

// A photographed sky is drawn into the environment's cubes in place of the gradient or the physical sky. These project
// the sky cube to L1, which the bakes and probes read, and read the picture behind an empty scene.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class SkyImageGpuTests(GpuFixture gpu)
{
    private const int FaceSize = 16;
    private const int Size = 64;

    // An even sky of radiance L projects to 4 pi Y0 L = 3.5449 L in the constant band.
    private const float ConstantBand = 3.5449f;

    [Fact]
    public void An_even_image_lights_like_a_gradient_of_its_colour_and_its_exposure_scales_it()
    {
        int errors = gpu.ValidationErrors;
        var colour = new Vector3(0.25f, 0.5f, 0.75f);
        var image = gpu.Renderer.CreateSkyImage(Image(_ => colour));

        var even = Project(new AnalyticSky { Image = image });
        AssertClose(ConstantBand * colour, Rgb(even[0]), 3e-3f);
        for (int band = 1; band < 4; band++)
        {
            AssertClose(Vector3.Zero, Rgb(even[band]), 3e-3f);
        }

        var brighter = Project(new AnalyticSky { Image = image, Exposure = 1f });
        AssertClose(2f * ConstantBand * colour, Rgb(brighter[0]), 6e-3f);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_rotated_image_lights_as_the_image_turned_counterclockwise_seen_from_above()
    {
        int errors = gpu.ValidationErrors;
        var east = gpu.Renderer.CreateSkyImage(Image(direction => direction.X > 0f ? Vector3.One : Vector3.Zero));
        var north = gpu.Renderer.CreateSkyImage(Image(direction => direction.Z < 0f ? Vector3.One : Vector3.Zero));

        // A quarter turn counterclockwise, seen from above, takes +X to -Z.
        var turned = Project(new AnalyticSky { Image = east, Rotation = MathF.PI / 2f });
        var straight = Project(new AnalyticSky { Image = north });
        var unturned = Project(new AnalyticSky { Image = east });
        for (int band = 0; band < 4; band++)
        {
            AssertClose(Rgb(straight[band]), Rgb(turned[band]), 5e-3f);
        }

        float apart = 0f;
        for (int band = 1; band < 4; band++)
        {
            apart += Vector3.Distance(Rgb(straight[band]), Rgb(unturned[band]));
        }

        Assert.True(apart > 0.5f, $"The east and north halves light alike: {apart}.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void The_image_shows_behind_the_scene()
    {
        int errors = gpu.ValidationErrors;
        var red = gpu.Renderer.CreateSkyImage(Image(_ => new Vector3(1f, 0.05f, 0.05f)));
        var blue = gpu.Renderer.CreateSkyImage(Image(_ => new Vector3(0.05f, 0.05f, 1f)));

        var reds = Picture(red);
        var blues = Picture(blue);
        int centre = (Size / 2 * Size + Size / 2) * 4;
        Assert.True(reds[centre] > reds[centre + 2] + 50, $"Under the red sky the centre is {reds[centre]}, {reds[centre + 2]}.");
        Assert.True(blues[centre + 2] > blues[centre] + 50, $"Under the blue sky the centre is {blues[centre]}, {blues[centre + 2]}.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void The_bakes_see_a_new_rotation_or_image()
    {
        int errors = gpu.ValidationErrors;
        var first = gpu.Renderer.CreateSkyImage(Image(direction => direction.X > 0f ? Vector3.One : Vector3.Zero));
        var second = gpu.Renderer.CreateSkyImage(Image(direction => direction.X > 0f ? Vector3.Zero : Vector3.One));

        Project(new AnalyticSky { Image = first });
        ulong plain = gpu.Renderer.EnvironmentHash;
        Project(new AnalyticSky { Image = first, Rotation = 1f });
        ulong rotated = gpu.Renderer.EnvironmentHash;
        Project(new AnalyticSky { Image = second });
        ulong other = gpu.Renderer.EnvironmentHash;

        Assert.NotEqual(plain, rotated);
        Assert.NotEqual(plain, other);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_sky_image_without_six_faces_is_refused()
    {
        var data = Image(_ => Vector3.One);
        var error = Assert.Throws<ArgumentException>(() => gpu.Renderer.CreateSkyImage(new SkyImageData { Faces = data.Faces[..5] }));
        Assert.StartsWith("A sky image needs 6 faces, not 5.", error.Message);
    }

    private Vector4[] Project(AnalyticSky sky)
    {
        var view = gpu.View(DebugView.None);
        view.Environment.Sky = sky;
        gpu.Renderer.Render(view, []);
        return gpu.Renderer.ProjectSky();
    }

    private byte[] Picture(SkyImageHandle image)
    {
        var view = new FrameView
        {
            Width = Size,
            Height = Size,
            View = Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0f, 0.3f, -1f), Vector3.UnitY),
            Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
            CameraPosition = Vector3.Zero,
            Environment = new EnvironmentSettings { Intensity = 1f, ShowAsBackground = true, Sky = new AnalyticSky { Image = image } },
            Off = RenderEffects.AntiAliasing | RenderEffects.LightProbes | RenderEffects.Bloom,
        };
        for (int frame = 0; frame < 2; frame++)
        {
            gpu.Renderer.Render(view, []);
        }

        return gpu.Renderer.Capture(view, [], out _, out _);
    }

    // A small cooked sky whose every texel has the radiance the function gives towards its centre, with no sun.
    private static SkyImageData Image(Func<Vector3, Vector3> radiance)
    {
        var faces = new TextureData[6];
        for (int face = 0; face < 6; face++)
        {
            var mips = new List<byte[]>();
            for (int size = FaceSize; size >= 1; size /= 2)
            {
                var texels = new Half[size * size * 4];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        var uv = new Vector2((x + 0.5f) / size * 2f - 1f, (y + 0.5f) / size * 2f - 1f);
                        var colour = radiance(Vector3.Normalize(SkyImageData.FaceDirection(face, uv)));
                        int at = (y * size + x) * 4;
                        texels[at] = (Half)colour.X;
                        texels[at + 1] = (Half)colour.Y;
                        texels[at + 2] = (Half)colour.Z;
                        texels[at + 3] = (Half)1f;
                    }
                }

                mips.Add(MemoryMarshal.AsBytes(texels.AsSpan()).ToArray());
            }

            faces[face] = new TextureData
            {
                Width = FaceSize, Height = FaceSize, Kind = TextureKind.Linear, Format = TextureFormat.Rgba16f, Mips = [.. mips],
            };
        }

        return new SkyImageData { Faces = faces };
    }

    private static Vector3 Rgb(Vector4 value) => new(value.X, value.Y, value.Z);

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"Expected {expected}, got {actual}");
    }
}
