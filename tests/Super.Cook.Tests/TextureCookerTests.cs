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
        Assert.Equal(TextureFormat.Rgba8, read.Format);
        Assert.Equal(TwoPixels, Assert.Single(read.Mips));
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
    public void With_compression_off_a_png_cooks_to_its_rgba_pixels_in_srgb()
    {
        var texture = CookPng("{ \"compressTextures\": false }");

        Assert.Equal(2, texture.Width);
        Assert.Equal(1, texture.Height);
        Assert.Equal(TextureKind.Color, texture.Kind);
        Assert.False(texture.NormalMap);
        Assert.Equal(TextureFormat.Rgba8, texture.Format);
        Assert.Equal(TwoPixels, Assert.Single(texture.Mips));
    }

    [Fact]
    public void A_png_cooks_to_bc7_with_every_mip()
    {
        var texture = CookPng(null);

        Assert.Equal((2, 1, TextureKind.Color, false), (texture.Width, texture.Height, texture.Kind, texture.NormalMap));
        Assert.Equal(TextureFormat.Bc7, texture.Format);

        // 2 pixels across, then 1: one block each.
        Assert.Equal([16, 16], texture.Mips.Select(mip => mip.Length));
    }

    [Fact]
    public void A_normal_map_cooks_to_bc5_and_a_linear_texture_to_bc7()
    {
        Assert.Equal(TextureFormat.Bc5, CookPng("{ \"normalMap\": true }").Format);
        Assert.Equal(TextureFormat.Bc7, CookPng("{ \"linear\": true }").Format);
    }

    [Fact]
    public void A_compressed_texture_decodes_close_to_its_pixels()
    {
        // Smooth ramps in every channel, 40 by 24, which isn't a whole number of blocks down the smaller levels.
        byte[] pixels = new byte[40 * 24 * 4];
        for (int y = 0; y < 24; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                int i = (y * 40 + x) * 4;
                pixels[i] = (byte)(x * 6);
                pixels[i + 1] = (byte)(y * 10);
                pixels[i + 2] = (byte)(255 - x * 3);
                pixels[i + 3] = 255;
            }
        }

        var root = new MemoryRoot();
        root.Add("ramp.png", Png(pixels, 40, 24));
        var (result, store) = Cook(root);
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var texture = CookedTexture.Read(store.Get(store.Manifest.Assets["ramp.png"].Hash), "ramp.png");

        // 40x24, 20x12, 10x6, 5x3, 2x1, 1x1.
        Assert.Equal(6, texture.Mips.Length);
        Assert.Equal(10 * 6 * 16, texture.Mips[0].Length);
        Assert.Equal(2 * 1 * 16, texture.Mips[3].Length);
        byte[] decoded = texture.DecodePixels();
        int[] errors = [.. pixels.Zip(decoded, (a, b) => Math.Abs(a - b))];
        Assert.True(errors.Average() < 3.0, $"the bytes are off by {errors.Average():0.00} on average.");
        Assert.True(errors.Max() <= 16, $"a byte is off by {errors.Max()}.");
    }

    [Fact]
    public void Cooking_a_texture_twice_gives_the_same_bytes()
    {
        var root = new MemoryRoot();
        root.Add("a.png", Png([.. Enumerable.Range(0, 8 * 8 * 4).Select(i => (byte)(i * 37))], 8, 8));

        var first = Cook(root).Store;
        var second = Cook(root).Store;

        Assert.Equal(first.Manifest.Assets["a.png"].Hash, second.Manifest.Assets["a.png"].Hash);
        Assert.Equal(TextureFormat.Bc7, CookedTexture.Read(first.Get(first.Manifest.Assets["a.png"].Hash), "a.png").Format);
    }

    [Fact]
    public void CookedTexture_round_trips_a_compressed_texture()
    {
        var mips = TextureData.FromPixels(8, 4, TextureKind.Color, [.. Enumerable.Range(0, 8 * 4 * 4).Select(i => (byte)i)]);
        var compressed = TextureCompression.Compress(mips, TextureFormat.Bc7);

        var read = CookedTexture.Read(CookedTexture.From(compressed, normalMap: false).Write(), "t.png");

        Assert.Equal((8, 4, TextureKind.Color, TextureFormat.Bc7), (read.Width, read.Height, read.Kind, read.Format));
        Assert.Equal(compressed.Mips, read.Mips);
        var data = read.ToTextureData();
        Assert.Same(read.Mips, data.Mips);
        Assert.Equal(TextureFormat.Bc7, data.Format);
    }

    [Fact]
    public void An_uncompressed_cooked_texture_gets_its_mips_built()
    {
        var data = new CookedTexture(2, 1, TextureKind.Color, false, TwoPixels).ToTextureData();

        Assert.Equal(TextureFormat.Rgba8, data.Format);
        Assert.Equal([8, 4], data.Mips.Select(mip => mip.Length));
        Assert.Equal(TwoPixels, data.Mips[0]);
    }

    [Fact]
    public void A_texture_cooked_before_mips_were_stored_must_be_cooked_again()
    {
        // Version 1: the header without a format or a count of levels, then the pixels.
        byte[] old = new byte[21 + TwoPixels.Length];
        BitConverter.GetBytes(0x58455453u).CopyTo(old, 0);
        BitConverter.GetBytes(1).CopyTo(old, 4);
        BitConverter.GetBytes(2).CopyTo(old, 8);
        BitConverter.GetBytes(1).CopyTo(old, 12);
        TwoPixels.CopyTo(old, 21);

        var exception = Assert.Throws<ContentException>(() => CookedTexture.ReadSize(old, "rock.png"));

        Assert.Equal("rock.png is not a current cooked texture. Cook again.", exception.Message);
    }

    [Fact]
    public void A_texture_marked_with_an_older_version_must_be_cooked_again()
    {
        byte[] bytes = new CookedTexture(2, 1, TextureKind.Color, false, TwoPixels).Write();
        BitConverter.GetBytes(1).CopyTo(bytes, 4);

        var exception = Assert.Throws<ContentException>(() => CookedTexture.Read(bytes, "rock.png"));

        Assert.Equal("rock.png is not a current cooked texture. Cook again.", exception.Message);
    }

    [Fact]
    public void A_compressed_texture_missing_a_level_must_be_cooked_again()
    {
        var mips = TextureData.FromPixels(8, 4, TextureKind.Color, new byte[8 * 4 * 4]);
        var compressed = TextureCompression.Compress(mips, TextureFormat.Bc7);
        var cut = new CookedTexture(8, 4, TextureKind.Color, false, TextureFormat.Bc7, compressed.Mips[..^1]);

        var exception = Assert.Throws<ContentException>(() => CookedTexture.Read(cut.Write(), "rock.png"));

        Assert.Equal("rock.png is not a current cooked texture. Cook again.", exception.Message);
    }

    [Fact]
    public void The_crafting_icons_cook_to_their_exact_pixels()
    {
        string content = CraftingContent();
        var store = new MemoryStore();
        var result = new CookPipeline(BuiltInCookers.All, new CookOptions()).Cook([new FolderRoot(content, ["icons/**"])], store);
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));

        string[] icons = [.. store.Manifest.Assets.Keys.Where(name => name.StartsWith("icons/")).Order()];
        Assert.Equal(Directory.GetFiles(Path.Combine(content, "icons"), "*.png").Length, icons.Length);
        Assert.NotEmpty(icons);
        foreach (string icon in icons)
        {
            var texture = CookedTexture.Read(store.Get(store.Manifest.Assets[icon].Hash), icon);
            var source = StbImageSharp.ImageResult.FromMemory(File.ReadAllBytes(Path.Combine(content, icon)), StbImageSharp.ColorComponents.RedGreenBlueAlpha);

            Assert.True(texture.Format == TextureFormat.Rgba8, $"{icon} cooked to {texture.Format}, not its exact pixels.");
            Assert.True(source.Data.AsSpan().SequenceEqual(texture.DecodePixels()), $"{icon}'s pixels changed in the cook.");
        }
    }

    private static string CraftingContent()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string content = Path.Combine(folder.FullName, "examples", "Crafting", "content");
            if (Directory.Exists(content))
            {
                return content;
            }
        }

        throw new DirectoryNotFoundException("examples/Crafting/content isn't in any folder above the tests.");
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
