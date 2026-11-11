using Super.Materials;

namespace Courtyard.Tests;

/// <summary>The Courtyard's bricks take one of eight tones each, dealt so that no two bricks that touch match.</summary>
public sealed class BrickToneTests
{
    private const int Size = 512;

    // A joint is 0.075 of a 42.7 pixel course, about 3 pixels, so 8 steps reach across one but never down one.
    private const int MostJoint = 8;

    /// <summary>The tone of every pixel, and where the bricks are, laid as the real graph lays them.</summary>
    private sealed class ToneMap : MaterialGraph
    {
        public override Material Build()
        {
            return new Material
            {
                Height = Bricks.ToneImage(),
                Roughness = Tiles.Bricks(columns: Bricks.Columns, rows: Bricks.Rows, gap: 0.075f, bevel: 0f),
            };
        }
    }

    private static int ToneAt(float[] tones, int pixel) => (int)MathF.Round(tones[pixel] * (Bricks.Tones.Length - 1));

    /// <summary>The pixel at the middle of brick (row, column): each course is shifted half a brick along from the one above.</summary>
    private static int CentreOf(int row, int column)
    {
        var u = (column + 0.5f + 0.5f * row) / Bricks.Columns;
        var x = (int)((u - MathF.Floor(u)) * Size);
        var y = (int)((row + 0.5f) / Bricks.Rows * Size);

        return y * Size + x;
    }

    [Fact]
    public void No_two_bricks_that_touch_share_a_tone()
    {
        var images = GraphRunner.Run(new ToneMap(), Size);
        var tones = images.Height;
        var brick = images.Roughness;
        var pairs = 0;

        // From every brick pixel, look right and down across the joint: the first brick pixel past mortar is a
        // neighbouring brick. The image wraps, so the bricks across the tile's edges are checked too.
        foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
        {
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    if (brick[y * Size + x] <= 0f)
                    {
                        continue;
                    }

                    var next = ((y + dy) % Size) * Size + (x + dx) % Size;

                    if (brick[next] > 0f)
                    {
                        continue;
                    }

                    var steps = 1;
                    var across = next;

                    while (brick[across] <= 0f)
                    {
                        steps++;
                        across = ((y + dy * steps) % Size) * Size + (x + dx * steps) % Size;
                    }

                    // A long way through mortar ran down a joint, not across one, so the brick it reached doesn't touch.
                    if (steps > MostJoint)
                    {
                        continue;
                    }

                    pairs++;
                    var here = ToneAt(tones, y * Size + x);
                    var there = ToneAt(tones, across);

                    Assert.True(here != there, $"The brick at pixel ({x}, {y}) and its neighbour {steps} pixels {(dx == 1 ? "right" : "down")} are both tone {here}.");
                }
            }
        }

        // Every brick has joints right and below, so the walk must have crossed a great many of them.
        Assert.True(pairs > Bricks.Columns * Bricks.Rows * 10, $"Only {pairs} joints were crossed.");
    }

    [Fact]
    public void A_third_of_the_bricks_are_the_two_middle_reds()
    {
        var tones = GraphRunner.Run(new ToneMap(), Size).Height;
        var reds = 0;

        for (var row = 0; row < Bricks.Rows; row++)
        {
            for (var column = 0; column < Bricks.Columns; column++)
            {
                if (Bricks.MiddleReds.Contains(ToneAt(tones, CentreOf(row, column))))
                {
                    reds++;
                }
            }
        }

        // 96 bricks, a third of them is 32.
        Assert.Equal(32, reds);
    }

    [Fact]
    public void The_dark_plum_and_the_pale_buff_are_rare_accents()
    {
        var tones = GraphRunner.Run(new ToneMap(), Size).Height;
        var plums = 0;
        var buffs = 0;

        for (var row = 0; row < Bricks.Rows; row++)
        {
            for (var column = 0; column < Bricks.Columns; column++)
            {
                var tone = ToneAt(tones, CentreOf(row, column));

                if (tone == 0)
                {
                    plums++;
                }

                if (tone == Bricks.Tones.Length - 1)
                {
                    buffs++;
                }
            }
        }

        // Both show up, but neither on more than one brick in sixteen, 6 of the 96.
        Assert.InRange(plums, 1, 6);
        Assert.InRange(buffs, 1, 6);
    }

    /// <summary>Only the efflorescence patches, before they are cut down to rims along the joints of soft bricks.</summary>
    private sealed class PatchMap : MaterialGraph
    {
        public override Material Build()
        {
            return new Material { Height = Bricks.EfflorescencePatches() };
        }
    }

    [Fact]
    public void The_salt_patches_cover_about_a_fifth_of_the_wall()
    {
        var patches = GraphRunner.Run(new PatchMap(), Size).Height;
        var covered = patches.Count(value => value > 0.5f) / (float)patches.Length;

        Assert.True(covered is >= 0.18f and <= 0.26f, $"The salt patches cover {covered:P1} of the wall.");
    }

    [Fact]
    public void The_cooked_colour_follows_the_tones_from_plum_to_buff()
    {
        var tones = GraphRunner.Run(new ToneMap(), Size).Height;
        var color = GraphRunner.Run(new Bricks(), Size).Color;
        var points = new List<(float Tone, float Light)>();

        for (var row = 0; row < Bricks.Rows; row++)
        {
            for (var column = 0; column < Bricks.Columns; column++)
            {
                var centre = CentreOf(row, column);
                var light = 0f;

                // The middle of the brick, 9 by 9 pixels, so the mottling evens out.
                for (var dy = -4; dy <= 4; dy++)
                {
                    for (var dx = -4; dx <= 4; dx++)
                    {
                        var at = (centre + dy * Size + dx + Size * Size) % (Size * Size);

                        light += 0.2126f * color[at * 3] + 0.7152f * color[at * 3 + 1] + 0.0722f * color[at * 3 + 2];
                    }
                }

                points.Add((ToneAt(tones, centre), light / 81f));
            }
        }

        // The tones run from dark plum to pale buff, so a brick's lightness rises with its tone, through the
        // mottling, the salts and the soot.
        var meanTone = points.Average(p => p.Tone);
        var meanLight = points.Average(p => p.Light);
        var covariance = points.Sum(p => (p.Tone - meanTone) * (p.Light - meanLight));
        var spreadTone = MathF.Sqrt(points.Sum(p => (p.Tone - meanTone) * (p.Tone - meanTone)));
        var spreadLight = MathF.Sqrt(points.Sum(p => (p.Light - meanLight) * (p.Light - meanLight)));
        var correlation = covariance / (spreadTone * spreadLight);

        Assert.True(correlation > 0.9f, $"A brick's lightness follows its tone with a correlation of only {correlation:0.000}.");
    }
}
