using Rendition;

namespace Super.Tests;

public class TextureDataTests
{
    // A width x 1 linear texture whose pixels are grey at these values.
    private static TextureData Row(TextureKind kind, params byte[] values)
    {
        var rgba = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = values[i];
            rgba[i * 4 + 3] = 255;
        }

        return TextureData.FromPixels(values.Length, 1, kind, rgba);
    }

    [Theory]
    [InlineData(TextureFormat.Rgba8, 5, 3, 60)]
    [InlineData(TextureFormat.Bc7, 5, 3, 32)]
    [InlineData(TextureFormat.Bc5, 1, 1, 16)]
    [InlineData(TextureFormat.Bc4, 5, 3, 16)]
    [InlineData(TextureFormat.Bc4, 8, 8, 32)]
    public void Level_size_counts_whole_blocks_of_four_by_four_pixels(TextureFormat format, int width, int height, int bytes)
    {
        Assert.Equal(bytes, TextureData.LevelSize(format, width, height));
    }

    [Fact]
    public void Smaller_levels_keep_fine_stripes_sharper_than_an_average_of_four_would()
    {
        // Stripes four pixels dark and four light, a pixel off from the 2x2 grid of the next level, so half its pixels
        // straddle an edge. An average can't go beyond 64 to 192; the sharper filter overshoots both.
        var values = new byte[32];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (byte)((i + 1) % 8 < 4 ? 64 : 192);
        }

        var texture = Row(TextureKind.Linear, values);

        byte[] level = texture.Mips[1];
        var reds = Enumerable.Range(0, 16).Select(i => level[i * 4]).ToArray();
        Assert.True(reds.Min() < 60, $"darkest was {reds.Min()}");
        Assert.True(reds.Max() > 196, $"lightest was {reds.Max()}");
    }

    [Fact]
    public void Roughness_is_left_as_it_is_where_the_normals_agree()
    {
        Assert.Equal(0.5f, TextureData.WidenedRoughness(0.5f, 1f));
    }

    [Fact]
    public void Roughness_widens_with_the_spread_of_the_normals_under_it()
    {
        // Normals averaging to 0.9 long: kappa = (3 * 0.9 - 0.9^3) / (1 - 0.9^2) = 1.971 / 0.19 = 10.374.
        // GGX alpha 0.25 for roughness 0.5, so alpha^2 = 0.0625, plus 2 / kappa = 0.1928, is 0.2553.
        // Its square root is the new alpha, 0.5053, and roughness is the root of that, 0.7108.
        Assert.Equal(0.7108f, TextureData.WidenedRoughness(0.5f, 0.9f), 0.001f);
    }

    [Fact]
    public void Roughness_widens_on_smaller_levels_of_a_bumpy_normal_map_but_not_the_full_size_one()
    {
        // A 2x2 checker of normals leaning 45 degrees left and right, which average to (0, 0, 0.707) one level down.
        byte[] right = [218, 128, 218, 255];
        byte[] left = [37, 128, 218, 255];
        var normal = TextureData.FromPixels(2, 2, TextureKind.Linear, [.. right, .. left, .. left, .. right], normalMap: true);
        byte[] rough = [0, 128, 0, 255];
        var surface = TextureData.FromPixels(2, 2, TextureKind.Linear, [.. rough, .. rough, .. rough, .. rough]);

        var widened = TextureData.WidenRoughness(surface, normal);

        Assert.Equal(surface.Mips[0], widened.Mips[0]);
        // Length 0.707: kappa = (2.121 - 0.354) / 0.5 = 3.536 and 2 / kappa = 0.566. Roughness 128/255 = 0.502 is alpha
        // 0.252, whose square is 0.0635. sqrt(0.0635 + 0.566) = 0.793, and sqrt(0.793) = 0.891, which is 227 of 255.
        Assert.InRange(widened.Mips[1][1], 225, 229);
    }

    // A 128x128 cut-out of white threads a pixel wide on clear: a wave across the middle and a ring, like a cobweb's.
    private static byte[] Threads()
    {
        const int size = 128;
        var rgba = new byte[size * size * 4];
        void Opaque(int x, int y)
        {
            int i = (((y % size + size) % size) * size + (x % size + size) % size) * 4;
            rgba[i] = rgba[i + 1] = rgba[i + 2] = rgba[i + 3] = 255;
        }

        for (int x = 0; x < size; x++)
        {
            Opaque(x, 64 + (int)MathF.Round(20f * MathF.Sin(x * MathF.Tau / size)));
        }

        for (int step = 0; step < 720; step++)
        {
            float angle = step * MathF.Tau / 720;
            Opaque(40 + (int)MathF.Round(25f * MathF.Cos(angle)), 40 + (int)MathF.Round(25f * MathF.Sin(angle)));
        }

        return rgba;
    }

    [Fact]
    public void Thin_threads_keep_their_share_of_pixels_past_the_cutoff_at_every_level()
    {
        var filtered = TextureData.FromPixels(128, 128, TextureKind.Color, Threads());

        var kept = TextureData.KeepAlphaCoverage(filtered, 0.5f);

        double top = TextureData.AlphaCoverage(filtered, 0, 0.5f);
        Assert.InRange(top, 0.01, 0.03);
        for (int level = 1; level < kept.Mips.Length; level++)
        {
            // A level's share moves in whole pixels, so it can be off by half of one, or 1% where pixels are many.
            int pixels = Math.Max(128 >> level, 1) * Math.Max(128 >> level, 1);
            double share = TextureData.AlphaCoverage(kept, level, 0.5f);
            Assert.True(Math.Abs(share - top) <= Math.Max(0.01, 0.5 / pixels),
                $"level {level} keeps {share:F4} of its pixels, against {top:F4} at the top");
        }

        // Filtering alone thins the threads away: by the 16x16 level fewer than half as many pixels pass.
        Assert.True(TextureData.AlphaCoverage(filtered, 3, 0.5f) < top / 2, $"filtered kept {TextureData.AlphaCoverage(filtered, 3, 0.5f):F4}");
    }

    [Fact]
    public void Keeping_coverage_changes_only_the_alpha_of_the_smaller_levels()
    {
        var filtered = TextureData.FromPixels(128, 128, TextureKind.Color, Threads());

        var kept = TextureData.KeepAlphaCoverage(filtered, 0.5f);

        Assert.Equal(filtered.Mips[0], kept.Mips[0]);
        Assert.NotEqual(filtered.Mips[3], kept.Mips[3]);
        for (int level = 1; level < kept.Mips.Length; level++)
        {
            for (int i = 0; i < kept.Mips[level].Length; i++)
            {
                if (i % 4 != 3)
                {
                    Assert.Equal(filtered.Mips[level][i], kept.Mips[level][i]);
                }
            }
        }
    }

    [Fact]
    public void A_compressed_texture_cant_be_read_for_alpha()
    {
        var texture = new TextureData
        {
            Width = 4, Height = 4, Kind = TextureKind.Color, Format = TextureFormat.Bc7, Mips = [new byte[16]],
        };

        var error = Assert.Throws<InvalidOperationException>(() => texture.AlphaRange());
        Assert.Equal("Can't read the alpha of a Bc7 texture, only of an RGBA8 one.", error.Message);
    }

    [Fact]
    public void A_models_textures_keep_their_format_through_its_file()
    {
        var model = new ModelData
        {
            Vertices = [],
            Indices = [],
            Parts = [],
            Materials = [],
            Textures =
            [
                new TextureData { Width = 4, Height = 4, Kind = TextureKind.Linear, Format = TextureFormat.Bc5, Mips = [new byte[16]] },
                new TextureData { Width = 4, Height = 4, Kind = TextureKind.Linear, Format = TextureFormat.Bc4, Mips = [new byte[8]] },
            ],
        };

        var read = ModelData.Read(new MemoryStream(model.ToBytes()), "a.model");

        Assert.Equal([TextureFormat.Bc5, TextureFormat.Bc4], read.Textures.Select(t => t.Format));
    }
}
