using System.Numerics;
using Super.Materials;

namespace Courtyard.Tests;

/// <summary>
/// The Courtyard's wood: six boards of six tones, knots, darker ends, silvering that grows with <see cref="Wood.Weathering"/>,
/// and rust that runs down from iron bands.
/// </summary>
[Trait("Speed", "Slow")]
public sealed class WoodTests
{
    private const int Size = 512;

    private sealed class KnotMap : MaterialGraph
    {
        public override Material Build()
        {
            return new Material { Height = Wood.Knots() };
        }
    }

    private static readonly Lazy<MaterialImages> Fresh = new(() => GraphRunner.Run(new Wood { Weathering = 0f }, Size));

    private static Vector2 UvOf(int i) => new((i % Size + 0.5f) / Size, (i / Size + 0.5f) / Size);

    private static float Light(float[] color, int i) => 0.2126f * color[i * 3] + 0.7152f * color[i * 3 + 1] + 0.0722f * color[i * 3 + 2];

    private static float Saturation(float[] color, int i)
    {
        var high = MathF.Max(color[i * 3], MathF.Max(color[i * 3 + 1], color[i * 3 + 2]));
        var low = MathF.Min(color[i * 3], MathF.Min(color[i * 3 + 1], color[i * 3 + 2]));

        return high <= 0f ? 0f : (high - low) / high;
    }

    /// <summary>Pixels on the face of a board, clear of the joints above and below it, between <paramref name="from"/> and <paramref name="to"/> of the way along it.</summary>
    private static IEnumerable<int> OnBoard(int board, float from = 0.1f, float to = 0.9f)
    {
        for (var i = 0; i < Size * Size; i++)
        {
            var uv = UvOf(i);
            var across = uv.Y * Wood.Boards - board;
            var along = Wood.AlongBoard(uv);

            if (across is > 0.15f and < 0.85f && along >= from && along <= to)
            {
                yield return i;
            }
        }
    }

    private static float Warmth(float[] color, int board)
    {
        // Red over blue: the grey tone is about 0.05, the warm brown about 0.34.
        return (float)OnBoard(board).Average(i => color[i * 3] - color[i * 3 + 2]);
    }

    [Fact]
    public void The_six_boards_are_six_tones_from_grey_silver_to_warm_brown()
    {
        var color = Fresh.Value.Color;
        var warmths = Enumerable.Range(0, Wood.Boards).Select(board => Warmth(color, board)).OrderBy(w => w).ToList();

        Assert.True(warmths[0] < 0.12f, $"The greyest board's red is {warmths[0]:0.000} over its blue.");
        Assert.True(warmths[^1] > 0.25f, $"The warmest board's red is {warmths[^1]:0.000} over its blue.");

        // Six stops evenly from 0.05 to 0.34 are 0.06 apart. No two boards share a tone.
        for (var k = 1; k < warmths.Count; k++)
        {
            Assert.True(warmths[k] - warmths[k - 1] > 0.025f, $"Two boards are only {warmths[k] - warmths[k - 1]:0.000} apart: {string.Join(", ", warmths.Select(w => w.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)))}.");
        }
    }

    [Fact]
    public void No_board_is_close_in_tone_to_the_boards_above_and_below_it()
    {
        var color = Fresh.Value.Color;

        for (var board = 0; board < Wood.Boards; board++)
        {
            // The image tiles, so the last board lies against the first.
            var next = (board + 1) % Wood.Boards;
            var step = MathF.Abs(Warmth(color, board) - Warmth(color, next));

            // Two stops apart is about 0.11.
            Assert.True(step > 0.08f, $"Boards {board} and {next} differ by only {step:0.000} red over blue.");
        }
    }

    /// <summary>The linear luminance of an sRGB colour written <c>#rrggbb</c>.</summary>
    private static float Luminance(string hex)
    {
        static float Linear(string pair)
        {
            var c = Convert.ToInt32(pair, 16) / 255f;

            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        return 0.2126f * Linear(hex[1..3]) + 0.7152f * Linear(hex[3..5]) + 0.0722f * Linear(hex[5..7]);
    }

    [Fact]
    public void The_six_tones_are_equally_light_so_no_board_stands_out_in_stripes()
    {
        // Worked by hand: #8a5a34 is 138, 90 and 52, which are 0.2542, 0.1022 and 0.0343 linear, so
        // 0.2126 * 0.2542 + 0.7152 * 0.1022 + 0.0722 * 0.0343 = 0.1296.
        Assert.Equal(0.1296f, Luminance("#8a5a34"), 0.0005f);

        var lights = Wood.Tones.Select(Luminance).ToList();
        var mean = lights.Average();

        foreach (var (hex, light) in Wood.Tones.Zip(lights))
        {
            Assert.True(MathF.Abs(light / mean - 1f) < 0.15f, $"{hex} is {light:0.000} light, {light / mean:P0} of the tones' mean {mean:0.000}.");
        }
    }

    [Fact]
    public void At_the_decks_weathering_neighbouring_boards_still_differ_in_warmth()
    {
        var deck = DeckVariant();
        var color = GraphRunner.Run(deck, Size).Color;

        for (var board = 0; board < Wood.Boards; board++)
        {
            var next = (board + 1) % Wood.Boards;
            var step = MathF.Abs(Warmth(color, board) - Warmth(color, next));

            Assert.True(step > 0.04f, $"At weathering {deck.Weathering} and tone {deck.Tone}, boards {board} and {next} differ by only {step:0.000} red over blue.");
        }
    }

    [Fact]
    public void At_full_tone_every_board_is_the_warm_brown()
    {
        var warm = GraphRunner.Run(new Wood { Weathering = 0f, Tone = 1f }, Size).Color;
        var warmest = Enumerable.Range(0, Wood.Boards).Max(board => Warmth(Fresh.Value.Color, board));

        for (var board = 0; board < Wood.Boards; board++)
        {
            // Each board keeps its own rings, knots and slight lift or shade, so allow about a tenth either way. Half
            // way to warm, the greyest board would be near 0.19.
            var warmth = Warmth(warm, board);
            Assert.True(MathF.Abs(warmth / warmest - 1f) < 0.12f, $"At tone 1, board {board} is {warmth:0.000} red over blue, not the warm brown's {warmest:0.000}.");
        }
    }

    [Fact]
    public void Tone_moves_a_board_that_share_of_the_way_to_either_end()
    {
        // Board 2 is dealt the grey, the first of the six tones, at 0 on the ramp, and board 0 the third, at 0.4.
        Assert.Equal(0f, Wood.ToneAt(2, 0f), 0.0001f);
        Assert.Equal(0.6f, Wood.ToneAt(2, 0.6f), 0.0001f);
        Assert.Equal(0.7f, Wood.ToneAt(0, 0.5f), 0.0001f);
        Assert.Equal(0.2f, Wood.ToneAt(0, -0.5f), 0.0001f);
        Assert.Equal(0f, Wood.ToneAt(0, -1f), 0.0001f);
    }

    /// <summary>The wood as <c>deck-boards.material.json</c> sets it, read from the example's own file.</summary>
    private static Wood DeckVariant([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "examples", "Courtyard", "content", "materials", "deck-boards.material.json");
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;

        return new Wood
        {
            Weathering = root.TryGetProperty("weathering", out var weathering) ? weathering.GetSingle() : new Wood().Weathering,
            Tone = root.TryGetProperty("tone", out var tone) ? tone.GetSingle() : 0f,
        };
    }

    [Fact]
    public void Weathering_turns_the_wood_paler_greyer_and_rougher()
    {
        var fresh = Fresh.Value;
        var silvered = GraphRunner.Run(new Wood { Weathering = 1f }, Size);
        var faces = Enumerable.Range(0, Wood.Boards).SelectMany(board => OnBoard(board)).ToList();

        var freshLight = faces.Average(i => Light(fresh.Color, i));
        var silverLight = faces.Average(i => Light(silvered.Color, i));
        var freshSaturation = faces.Average(i => Saturation(fresh.Color, i));
        var silverSaturation = faces.Average(i => Saturation(silvered.Color, i));
        var freshRough = faces.Average(i => fresh.Roughness[i]);
        var silverRough = faces.Average(i => silvered.Roughness[i]);

        Assert.True(silverLight > freshLight * 1.04f, $"Silvered wood is {silverLight:0.000} light, fresh {freshLight:0.000}.");
        Assert.True(silverSaturation < freshSaturation * 0.75f, $"Silvered wood is {silverSaturation:0.000} saturated, fresh {freshSaturation:0.000}.");
        Assert.True(silverRough > 0.85f && freshRough < 0.8f, $"Silvered wood is {silverRough:0.000} rough, fresh {freshRough:0.000}.");
    }

    [Fact]
    public void Partly_weathered_wood_is_silvered_in_patches_that_grow_with_the_weather()
    {
        var faces = Enumerable.Range(0, Wood.Boards).SelectMany(board => OnBoard(board)).ToList();

        // Silvered wood is 0.9 rough, the fresh face 0.62 to 0.85, and the patches' edges in between.
        float Silvered(float weathering)
        {
            var wood = GraphRunner.Run(new Wood { Weathering = weathering }, Size);

            return faces.Count(i => wood.Roughness[i] >= 0.86f) / (float)faces.Count;
        }

        var light = Silvered(0.3f);
        var half = Silvered(0.5f);
        var heavy = Silvered(0.8f);

        Assert.True(half is > 0.15f and < 0.85f, $"{half:P0} of half-weathered wood is silvered.");
        Assert.True(half - light > 0.1f && heavy - half > 0.15f, $"{light:P0}, {half:P0} and {heavy:P0} of the wood is silvered at weathering 0.3, 0.5 and 0.8.");
    }

    [Fact]
    public void The_boards_have_two_to_four_knots_to_the_metre()
    {
        var knots = GraphRunner.Run(new KnotMap(), Size).Height;
        var count = Islands(knots, 0.5f);

        // The six boards are six metres of board.
        var perMetre = count / (float)Wood.Boards;

        Assert.True(perMetre is >= 2f and <= 4f, $"The boards have {count} knots, {perMetre:0.0} to the metre.");
    }

    [Fact]
    public void Knots_are_darker_than_the_wood_round_them()
    {
        var knots = GraphRunner.Run(new KnotMap(), Size).Height;
        var color = Fresh.Value.Color;
        var hearts = Enumerable.Range(0, Size * Size).Where(i => knots[i] > 0.7f).ToList();
        var clear = Enumerable.Range(0, Size * Size).Where(i => knots[i] == 0f).ToList();

        Assert.NotEmpty(hearts);
        Assert.NotEmpty(clear);

        var heart = hearts.Average(i => Light(color, i));
        var wood = clear.Average(i => Light(color, i));

        Assert.True(heart < 0.7f * wood, $"The knots' hearts are {heart:0.000} light, the wood {wood:0.000}.");
    }

    [Fact]
    public void The_end_grain_is_darker_than_the_middle_of_the_board()
    {
        var color = Fresh.Value.Color;
        var ends = new List<float>();
        var middles = new List<float>();

        for (var board = 0; board < Wood.Boards; board++)
        {
            // The joint itself is the first 2.5 mm; the end grain is the next centimetre.
            ends.AddRange(OnBoard(board, 0.004f, 0.015f).Select(i => Light(color, i)));
            ends.AddRange(OnBoard(board, 0.985f, 0.996f).Select(i => Light(color, i)));
            middles.AddRange(OnBoard(board, 0.2f, 0.8f).Select(i => Light(color, i)));
        }

        Assert.NotEmpty(ends);

        var end = ends.Average();
        var middle = middles.Average();

        Assert.True(end < 0.8f * middle, $"The ends are {end:0.000} light, the middles {middle:0.000}.");
    }

    [Fact]
    public void Rust_runs_down_the_boards_from_an_iron_band_and_not_up()
    {
        var plain = Fresh.Value.Color;
        var rusted = GraphRunner.Run(new Wood { Weathering = 0f, RustAt = "0.5" }, Size).Color;
        var below = Enumerable.Range(0, Wood.Boards).SelectMany(board => OnBoard(board, 0.43f, 0.49f)).ToList();
        var above = Enumerable.Range(0, Wood.Boards).SelectMany(board => OnBoard(board, 0.51f, 0.57f)).ToList();

        // Rust is orange-brown, so it takes more blue than red away from the wood.
        float Redness(float[] color, int i) => color[i * 3] / MathF.Max(color[i * 3 + 2], 1e-3f);

        var plainBelow = below.Average(i => Redness(plain, i));
        var rustedBelow = below.Average(i => Redness(rusted, i));
        var changedAbove = above.Count(i => MathF.Abs(rusted[i * 3] - plain[i * 3]) > 1e-4f);

        Assert.True(rustedBelow > plainBelow * 1.2f, $"Below the band the wood's red over blue is {rustedBelow:0.000} with rust, {plainBelow:0.000} without.");
        Assert.Equal(0, changedAbove);
    }

    [Fact]
    public void A_weathering_outside_0_to_1_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Run(new Wood { Weathering = 1.5f }, 16));

        Assert.StartsWith("Weathering must be from 0 to 1, not 1.5.", error.Message);
    }

    [Fact]
    public void A_rust_band_that_is_not_a_number_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => GraphRunner.Run(new Wood { RustAt = "0.5 top" }, 16));

        Assert.StartsWith("RustAt must be numbers from 0 to 1 separated by spaces, and \"top\" isn't one.", error.Message);
    }

    /// <summary>How many separate islands of pixels above <paramref name="threshold"/> there are, joined on their sides, wrapping at the edges.</summary>
    private static int Islands(float[] image, float threshold)
    {
        var seen = new bool[image.Length];
        var count = 0;
        var stack = new Stack<int>();

        for (var start = 0; start < image.Length; start++)
        {
            if (seen[start] || image[start] <= threshold)
            {
                continue;
            }

            count++;
            seen[start] = true;
            stack.Push(start);

            while (stack.Count > 0)
            {
                var i = stack.Pop();
                var x = i % Size;
                var y = i / Size;

                foreach (var n in new[] { y * Size + (x + 1) % Size, y * Size + (x + Size - 1) % Size, (y + 1) % Size * Size + x, (y + Size - 1) % Size * Size + x })
                {
                    if (!seen[n] && image[n] > threshold)
                    {
                        seen[n] = true;
                        stack.Push(n);
                    }
                }
            }
        }

        return count;
    }
}
