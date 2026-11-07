using System.Globalization;
using System.Numerics;

namespace Super.Materials.Tests;

public class TransformTests
{
    const int Size = 64;

    // A different value in every pixel, so any move shows.
    static float[] Ramp()
    {
        var values = new float[Size * Size];

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i / (float)values.Length;
        }

        return values;
    }

    public static TheoryData<string, Func<Image>> Transforms => new()
    {
        { "Offset", () => Noise.Perlin(4, octaves: 3).Transform(new Vector2(0.3f, 0.7f)) },
        { "Rotate 30", () => Noise.Perlin(4, octaves: 3).Transform(rotate: 30f) },
        { "Rotate 45", () => Noise.Perlin(4, octaves: 3).Transform(rotate: 45f) },
        { "Tile 3", () => Noise.Perlin(2, octaves: 3).Transform(tile: 3) },
        { "Scale a half", () => Noise.Perlin(2, octaves: 3).Transform(scale: 0.5f) },
    };

#pragma warning disable xUnit1026
    [Theory]
    [MemberData(nameof(Transforms))]
    public void Every_transform_tiles(string name, Func<Image> build) => NodeChecks.Tiles(build);

    [Theory]
    [MemberData(nameof(Transforms))]
    public void Every_transform_looks_the_same_at_two_sizes(string name, Func<Image> build) => NodeChecks.SameAtSizes(build);
#pragma warning restore xUnit1026

    [Fact]
    public void Transform_tile_two_repeats_the_image()
    {
        var p = GraphRunner.Evaluate(() => Noise.Perlin(4, octaves: 3).Transform(tile: 2), Size);

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size / 2; x++)
            {
                Assert.Equal(p[y * Size + x], p[y * Size + x + Size / 2], 5);
                Assert.Equal(p[y * Size + x], p[(y + Size / 2) % Size * Size + x], 5);
            }
        }
    }

    [Fact]
    public void Tile_two_shrinks_the_image_to_half()
    {
        // Output pixel (0, 0) reads the source at uv (1/64, 1/64), between source pixels 0 and 1 on both axes.
        var source = Ramp();
        var p = GraphRunner.Evaluate(() => Gray.FromArray(source).Transform(tile: 2), Size);
        var expected = (source[0] + source[1] + source[Size] + source[Size + 1]) / 4f;
        Assert.Equal(expected, p[0], 5);
    }

    [Fact]
    public void Scale_a_half_is_the_same_as_tile_two()
    {
        var half = GraphRunner.Evaluate(() => Noise.Perlin(4).Transform(scale: 0.5f), Size);
        var tiled = GraphRunner.Evaluate(() => Noise.Perlin(4).Transform(tile: 2), Size);
        Assert.Equal(tiled, half);
    }

    [Fact]
    public void Transform_by_a_whole_image_changes_nothing()
    {
        var source = GraphRunner.Evaluate(() => Noise.Perlin(4, octaves: 3), Size);
        var moved = GraphRunner.Evaluate(() => Noise.Perlin(4, octaves: 3).Transform(new Vector2(1f, 0f)), Size);

        for (var i = 0; i < source.Length; i++)
        {
            Assert.Equal(source[i], moved[i], 5);
        }
    }

    [Fact]
    public void An_offset_of_a_quarter_moves_the_image_a_quarter_to_the_right()
    {
        var source = Ramp();
        var p = GraphRunner.Evaluate(() => Gray.FromArray(source).Transform(new Vector2(0.25f, 0f)), Size);

        Assert.Equal(source[5 * Size + 0], p[5 * Size + 16], 5);
        Assert.Equal(source[5 * Size + 60], p[5 * Size + 12], 5);
    }

    [Fact]
    public void Rotating_90_degrees_turns_the_image_counter_clockwise()
    {
        // Counter-clockwise on screen: the source's bottom row ends up as the right column, so output (x, y) reads
        // source column 63 - y in row x.
        var source = Ramp();
        var p = GraphRunner.Evaluate(() => Gray.FromArray(source).Transform(rotate: 90f), Size);

        Assert.Equal(source[63 * Size + 58], p[5 * Size + 63], 5);
        Assert.Equal(source[10 * Size + 63 - 20], p[20 * Size + 10], 5);
    }

    [Fact]
    public void Rotation_snaps_like_directional_noise()
    {
        var thirty = GraphRunner.Evaluate(() => Noise.Perlin(4).Transform(rotate: 30f), Size);
        var snapped = GraphRunner.Evaluate(() => Noise.Perlin(4).Transform(rotate: MathF.Atan(0.5f) * 180f / MathF.PI), Size);
        Assert.Equal(snapped, thirty);
        Assert.NotEqual(GraphRunner.Evaluate(() => Noise.Perlin(4), Size), thirty);
    }

    [Fact]
    public void Rgb_transform_moves_every_channel()
    {
        var p = GraphRunner.Evaluate(() => Noise.Perlin(4).GradientMap(Materials.Ramp.Of("#000000", "#ff8040")).Transform(tile: 2), Size);

        for (var c = 0; c < 3; c++)
        {
            Assert.Equal(p[(5 * Size + 3) * 3 + c], p[(5 * Size + 3 + Size / 2) * 3 + c], 5);
        }

        Assert.NotEqual(p[(5 * Size + 3) * 3], p[(5 * Size + 3) * 3 + 1]);
    }

    [Fact]
    public void A_scale_that_would_not_tile_says_why()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");

        try
        {
            var error = Assert.Throws<ArgumentException>(() => GraphRunner.Evaluate(() => Noise.Perlin(4).Transform(scale: 0.4f), 8));
            Assert.Equal("tile / scale must be a whole number so the image tiles, not 1 / 0.4 = 2.5. Use a scale of 1, 0.5, 0.25 and so on, or one that divides tile.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void More_than_4096_copies_across_says_why()
    {
        var tile = Assert.Throws<ArgumentException>(() => GraphRunner.Evaluate(() => Noise.Perlin(4).Transform(tile: 5000), 8));
        Assert.Equal("tile / scale must be at most 4096 copies across, not 5000 / 1 = 5000.", tile.Message);

        var scale = Assert.Throws<ArgumentException>(() => GraphRunner.Evaluate(() => Noise.Perlin(4).Transform(scale: 1e-30f), 8));
        Assert.StartsWith("tile / scale must be at most 4096 copies across, not 1 / ", scale.Message);

        GraphRunner.Evaluate(() => Noise.Perlin(4).Transform(tile: 4096), 8);
    }

    [Fact]
    public void A_45_degree_turn_repeats_along_the_diagonals_and_not_across()
    {
        // At 45° the image shrinks by √2 and its copies sit half an image apart on the diagonals: moving half an
        // image right and half down reads one whole step further in the source, but moving only right reads half a step.
        var source = Ramp();
        var p = GraphRunner.Evaluate(() => Gray.FromArray(source).Transform(rotate: 45f), Size);
        var half = Size / 2;
        var across = 0;

        for (var y = 0; y < half; y++)
        {
            for (var x = 0; x < half; x++)
            {
                Assert.Equal(p[y * Size + x], p[(y + half) * Size + x + half], 4);

                if (MathF.Abs(p[y * Size + x] - p[y * Size + x + half]) > 1e-3f)
                {
                    across++;
                }
            }
        }

        Assert.True(across > half * half / 2, $"Only {across} of {half * half} pixels differ half an image across.");
    }

    [Fact]
    public void A_scale_that_divides_tile_is_fine()
    {
        var p = GraphRunner.Evaluate(() => Noise.Perlin(4).Transform(scale: 2f, tile: 2), Size);
        Assert.Equal(GraphRunner.Evaluate(() => Noise.Perlin(4), Size), p);
    }

    [Fact]
    public void Scale_and_tile_name_their_ranges()
    {
        var scale = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Noise.Perlin(4).Transform(scale: 0f), 8));
        Assert.StartsWith("scale must be more than 0, not 0.", scale.Message);

        var tile = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Noise.Perlin(4).Transform(tile: 0), 8));
        Assert.StartsWith("tile must be at least 1, not 0.", tile.Message);
    }
}
