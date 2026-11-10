using Rendition;
using Super.Cook.Models;

namespace Super.Cook.Tests;

/// <summary>
/// Which textures the model cooker keeps the alpha coverage of in their mips: only those of a material with an alpha
/// cutoff. Everything else keeps the mips the filter made, byte for byte.
/// </summary>
public sealed class AlphaCoverageTests : IDisposable
{
    private const int Size = 32;

    private static readonly float[] Uvs = [0f, 0f, 1f, 0f, 0f, 1f];

    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            Directory.Delete(folder, true);
        }
    }

    private string Temp()
    {
        string folder = Path.Combine(Path.GetTempPath(), "super-coverage-" + Guid.NewGuid().ToString("N"));
        _folders.Add(folder);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// A 32x32 web of white threads a pixel wide, every 8 pixels each way, on clear: 4 rows and 4 columns of 32 pixels,
    /// less the 16 where they cross, is 240 of 1024 pixels opaque.
    /// </summary>
    private static byte[] Threads()
    {
        var rgba = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int i = (y * Size + x) * 4;
                rgba[i] = rgba[i + 1] = rgba[i + 2] = 255;
                rgba[i + 3] = (byte)(x % 8 == 0 || y % 8 == 0 ? 255 : 0);
            }
        }

        return rgba;
    }

    private string Folder(string rule)
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "web", ["a"], uvs: Uvs);
        PngFile.Write(Path.Combine(src, "web.png"), Threads(), Size, Size);
        File.WriteAllText(Path.Combine(src, "cook.json"), "{ \"rules\": [ { \"match\": \"**/*.png\", \"cook\": false } ] }");
        Rule(src, rule);
        return src;
    }

    private static void Rule(string src, string rule, bool compress = false)
    {
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            $"{{ \"compressTextures\": {(compress ? "true" : "false")}, \"materials\": [ {{ \"match\": \"web\", \"baseColor\": \"web.png\"{rule} }} ] }}");
    }

    private static MemoryStore Cook(string src, string? cache = null)
    {
        var store = new MemoryStore();
        var result = new CookPipeline(ModelCookers.All, new CookOptions(CacheFolder: cache)).Cook([new FolderRoot(src)], store);
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        return store;
    }

    private static TextureData Colour(MemoryStore store)
    {
        var model = ModelData.Read(new MemoryStream(store.Get(store.Manifest.Assets["a.gltf"].Hash)), "a.gltf",
            hash => TextureData.Read(new MemoryStream(store.Get(hash)), hash));
        return model.Textures[model.Materials[Assert.Single(model.Parts).Material].BaseColorTexture];
    }

    [Theory]
    [InlineData("")]
    [InlineData(", \"transparent\": true")]
    [InlineData(", \"dithered\": true")]
    public void A_texture_of_a_material_without_a_cutoff_keeps_the_filtered_mips(string rule)
    {
        var store = Cook(Folder(rule));

        var expected = TextureData.FromPixels(Size, Size, TextureKind.Color, Threads());
        var colour = Colour(store);
        Assert.Equal(expected.Mips.Length, colour.Mips.Length);
        for (int level = 0; level < expected.Mips.Length; level++)
        {
            Assert.Equal(expected.Mips[level], colour.Mips[level]);
        }
    }

    [Fact]
    public void A_texture_of_a_material_with_a_cutoff_keeps_its_coverage_where_the_filtered_mips_lose_it()
    {
        var store = Cook(Folder(", \"alphaCutoff\": 0.5"));

        var filtered = TextureData.FromPixels(Size, Size, TextureKind.Color, Threads());
        var colour = Colour(store);

        // 240 of 1024 pixels pass at the top, 15 in 64. On the 8x8 level a thread runs through every other row and
        // column, and the filter spreads them all below a half. The 16 pixels where two cross are the most opaque, the
        // 32 on one thread next and the 16 between least, so the shares a scale can give are 0, 16, 48 and 64 in 64.
        // Kept, it is 16, the nearest to 15.
        Assert.Equal(240.0 / 1024, TextureData.AlphaCoverage(colour, 0, 0.5f));
        Assert.Equal(0.0, TextureData.AlphaCoverage(filtered, 2, 0.5f));
        Assert.Equal(16.0 / 64, TextureData.AlphaCoverage(colour, 2, 0.5f));
        Assert.Equal(filtered.Mips[0], colour.Mips[0]);
    }

    // The share of a level's pixels where the colour's alpha times the surface's reaches the cutoff, as the shader reads them.
    private static double ProductCoverage(TextureData colour, TextureData surface, int level, float cutoff)
    {
        byte[] a = colour.Mips[level];
        byte[] b = surface.Mips[level];
        Assert.Equal(a.Length, b.Length);
        int passing = 0;
        for (int i = 3; i < a.Length; i += 4)
        {
            if (a[i] / 255f * (b[i] / 255f) >= cutoff)
            {
                passing++;
            }
        }

        return (double)passing / (a.Length / 4);
    }

    [Fact]
    public void A_colour_and_surface_that_both_carry_alpha_keep_the_coverage_of_their_product()
    {
        string src = Folder("");
        // A surface whose alpha is 160 everywhere: alone it passes a cutoff of a half, but over the threads' filtered
        // alpha it takes the product under it unless the colour is scaled for the product.
        var surface = new byte[Size * Size * 4];
        for (int i = 0; i < surface.Length; i += 4)
        {
            surface[i] = 0;
            surface[i + 1] = 150;
            surface[i + 2] = 0;
            surface[i + 3] = 160;
        }

        PngFile.Write(Path.Combine(src, "surface.png"), surface, Size, Size);
        Rule(src, ", \"surface\": \"surface.png\", \"alphaCutoff\": 0.5");

        var store = Cook(src);
        var model = ModelData.Read(new MemoryStream(store.Get(store.Manifest.Assets["a.gltf"].Hash)), "a.gltf",
            hash => TextureData.Read(new MemoryStream(store.Get(hash)), hash));
        var material = model.Materials[Assert.Single(model.Parts).Material];
        var colour = model.Textures[material.BaseColorTexture];
        var cookedSurface = model.Textures[material.SurfaceTexture];

        // On the threads the product is 160 / 255, so 240 of 1024 pixels pass at the top. On the 8x8 level the shares a
        // scale can give are 0, 16, 48 and 64 in 64, as in the test above, and 16 is the nearest to 15. Keeping each
        // texture's own coverage would leave the threads' alpha just over a half there, under the cutoff once times
        // 160 / 255, and none would pass.
        Assert.Equal(240.0 / 1024, ProductCoverage(colour, cookedSurface, 0, 0.5f));
        Assert.Equal(16.0 / 64, ProductCoverage(colour, cookedSurface, 2, 0.5f));

        // Over a surface this even, the product passes where the colour's alpha alone reaches 0.5 * 255 / 160, so every
        // level keeps the share that the colour alone would keep at that cutoff.
        float alone = 0.5f * 255f / 160f;
        var expected = TextureData.KeepAlphaCoverage(TextureData.FromPixels(Size, Size, TextureKind.Color, Threads()), alone);
        Assert.Equal(expected.Mips.Length, colour.Mips.Length);
        for (int level = 1; level < expected.Mips.Length; level++)
        {
            Assert.Equal(TextureData.AlphaCoverage(expected, level, alone), ProductCoverage(colour, cookedSurface, level, 0.5f));
        }

        // The surface is left as the filter made it, and only the colour is scaled.
        var filteredSurface = TextureData.FromPixels(Size, Size, TextureKind.Linear, surface);
        for (int level = 0; level < filteredSurface.Mips.Length; level++)
        {
            Assert.Equal(filteredSurface.Mips[level], cookedSurface.Mips[level]);
        }
    }

    [Fact]
    public void Changing_the_cutoff_compresses_the_colour_texture_again()
    {
        string src = Folder("");
        string cache = Temp();
        Rule(src, ", \"alphaCutoff\": 0.5", compress: true);
        var before = Cook(src, cache);
        string steps = Path.Combine(cache, "steps", "compressed-texture");
        int kept = Directory.GetFiles(steps, "*", SearchOption.AllDirectories).Length;

        // The same cutoff again, written another way so the model cooks again, finds what it kept.
        Rule(src, ", \"alphaCutoff\": 0.50", compress: true);
        Cook(src, cache);
        Assert.Equal(kept, Directory.GetFiles(steps, "*", SearchOption.AllDirectories).Length);

        Rule(src, ", \"alphaCutoff\": 0.3", compress: true);
        var after = Cook(src, cache);

        Assert.Equal(kept + 1, Directory.GetFiles(steps, "*", SearchOption.AllDirectories).Length);
        Assert.NotEqual(Colour(before).Mips[2], Colour(after).Mips[2]);
    }
}
