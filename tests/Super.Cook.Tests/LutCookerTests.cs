using System.Numerics;
using System.Text;
using Rendition;

namespace Super.Cook.Tests;

public sealed class LutCookerTests
{
    [Fact]
    public void A_cube_file_is_read_with_red_changing_fastest()
    {
        // Two cells a side: the eight rows run red, then green, then blue. Each cell holds its own index over 10.
        var text = new StringBuilder("TITLE \"Steps\"\n# a comment\nLUT_3D_SIZE 2\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n\n");
        for (int i = 0; i < 8; i++)
        {
            text.Append(FormattableString.Invariant($"{i / 10.0:0.0} 0.5 1\n"));
        }

        var output = new CookOutput();
        var lut = LutCooker.FromCube(text.ToString(), "steps.cube", output);

        Assert.Empty(output.Problems);
        Assert.NotNull(lut);
        Assert.Equal(2, lut.Size);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(i / 10f, (float)lut.Cells[i * 4], 0.001f);
            Assert.Equal(0.5f, (float)lut.Cells[(i * 4) + 1]);
            Assert.Equal(1f, (float)lut.Cells[(i * 4) + 2]);
        }
    }

    [Theory]
    [InlineData("LUT_1D_SIZE 16\n", 1, "steps.cube is a 1D table. Only 3D tables, with LUT_3D_SIZE, can grade the picture.")]
    [InlineData("LUT_3D_SIZE 1\n", 1, "steps.cube has LUT_3D_SIZE 1. It must be a whole number from 2 to 128.")]
    [InlineData("LUT_3D_SIZE 2\nDOMAIN_MAX 2 2 2\n", 2, "steps.cube has DOMAIN_MAX 2 2 2. Only tables over 0 to 1 are read; export it without a custom domain.")]
    [InlineData("LUT_3D_SIZE 2\n0 0\n", 2, "steps.cube has \"0 0\" where a cell's three numbers should be.")]
    [InlineData("LUT_3D_SIZE 2\n0,5 0 0\n", 2, "steps.cube has \"0,5 0 0\" where a cell's three numbers should be.")]
    public void A_broken_cube_file_says_what_is_wrong_on_which_line(string text, int line, string message)
    {
        var output = new CookOutput();

        Assert.Null(LutCooker.FromCube(text, "steps.cube", output));
        var problem = Assert.Single(output.Problems);
        Assert.Equal(message, problem.Message);
        Assert.Equal(line, problem.Line);
        Assert.True(problem.IsError);
    }

    [Theory]
    [InlineData("0 0 0\n", "steps.cube has no LUT_3D_SIZE line, so its size is unknown.")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n", "steps.cube has 1 cells, but LUT_3D_SIZE 2 needs 8.")]
    public void A_cube_file_without_its_size_or_cells_is_refused(string text, string message)
    {
        var output = new CookOutput();

        Assert.Null(LutCooker.FromCube(text, "steps.cube", output));
        Assert.Equal(message, Assert.Single(output.Problems).Message);
    }

    [Fact]
    public void A_cube_file_reads_the_same_under_a_comma_decimal_culture()
    {
        using var culture = new CultureScope("sv-SE");
        var text = "LUT_3D_SIZE 2\n" + string.Concat(Enumerable.Repeat("0.25 0.5 0.75\n", 8));

        var lut = LutCooker.FromCube(text, "steps.cube", new CookOutput());

        Assert.NotNull(lut);
        Assert.Equal(0.25f, (float)lut.Cells[0]);
        Assert.Equal(0.75f, (float)lut.Cells[2]);
    }

    // The way back from --neutral-lut: the stamped strip, cooked untouched, is the neutral table.
    [Fact]
    public void The_neutral_strip_cooks_back_to_a_neutral_table_within_one_step_of_eight_bits()
    {
        var lut = LutCooker.FromStrip(StripPng(NeutralLutStrip.Stamped(new byte[1024 * 32 * 4], 1024, 32), 1024, 32), "neutral.lut.png", new CookOutput());

        Assert.NotNull(lut);
        Assert.Equal(32, lut.Size);
        var neutral = ColorLutData.Neutral(32);
        float worst = 0f;
        for (int i = 0; i < neutral.Cells.Length; i++)
        {
            worst = MathF.Max(worst, MathF.Abs((float)lut.Cells[i] - (float)neutral.Cells[i]));
        }

        Assert.True(worst <= 1f / 255f, $"a cell is {worst * 255f} steps of eight bits off neutral");
    }

    [Fact]
    public void A_strip_is_read_with_blue_rising_from_square_to_square()
    {
        // Two squares of two pixels: the pixel at x = 3, y = 1 is red 1, green 1, blue 1, the cube's last cell.
        var pixels = new byte[4 * 2 * 4];
        Paint(pixels, 4, 3, 1, new Vector3(10, 20, 30));
        Paint(pixels, 4, 2, 0, new Vector3(40, 50, 60));

        var lut = LutCooker.FromStrip(StripPng(pixels, 4, 2), "two.lut.png", new CookOutput());

        Assert.NotNull(lut);
        Assert.Equal(new Vector3(10, 20, 30) / 255f, Cell(lut, 7), new Vector3Comparer(0.001f));
        Assert.Equal(new Vector3(40, 50, 60) / 255f, Cell(lut, 4), new Vector3Comparer(0.001f));
    }

    [Fact]
    public void A_strip_that_is_not_squares_side_by_side_is_refused()
    {
        var output = new CookOutput();

        Assert.Null(LutCooker.FromStrip(StripPng(new byte[5 * 2 * 4], 5, 2), "odd.lut.png", output));
        Assert.Equal(
            "odd.lut.png is 5x2. A colour table strip is N squares of N pixels side by side, such as 1024x32, with N from 2 to 128.",
            Assert.Single(output.Problems).Message);
    }

    [Fact]
    public void The_cooker_takes_strips_by_their_double_extension()
    {
        Assert.Contains(".lut.png", new LutCooker().Extensions);
        Assert.Contains(".cube", new LutCooker().Extensions);
    }

    private static Vector3 Cell(ColorLutData lut, int index) =>
        new((float)lut.Cells[index * 4], (float)lut.Cells[(index * 4) + 1], (float)lut.Cells[(index * 4) + 2]);

    private static void Paint(byte[] pixels, int width, int x, int y, Vector3 color)
    {
        int at = ((y * width) + x) * 4;
        pixels[at] = (byte)color.X;
        pixels[at + 1] = (byte)color.Y;
        pixels[at + 2] = (byte)color.Z;
        pixels[at + 3] = 255;
    }

    private static byte[] StripPng(byte[] rgba, int width, int height)
    {
        string path = Path.Combine(Path.GetTempPath(), $"strip-{Guid.NewGuid():N}.png");
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

    private sealed class Vector3Comparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 x, Vector3 y) => Vector3.Distance(x, y) <= tolerance;

        public int GetHashCode(Vector3 obj) => 0;
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}
