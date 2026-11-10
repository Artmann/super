using Super.Materials;

namespace Courtyard.Tests;

/// <summary>Grass blade cards: a row of tapering blades cut out by their opacity, in three species.</summary>
public sealed class GrassBladeTests
{
    private const int Size = 512;

    private static readonly Lazy<MaterialImages> Meadow = new(() => GraphRunner.Run(new GrassBlade(), Size));
    private static readonly Lazy<MaterialImages> Broad = new(() => GraphRunner.Run(new GrassBlade { Species = "broad" }, Size));
    private static readonly Lazy<MaterialImages> Dry = new(() => GraphRunner.Run(new GrassBlade { Species = "dry" }, Size));

    [Fact]
    public void Blades_are_opaque_on_their_rib_and_clear_between_them_and_above_their_tips()
    {
        var meadow = Meadow.Value;

        // 24 columns of about 21 pixels. A third of the way up every blade is standing, and its rib, the column's
        // middle give or take the lean, is solid, while the column's edges, outside the blade's 0.3 half width, are
        // clear.
        int row = Size * 2 / 3;
        for (int column = 0; column < 24; column++)
        {
            float left = column * Size / 24f;
            Assert.True(Opacity(meadow, left + Size / 48f, row) > 0.99f, $"Column {column} is not solid on its rib.");
            Assert.True(Opacity(meadow, left + 1f, row) < 0.01f, $"Column {column} is not clear at its left edge.");
            Assert.True(Opacity(meadow, left + Size / 24f - 1f, row) < 0.01f, $"Column {column} is not clear at its right edge.");
        }

        // The longest blade reaches 97% of the way up, so the top 2% is clear, corners and all.
        for (int y = 0; y < Size / 50; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                Assert.True(Opacity(meadow, x, y) < 0.01f, $"({x}, {y}) is above every tip but not clear.");
            }
        }
    }

    [Fact]
    public void Blades_narrow_to_a_point()
    {
        var meadow = Meadow.Value;

        // A meadow blade narrows over the top 30% of its length. Following each blade up its own column to its tip,
        // 5% of the card below the tip, about 6% of its length, it is down to under a third of the width it has a
        // third of the way up; a blade cut square would be as wide there as lower down.
        int columnWidth = Size / 24;
        for (int column = 0; column < 24; column++)
        {
            int left = column * Size / 24;
            int Width(int y) => Enumerable.Range(left, columnWidth).Count(x => meadow.Opacity[y * Size + x] > 0.5f);

            int tip = Enumerable.Range(0, Size).First(y => Width(y) > 0);
            int low = Width(Size * 2 / 3);
            int nearTip = Width(tip + Size / 20);

            Assert.True(low > 0, $"Blade {column} has no width a third of the way up.");
            Assert.True(nearTip < low * 0.5f, $"Blade {column} is {nearTip} pixels wide just under its tip, {low} lower down.");
        }
    }

    [Fact]
    public void Each_species_has_its_own_number_and_width_of_blades()
    {
        // A fifth of the way up, every blade stands. Meadow has 24 blades about 13 pixels wide, broad 12 about 27
        // wide and dry 16 stalks about 4 wide.
        int row = Size * 4 / 5;
        var meadow = Runs(Meadow.Value, row);
        var broad = Runs(Broad.Value, row);
        var dry = Runs(Dry.Value, row);

        Assert.Equal(24, meadow.Count);
        Assert.Equal(12, broad.Count);
        Assert.Equal(16, dry.Count);
        Assert.InRange(meadow.Average(), 9.0, 17.0);
        Assert.InRange(broad.Average(), 20.0, 34.0);
        Assert.InRange(dry.Average(), 2.0, 7.0);
    }

    [Fact]
    public void Dry_stalks_end_in_a_seed_head_wider_than_the_stalk()
    {
        var dry = Dry.Value;

        // Below the heads, 16 stalks of about 4 pixels cover an eighth of a row. In the heads, the top third, the
        // spikelets stand out on both sides of each stalk as separate pieces with gaps between them, so no one run is
        // wide, but together they cover a row at least twice as much.
        float stalks = Coverage(dry, Size * 4 / 5);
        float heads = Enumerable.Range(Size / 10, Size / 4).Max(y => Coverage(dry, y));

        Assert.True(heads > stalks * 2f, $"At most {heads:0.000} of a row in the heads is solid, against {stalks:0.000} of the stalks'.");
    }

    [Fact]
    public void Blades_are_green_at_the_foot_and_dry_towards_the_tip()
    {
        var meadow = Meadow.Value;

        // Low down a meadow blade is green, its green well above its red. Near the tips it has dried towards straw,
        // so its red has caught up much of the way.
        var foot = RedOverGreen(meadow, (int)(Size * 0.85f), (int)(Size * 0.95f));
        var tips = RedOverGreen(meadow, (int)(Size * 0.05f), (int)(Size * 0.2f));

        Assert.True(foot < 0.8f, $"At the foot red over green is {foot:0.000}.");
        Assert.True(tips > foot + 0.15f, $"Near the tips red over green is {tips:0.000}, at the foot {foot:0.000}.");
    }

    [Fact]
    public void Dry_grass_is_straw_coloured_and_broad_grass_is_green()
    {
        // Over the whole card, straw has about as much red as green, and living grass far less.
        Assert.True(RedOverGreen(Dry.Value, 0, Size) > 0.95f, "Dry grass is not straw coloured.");
        Assert.True(RedOverGreen(Broad.Value, 0, Size) < 0.8f, "Broad grass is not green.");
    }

    [Fact]
    public void An_unknown_species_names_the_species_there_are()
    {
        var error = Assert.Throws<ArgumentException>(() => GrassBlade.Of("tall"));

        Assert.Equal("Species must be meadow, broad or dry, not tall. (Parameter 'Species')", error.Message);
    }

    private static float Opacity(MaterialImages images, float x, int y) => images.Opacity[y * Size + (int)x];

    // The share of a row that is solid.
    private static float Coverage(MaterialImages images, int y) =>
        Enumerable.Range(0, Size).Count(x => images.Opacity[y * Size + x] > 0.5f) / (float)Size;

    // The widths of the solid runs across a row, left to right.
    private static List<int> Runs(MaterialImages images, int y)
    {
        var runs = new List<int>();
        int run = 0;
        for (int x = 0; x < Size; x++)
        {
            if (images.Opacity[y * Size + x] > 0.5f)
            {
                run++;
            }
            else if (run > 0)
            {
                runs.Add(run);
                run = 0;
            }
        }

        if (run > 0)
        {
            runs.Add(run);
        }

        return runs;
    }

    // The mean red over the mean green of the solid pixels between two rows.
    private static float RedOverGreen(MaterialImages images, int top, int bottom)
    {
        double red = 0;
        double green = 0;
        for (int i = top * Size; i < bottom * Size; i++)
        {
            if (images.Opacity[i] > 0.5f)
            {
                red += images.Color[i * 3];
                green += images.Color[i * 3 + 1];
            }
        }

        Assert.True(green > 0, "There are no solid pixels between those rows.");
        return (float)(red / green);
    }
}
