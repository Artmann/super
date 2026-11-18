using System.Numerics;
using System.Text;
using Rendition;
using Super.Testing;

namespace Super.Cook.Tests;

public sealed class SkyCookerTests
{
    [Fact]
    public void A_uniform_image_gives_uniform_faces_at_every_level_and_no_sun()
    {
        var color = new Vector3(0.5f, 0.25f, 0.125f);
        var sky = SkyCooker.FromEquirect(Equirect(64, 32, _ => color), 64, 32, faceSize: 8);

        Assert.Equal(6, sky.Faces.Length);
        foreach (var face in sky.Faces)
        {
            Assert.Equal(TextureFormat.Rgba16f, face.Format);
            Assert.Equal(4, face.Mips.Length); // 8, 4, 2, 1.
            foreach (var mip in face.Mips)
            {
                foreach (var texel in Texels(mip))
                {
                    Assert.Equal(color.X, texel.X, 0.002f);
                    Assert.Equal(color.Y, texel.Y, 0.002f);
                    Assert.Equal(color.Z, texel.Z, 0.002f);
                }
            }
        }

        Assert.Equal(Vector3.Zero, sky.SunRadiance);
        Assert.Equal(Vector3.Zero, sky.SunDirection);
    }

    [Fact]
    public void Faces_are_laid_out_as_Vulkan_samples_a_cube()
    {
        // Red towards +X, green towards -Z (the middle of the image), blue up.
        static Vector3 Paint(Vector3 d) => new(MathF.Max(d.X, 0f), MathF.Max(-d.Z, 0f), MathF.Max(d.Y, 0f));
        var sky = SkyCooker.FromEquirect(Equirect(256, 128, Paint), 256, 128, faceSize: 16);

        AssertNear(new Vector3(1f, 0f, 0f), Centre(sky.Faces[0]), 0.08f);
        AssertNear(Vector3.Zero, Centre(sky.Faces[1]), 0.08f);
        AssertNear(new Vector3(0f, 0f, 1f), Centre(sky.Faces[2]), 0.08f);
        AssertNear(Vector3.Zero, Centre(sky.Faces[4]), 0.08f);
        AssertNear(new Vector3(0f, 1f, 0f), Centre(sky.Faces[5]), 0.08f);

        // Vulkan stores the -Z face with its first column towards +X and its last towards -X.
        var minusZ = Texels(sky.Faces[5].Mips[0]).ToArray();
        Assert.True(minusZ[8 * 16 + 0].X > 0.5f, $"first column of -Z is {minusZ[8 * 16 + 0]}");
        Assert.True(minusZ[8 * 16 + 15].X < 0.01f, $"last column of -Z is {minusZ[8 * 16 + 15]}");
    }

    [Fact]
    public void The_sun_is_taken_out_of_the_faces_and_kept_as_a_disc_with_its_light()
    {
        const int width = 512, height = 256;
        var background = new Vector3(0.2f, 0.3f, 0.4f);
        var sunColor = new Vector3(4000f, 3000f, 2000f);

        // A small bright patch at u = 0.75, v = 0.25: 45 degrees up, towards +X.
        var pixels = Equirect(width, height, _ => background);
        var sunPixels = new List<(int X, int Y)>();
        for (int y = 63; y <= 64; y++)
        {
            for (int x = 383; x <= 384; x++)
            {
                Set(pixels, width, x, y, sunColor);
                sunPixels.Add((x, y));
            }
        }

        var sky = SkyCooker.FromEquirect(pixels, width, height, faceSize: 32);

        var expected = Vector3.Normalize(new Vector3(1f, 1f, 0f));
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(sky.SunDirection, expected), -1f, 1f));
        Assert.True(angle < 0.01f, $"sun at {sky.SunDirection}, {angle} radians from {expected}");

        // The faces keep only the background, with no trace of the sun.
        foreach (var face in sky.Faces)
        {
            foreach (var texel in Texels(face.Mips[0]))
            {
                AssertNear(background, texel, 0.01f);
            }
        }

        // The disc gives back the light the faces lost: (sun - background) over the patch's solid angle.
        var lost = Vector3.Zero;
        foreach (var (_, y) in sunPixels)
        {
            float theta = (y + 0.5f) / height * MathF.PI;
            float solidAngle = 2f * MathF.PI / width * (MathF.PI / height) * MathF.Sin(theta);
            lost += (sunColor - background) * solidAngle;
        }

        float discSolidAngle = 2f * MathF.PI * (1f - sky.SunCosine);
        AssertNear(lost, sky.SunRadiance * discSolidAngle, lost.X * 0.01f);
    }

    [Fact]
    public void An_hdr_file_cooks_to_a_sky_that_reads_back()
    {
        var color = new Vector3(0.75f, 0.5f, 0.25f);
        byte[] hdr = TestHdr.Make(64, 32, color);
        var root = new MemoryRoot();
        root.Add("skies/flat.hdr", hdr);
        var store = new MemoryStore();

        var result = new CookPipeline(BuiltInCookers.All, new CookOptions()).Cook([root], store);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var entry = store.Manifest.Assets["skies/flat.hdr"];
        Assert.Equal(ContentKind.Data, entry.Kind);
        var sky = SkyImageData.Read(new MemoryStream(store.Get(entry.Hash)), "skies/flat.hdr");
        Assert.Equal(256, sky.Faces[0].Width);
        Assert.Equal(9, sky.Faces[0].Mips.Length);
        AssertNear(color, Centre(sky.Faces[3]), 0.01f);
        Assert.Equal(Vector3.Zero, sky.SunDirection);
    }

    [Fact]
    public void An_image_not_twice_as_wide_as_tall_is_an_error_naming_it()
    {
        var root = new MemoryRoot();
        root.Add("skies/square.hdr", TestHdr.Make(32, 32, Vector3.One));

        var result = new CookPipeline(BuiltInCookers.All, new CookOptions()).Cook([root], new MemoryStore());

        Assert.False(result.Succeeded);
        string message = Assert.Single(result.Problems, p => p.IsError).Message;
        Assert.Contains("skies/square.hdr", message);
        Assert.Contains("32x32", message);
        Assert.Contains("twice as wide as it is tall", message);
    }

    [Fact]
    public void A_broken_hdr_is_an_error_naming_it()
    {
        var root = new MemoryRoot();
        root.Add("skies/broken.hdr", Encoding.ASCII.GetBytes("not an image"));

        var result = new CookPipeline(BuiltInCookers.All, new CookOptions()).Cook([root], new MemoryStore());

        Assert.False(result.Succeeded);
        Assert.Contains("skies/broken.hdr", Assert.Single(result.Problems, p => p.IsError).Message);
    }

    private static float[] Equirect(int width, int height, Func<Vector3, Vector3> paint)
    {
        var pixels = new float[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            float theta = (y + 0.5f) / height * MathF.PI;
            for (int x = 0; x < width; x++)
            {
                float phi = ((x + 0.5f) / width - 0.5f) * 2f * MathF.PI;
                var direction = new Vector3(MathF.Sin(theta) * MathF.Sin(phi), MathF.Cos(theta), -MathF.Sin(theta) * MathF.Cos(phi));
                Set(pixels, width, x, y, paint(direction));
            }
        }

        return pixels;
    }

    private static void Set(float[] pixels, int width, int x, int y, Vector3 color)
    {
        int i = (y * width + x) * 3;
        pixels[i] = color.X;
        pixels[i + 1] = color.Y;
        pixels[i + 2] = color.Z;
    }

    private static IEnumerable<Vector3> Texels(byte[] mip)
    {
        for (int i = 0; i < mip.Length; i += 8)
        {
            yield return new Vector3(
                (float)BitConverter.ToHalf(mip, i),
                (float)BitConverter.ToHalf(mip, i + 2),
                (float)BitConverter.ToHalf(mip, i + 4));
        }
    }

    private static Vector3 Centre(TextureData face)
    {
        // The four texels round the middle, as an even face has no middle texel.
        var texels = Texels(face.Mips[0]).ToArray();
        int size = face.Width, half = size / 2;
        return (texels[(half - 1) * size + half - 1] + texels[(half - 1) * size + half]
            + texels[half * size + half - 1] + texels[half * size + half]) / 4f;
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");
    }
}
