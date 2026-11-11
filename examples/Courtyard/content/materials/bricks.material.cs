using System.Numerics;
using Super.Materials;

/// <summary>
/// Old bricks in grey lime mortar, from dark plum clinkers to pale buff, each tilted, toned and worn on its own, with
/// chipped edges, a white bloom of salts on the soft ones, soot under the string course and uneven mortar.
/// </summary>
public sealed class Bricks : MaterialGraph
{
    /// <summary>Bricks across the tile.</summary>
    public const int Columns = 8;

    /// <summary>Courses up the tile. Even, so the half-brick offset comes round.</summary>
    public const int Rows = 12;

    /// <summary>
    /// The eight tones a brick can be, from an overfired plum through the reds and orange-browns to an underfired
    /// buff, greyer than the old palette, with the pale ones kept dark enough to stay brick in sunlight. Their
    /// lightness rises steadily, the buff about twice the red. Each brick is one of them exactly.
    /// </summary>
    public static readonly string[] Tones =
    [
        "#52302a", // dark plum clinker
        "#6a3429", // plum-red
        "#84463a", // red
        "#8c5040", // red going orange
        "#9c5a3c", // orange-brown
        "#96683f", // ochre
        "#92704f", // ochre-buff
        "#94765a", // pale buff
    ];

    /// <summary>The two middle reds, tones 2 and 3, which a third of the bricks are.</summary>
    public static readonly int[] MiddleReds = [2, 3];

    // How many of the 96 bricks take each tone: a bell round the reds, 32 of the two middle reds, and the plum
    // and the buff rare accents.
    private static readonly int[] ToneCounts = [5, 14, 16, 16, 20, 13, 8, 4];

    /// <summary>The column of the brick at <paramref name="u"/> in course <paramref name="row"/>, as Tiles.Bricks lays it.</summary>
    private static int ColumnAt(int row, float u)
    {
        var along = u * Columns - 0.5f * row;
        var brick = (int)MathF.Floor(along);

        return ((brick % Columns) + Columns) % Columns;
    }

    /// <summary>The bricks next to brick (row, column): the ones beside it and the two above and two below, wrapping.</summary>
    private static List<(int Row, int Column)> NeighboursOf(int row, int column)
    {
        var found = new List<(int Row, int Column)>();
        // Quarter-brick steps find every brick that shares an edge with this one in a half-brick bond.
        var steps = Columns * 4;

        for (var step = 0; step < steps; step++)
        {
            var u = (step + 0.5f) / steps;

            if (ColumnAt(row, u) != column)
            {
                continue;
            }

            foreach (var other in new[] { row - 1, row + 1 })
            {
                var wrapped = (other + Rows) % Rows;
                var next = (wrapped, ColumnAt(wrapped, u));

                if (!found.Contains(next))
                {
                    found.Add(next);
                }
            }
        }

        foreach (var side in new[] { (column + 1) % Columns, (column + Columns - 1) % Columns })
        {
            if (!found.Contains((row, side)))
            {
                found.Add((row, side));
            }
        }

        return found;
    }

    /// <summary>
    /// The tone of every brick, [row, column], dealt so that no brick shares a tone with any brick it touches, across
    /// the tile's edges too, and a third of them are the two middle reds. The same every time.
    /// </summary>
    public static int[,] ToneTable()
    {
        ulong state = 0x9e3779b97f4a7c15UL;

        for (var attempt = 0; attempt < 10000; attempt++)
        {
            var deck = new List<int>();

            for (var tone = 0; tone < ToneCounts.Length; tone++)
            {
                for (var n = 0; n < ToneCounts[tone]; n++)
                {
                    deck.Add(tone);
                }
            }

            // Shuffle with a fixed generator, so the wall comes out the same on every machine.
            for (var i = deck.Count - 1; i > 0; i--)
            {
                state = state * 6364136223846793005UL + 1442695040888963407UL;
                var j = (int)((state >> 33) % (ulong)(i + 1));
                (deck[i], deck[j]) = (deck[j], deck[i]);
            }

            var table = new int[Rows, Columns];
            var dealt = true;

            for (var row = 0; row < Rows && dealt; row++)
            {
                for (var column = 0; column < Columns; column++)
                {
                    table[row, column] = -1;
                }
            }

            for (var row = 0; row < Rows && dealt; row++)
            {
                for (var column = 0; column < Columns && dealt; column++)
                {
                    var taken = NeighboursOf(row, column).Select(n => table[n.Row, n.Column]).ToHashSet();
                    var pick = deck.FindIndex(tone => !taken.Contains(tone));

                    if (pick < 0)
                    {
                        dealt = false;

                        break;
                    }

                    table[row, column] = deck[pick];
                    deck.RemoveAt(pick);
                }
            }

            if (dealt)
            {
                return table;
            }
        }

        throw new InvalidOperationException("No way was found to deal the brick tones so that neighbours differ.");
    }

    /// <summary>Each brick's tone as tone / 7, so 0 is the plum and 1 the buff. Mortar takes the tone of the brick it is in line with.</summary>
    public static Gray ToneImage()
    {
        var table = ToneTable();

        return Pixel.Gray(p =>
        {
            var row = Math.Min((int)MathF.Floor(p.Uv.Y * Rows), Rows - 1);

            return table[row, ColumnAt(row, p.Uv.X)] / (float)(Tones.Length - 1);
        });
    }

    /// <summary>Where the wall has bloomed with salts: white over about a fifth of it, in broad soft-edged patches.</summary>
    public static Gray EfflorescencePatches()
    {
        return Noise.Perlin(scale: 3, octaves: 4, seed: 11).HistogramScan(0.38f, contrast: 0.85f);
    }

    public override Material Build()
    {
        // 8 bricks of 43.75 cm across the 3.5 m, 12 courses of about 29 cm: big old bricks,
        // so they read at the distance the courtyard is seen from.
        var bricks = Tiles.Bricks(columns: Columns, rows: Rows, gap: 0.075f, bevel: 0.08f);
        var ids = bricks.FloodFill();
        var tone = ToneImage();

        // Each brick leans its own way: a ramp across and a ramp down, each scaled by a random amount from -1 to 1.
        var across = ids.ToGradient(0f).Subtract(0.5f).Multiply(ids.RandomGray(seed: 1).Remap(-1f, 1f));
        var down = ids.ToGradient(90f).Subtract(0.5f).Multiply(ids.RandomGray(seed: 2).Remap(-1f, 1f));
        var tilt = across.Add(down).Multiply(0.15f);

        // Bricks from one kiln still differ: some came out with a hard, smooth skin and others soft and pitted.
        // 1 is the hardest. The dark, overfired bricks lean a little hard and the pale, underfired ones soft.
        var hardness = ids.RandomGray(seed: 5).Multiply(0.8f).Add(tone.Invert().Multiply(0.2f));
        var wear = hardness.Invert();

        // The faces undulate gently, more on the soft bricks, rather than every face being
        // crumpled the same way.
        var undulation = Noise.Perlin(scale: 24, octaves: 3).Remap(-0.03f, 0.03f).Multiply(wear.Remap(0.2f, 1f));
        var faces = bricks
            .Multiply(ids.RandomGray(seed: 3).Remap(0.8f, 1f))
            .Add(tilt.Multiply(bricks))
            .SlopeBlur(Noise.Cells(scale: 24), amount: 0.004f, mode: SlopeBlurMode.Min);

        // Chips: on one brick in five, a second, coarser pass bites bigger pieces out of the arrises,
        // only within a few centimetres of the joint so the faces themselves stay whole.
        var chipped = faces.SlopeBlur(Noise.Cells(scale: 9), amount: 0.05f, mode: SlopeBlurMode.Min);
        var nearJoint = bricks.Levels(0f, 0.3f).Invert().Distance(0.014f);
        var chipBricks = ids.RandomGray(seed: 7).HistogramScan(0.2f, contrast: 0.99f);
        var chipCover = Noise.Perlin(scale: 18, octaves: 3).HistogramScan(0.45f, contrast: 0.9f);
        faces = faces.Blend(chipped, mask: chipBricks.Multiply(nearJoint).Multiply(chipCover));

        // The mortar fills the joints to its own uneven level, ±30% from Perlin, and in about one joint
        // in seven, a patch from cells, it has weathered back to leave a deep recess.
        var mortarNoise = Noise.Perlin(scale: 12, octaves: 4);
        var recessed = Noise.Cells(scale: 10, output: CellOutput.Id).HistogramScan(0.15f, contrast: 0.98f);
        var mortarFill = mortarNoise.Remap(0.7f, 1.3f).Multiply(0.15f).Multiply(recessed.Invert());
        var height = faces.Max(mortarFill).Add(undulation.Multiply(bricks));

        // Small pores and fine grit in the brick faces and the mortar, so they hold detail close
        // up. The soft bricks are pitted far more than the hard ones.
        var pores = Noise.Cells(scale: 520, output: CellOutput.F1).Levels(0f, 0.22f);
        var grit = Noise.White().Blur(0.00025f).Levels(0.2f, 0.8f);
        var pitting = wear.Remap(0.25f, 1f);
        height = height.Add(grit.Remap(-0.012f, 0.012f)).Subtract(pores.Invert().Multiply(pitting).Multiply(0.05f));

        // Spalls: shallow scars a few centimetres across where the fired skin has flaked off the
        // face, showing paler brick under it. Only on the bricks, not the mortar.
        var spalls = Noise.Perlin(scale: 40, octaves: 4).Levels(0.64f, 0.68f).Multiply(bricks.Levels(0.3f, 0.6f));
        height = height.Subtract(spalls.Multiply(0.25f));

        var mortarMask = bricks.Levels(0f, 0.25f).Invert();
        // Each brick is exactly one of the eight tones, so no two neighbours match, then mottled within itself.
        var brickColor = tone
            .GradientMap(Ramp.Of(Tones.Select((hex, i) => (i / (float)(Tones.Length - 1), hex)).ToArray()))
            .Multiply(ids.RandomGray(seed: 6).Remap(0.95f, 1.04f))
            .Multiply(Noise.Perlin(scale: 24, octaves: 3).Remap(0.88f, 1.06f))
            .Multiply(Noise.Perlin(scale: 200, octaves: 3).Remap(0.92f, 1.05f))
            .Multiply(grit.Remap(0.92f, 1.05f))
            .Multiply(pores.Invert().Multiply(pitting).Multiply(0.28f).Invert())
            .Multiply(spalls.Remap(1f, 1.18f));

        // Old lime mortar, grey and dirty rather than clean, its own Perlin lightening and darkening it by ±30%.
        var mortarColor = Rgb.Of("#857e71")
            .Multiply(mortarNoise.Remap(0.7f, 1.3f))
            .Multiply(grit.Remap(0.86f, 1.08f))
            .Multiply(pores.Remap(0.8f, 1f));
        var color = brickColor.Blend(mortarColor, mask: mortarMask);

        // Efflorescence: damp carries salts out through the joints, where they dry to a white rim that bleeds about
        // 4 cm into the soft bricks and over the mortar between them, in patches over about a fifth of the wall.
        // Hard bricks keep it out. A fine grain of crystals breaks its edge.
        var soft = hardness.Levels(0.39f, 0.41f).Invert();
        var bleed = mortarMask.Distance(0.012f);
        var crystals = Noise.Perlin(scale: 60, octaves: 3).Levels(0.35f, 0.65f).Remap(0.5f, 1f);
        var bloom = EfflorescencePatches().Multiply(bleed).Multiply(soft).Multiply(crystals);
        color = color.Blend(Rgb.Of("#d8d4cc"), mask: bloom, opacity: 0.5f);

        // Soot: smoke and dirty rain have darkened a band under the string course, 4 courses up the tile
        // (two thirds of the way down the image), by up to 25%, fading downwards and torn into streaks.
        var band = Gradient.Linear(90f).Transform(offset: new Vector2(0f, 1f / 6f)).Levels(0.5f, 1f);
        var underCourse = Gradient.Linear(90f).Transform(offset: new Vector2(0f, 5f / 12f)).Levels(0.48f, 0.52f);
        var streaks = Noise.Directional(scale: 14, angle: 90f, stretch: 8f, octaves: 3).Levels(0.25f, 0.75f);
        var soot = band.Multiply(underCourse).Multiply(streaks);
        color = color.Multiply(soot.Multiply(0.25f).Invert());

        // Hard bricks have a slight sheen, soft ones are dull, the mortar duller and the salts dullest.
        var brickRoughness = hardness
            .Curve((0f, 0.95f), (0.4f, 0.8f), (0.6f, 0.65f), (1f, 0.55f))
            .Add(Noise.Perlin(scale: 32, octaves: 3).Remap(-0.02f, 0.02f))
            .Blend(Gray.Of(0.95f), mask: bloom);

        return new Material
        {
            Width = 3.5f,
            Depth = 0.02f,
            Occlusion = 0.85f,
            Height = height,
            Color = color,
            Roughness = brickRoughness.Blend(Gray.Of(0.9f), mask: mortarMask),
        };
    }
}
