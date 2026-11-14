using Super.Materials;

namespace Courtyard.Tests;

/// <summary>
/// The Courtyard's flagstones: each stone is a cell of an 8 by 8 grid, a third of them cracked, one in eight a warm
/// sandstone among close greys, and their crowns worn smoother than the rest.
/// </summary>
[Trait("Speed", "Slow")]
public sealed class FlagstoneTests
{
    private const int Size = 512;

    private const int Cell = Size / Flagstones.Stones;

    // Pixels this far inside a cell are well clear of the joint, the chamfer and the chips.
    private const int Margin = 8;

    private sealed class CrackMap : MaterialGraph
    {
        public override Material Build()
        {
            return new Material { Height = Flagstones.Cracks() };
        }
    }

    private static readonly Lazy<MaterialImages> Cooked = new(() => GraphRunner.Run(new Flagstones(), Size));

    // The cracks are a few millimetres wide, under a pixel at 512, so they are looked at four times as fine.
    private const int CrackSize = 2048;

    /// <summary>Every pixel of stone (row, column) at least <paramref name="margin"/> inside its cell, at <paramref name="size"/>.</summary>
    private static IEnumerable<int> InsideOf(int row, int column, int margin = Margin, int size = Size)
    {
        var cell = size / Flagstones.Stones;

        for (var y = row * cell + margin; y < (row + 1) * cell - margin; y++)
        {
            for (var x = column * cell + margin; x < (column + 1) * cell - margin; x++)
            {
                yield return y * size + x;
            }
        }
    }

    /// <summary>The middle of stone (row, column), 9 by 9 pixels.</summary>
    private static IEnumerable<int> MiddleOf(int row, int column)
    {
        return InsideOf(row, column, Cell / 2 - 4);
    }

    private static IEnumerable<(int Row, int Column)> AllStones()
    {
        for (var row = 0; row < Flagstones.Stones; row++)
        {
            for (var column = 0; column < Flagstones.Stones; column++)
            {
                yield return (row, column);
            }
        }
    }

    [Fact]
    public void About_a_third_of_the_stones_have_a_crack()
    {
        var cracks = GraphRunner.Run(new CrackMap(), CrackSize).Height;
        var cracked = AllStones().Count(stone => InsideOf(stone.Row, stone.Column, 4 * Margin, CrackSize).Any(i => cracks[i] > 0.5f));

        // 64 stones, a third of them is 21.
        Assert.True(cracked is >= 18 and <= 25, $"{cracked} of the 64 stones have a crack.");
    }

    [Fact]
    public void A_crack_runs_across_its_stone_rather_than_stopping_in_the_middle()
    {
        var cracks = GraphRunner.Run(new CrackMap(), CrackSize).Height;

        // The middle three quarters of a stone is 192 pixels across here. A crack through a point near the stone's
        // middle crosses most of it, a pixel or two wide, so it covers well over 100 pixels even where it cuts a
        // corner. Specks or a stub cover fewer.
        foreach (var (row, column) in AllStones())
        {
            var on = InsideOf(row, column, 4 * Margin, CrackSize).Count(i => cracks[i] > 0.5f);

            Assert.True(on == 0 || on >= 100, $"The stone at ({row}, {column}) has only {on} pixels of crack, a fragment.");
        }
    }

    [Fact]
    public void The_flagstones_are_darker_along_their_cracks()
    {
        // At 1024 a crack is about a pixel and a half wide, enough to look at in the full material.
        const int size = 1024;
        var cracks = GraphRunner.Run(new CrackMap(), size).Height;
        var color = GraphRunner.Run(new Flagstones(), size).Color;

        float Light(int i) => 0.2126f * color[i * 3] + 0.7152f * color[i * 3 + 1] + 0.0722f * color[i * 3 + 2];

        var inside = AllStones().SelectMany(s => InsideOf(s.Row, s.Column, 2 * Margin, size)).ToList();
        var onCrack = inside.Where(i => cracks[i] > 0.5f).ToList();
        var offCrack = inside.Where(i => cracks[i] < 0.01f).ToList();

        Assert.NotEmpty(onCrack);
        Assert.NotEmpty(offCrack);

        var on = onCrack.Average(Light);
        var off = offCrack.Average(Light);

        // The cracks darken the stone to about half; the mottle and flecks alone change it far less.
        Assert.True(on < 0.8f * off, $"Along the cracks the stone is {on:0.000}, elsewhere {off:0.000}.");
    }

    [Fact]
    public void One_stone_in_eight_is_the_warm_sandstone()
    {
        var color = Cooked.Value.Color;
        var warm = 0;

        foreach (var (row, column) in AllStones())
        {
            var middle = MiddleOf(row, column).ToList();
            var red = middle.Average(i => color[i * 3]);
            var blue = middle.Average(i => color[i * 3 + 2]);

            // The greys are within 6% red over blue, the sandstone about 14%.
            if (red - blue > 0.09f)
            {
                warm++;
            }
        }

        // 64 stones, one in eight is 8.
        Assert.True(warm is >= 6 and <= 10, $"{warm} of the 64 stones are warm.");
    }

    [Fact]
    public void The_grey_stones_differ_from_each_other_by_only_a_small_step()
    {
        var color = Cooked.Value.Color;
        var tones = Flagstones.ToneTable();
        var lights = new List<float>();

        foreach (var (row, column) in AllStones())
        {
            if (tones[row, column] == Flagstones.Sandstone)
            {
                continue;
            }

            lights.Add((float)InsideOf(row, column).Average(i => 0.2126f * color[i * 3] + 0.7152f * color[i * 3 + 1] + 0.0722f * color[i * 3 + 2]));
        }

        var mean = lights.Average();

        // No grey stone is more than an eighth lighter or darker than the average grey, so the path reads as one
        // stone, not a checkerboard.
        Assert.All(lights, light => Assert.True(MathF.Abs(light - mean) / mean < 0.125f, $"A grey stone is {light:0.000}, the greys average {mean:0.000}."));

        // And they do differ: the lightest and the darkest are at least 4% apart.
        Assert.True((lights.Max() - lights.Min()) / mean > 0.04f, $"The greys run only from {lights.Min():0.000} to {lights.Max():0.000}.");
    }

    [Fact]
    public void The_crowns_are_polished_smoother_than_the_rest_of_the_stone()
    {
        var roughness = Cooked.Value.Roughness;
        var crowns = AllStones().SelectMany(s => MiddleOf(s.Row, s.Column)).Average(i => roughness[i]);

        // A band 8 to 12 pixels in from the joint: past the chamfer and the chips, short of the crown.
        var rims = AllStones()
            .SelectMany(s => InsideOf(s.Row, s.Column, 8).Except(InsideOf(s.Row, s.Column, 12)))
            .Average(i => roughness[i]);

        // Polished to 0.5 where feet go, 0.7 to 0.9 elsewhere.
        Assert.True(crowns < 0.65f, $"The crowns average a roughness of {crowns:0.000}.");
        Assert.True(rims > 0.7f, $"The stone near the edges averages a roughness of {rims:0.000}.");
    }
}
