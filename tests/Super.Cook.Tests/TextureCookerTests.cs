using System.Text;
using Rendition;

namespace Super.Cook.Tests;

public sealed class TextureCookerTests
{
    private static readonly byte[] TwoPixels = [255, 0, 0, 255, 0, 0, 255, 128];

    private static byte[] Png(byte[] rgba, int width, int height)
    {
        string path = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            PngFile.Write(path, rgba, width, height);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (CookResult Result, MemoryStore Store) Cook(MemoryRoot root)
    {
        var store = new MemoryStore();
        var result = new CookPipeline(BuiltInCookers.All, new CookOptions()).Cook([root], store);
        return (result, store);
    }

    private static CookedTexture CookPng(string? settings)
    {
        var root = new MemoryRoot();
        root.Add("a.png", Png(TwoPixels, 2, 1));
        if (settings is not null)
        {
            root["a.png.cook.json"] = settings;
        }

        var (result, store) = Cook(root);
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var entry = store.Manifest.Assets["a.png"];
        Assert.Equal(ContentKind.Texture, entry.Kind);
        return CookedTexture.Read(store.Get(entry.Hash), "a.png");
    }

    [Fact]
    public void CookedTexture_round_trips()
    {
        var texture = new CookedTexture(2, 1, TextureKind.Linear, true, TwoPixels);

        var read = CookedTexture.Read(texture.Write(), "t.png");

        Assert.Equal(2, read.Width);
        Assert.Equal(1, read.Height);
        Assert.Equal(TextureKind.Linear, read.Kind);
        Assert.True(read.NormalMap);
        Assert.Equal(TwoPixels, read.Pixels);
        Assert.Equal((2, 1), CookedTexture.ReadSize(texture.Write(), "t.png"));
    }

    [Fact]
    public void CookedTexture_rejects_a_short_file_naming_it()
    {
        byte[] bytes = new CookedTexture(2, 1, TextureKind.Color, false, TwoPixels).Write();

        var exception = Assert.Throws<ContentException>(() => CookedTexture.Read(bytes[..^1], "rock.png"));

        Assert.Equal("rock.png is not a current cooked texture. Cook again.", exception.Message);
    }

    [Fact]
    public void Png_cooks_to_rgba_pixels_in_srgb()
    {
        var texture = CookPng(null);

        Assert.Equal(2, texture.Width);
        Assert.Equal(1, texture.Height);
        Assert.Equal(TextureKind.Color, texture.Kind);
        Assert.False(texture.NormalMap);
        Assert.Equal(TwoPixels, texture.Pixels);
    }

    [Fact]
    public void Linear_setting_gives_linear()
    {
        var texture = CookPng("{ \"linear\": true }");

        Assert.Equal(TextureKind.Linear, texture.Kind);
        Assert.False(texture.NormalMap);
    }

    [Fact]
    public void Normal_map_is_linear_even_without_linear()
    {
        var texture = CookPng("{ \"normalMap\": true }");

        Assert.Equal(TextureKind.Linear, texture.Kind);
        Assert.True(texture.NormalMap);
    }

    [Fact]
    public void Unknown_texture_setting_is_an_error()
    {
        var root = new MemoryRoot();
        root.Add("a.png", Png(TwoPixels, 2, 1));
        root["a.png.cook.json"] = "{ \"mipmap\": false }";

        var (result, _) = Cook(root);

        Assert.False(result.Succeeded);
        Assert.Contains("mipmap", Assert.Single(result.Problems, p => p.IsError).Message);
    }

    [Fact]
    public void Garbage_png_is_an_error_naming_it()
    {
        var root = new MemoryRoot();
        root.Add("bad.png", Encoding.UTF8.GetBytes("this is not an image"));

        var (result, _) = Cook(root);

        Assert.False(result.Succeeded);
        Assert.Equal("bad.png is not an image this can read", Assert.Single(result.Problems, p => p.IsError).Message);
    }
}
