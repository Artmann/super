using System.Numerics;
using Super.Materials;

/// <summary>
/// Old flagstones, one to a cell of an 8 by 8 grid, each worn on its own: chamfered and chipped arrises, hairline
/// cracks across a third of them, dirt and fine gravel in the joints, moss in the joints and on each stone's low half,
/// five close greys and a rare warm sandstone, and crowns polished by feet.
/// </summary>
/// <remarks>
/// The Courtyard's path is modelled stone by stone, so the model maps each stone's top onto one cell of this grid,
/// its outline on the cell's edge and its middle on the cell's middle (see <c>flag_uv</c> in <c>art/courtyard.py</c>).
/// The grid's size, <see cref="Stones"/>, <see cref="Gap"/> and the width must match the script.
/// </remarks>
public sealed class Flagstones : MaterialGraph
{
    /// <summary>Stones across the tile and down it.</summary>
    public const int Stones = 8;

    /// <summary>The joint between two stones, as a fraction of a stone.</summary>
    public const float Gap = 0.06f;

    /// <summary>
    /// The tones a stone can be: five greys a small step apart, two of them a touch warm and one a touch cool, and a
    /// warm sandstone. Each stone is one of them exactly.
    /// </summary>
    public static IReadOnlyList<string> Tones { get; } =
    [
        "#77736b", // mid grey
        "#7d7970", // a step lighter
        "#73726e", // cool grey
        "#827d73", // light warm grey
        "#79766f", // grey
        "#8a7c67", // warm sandstone
    ];

    /// <summary>The sandstone, the last tone.</summary>
    public const int Sandstone = 5;

    // How many of the 64 stones take each tone: the greys share 56, and 8, one in eight, are sandstone.
    private static readonly int[] ToneCounts = [12, 11, 11, 11, 11, 8];

    // How many stones have no crack, one crack and two: 21 of the 64, a third, are cracked.
    private static readonly int[] CrackCounts = [43, 14, 7];

    /// <summary>Deals <paramref name="counts"/> of each value into the grid from a fixed shuffle, so it is the same every time.</summary>
    private static int[,] Deal(int[] counts, ulong state)
    {
        var deck = new List<int>();

        for (var value = 0; value < counts.Length; value++)
        {
            for (var n = 0; n < counts[value]; n++)
            {
                deck.Add(value);
            }
        }

        if (deck.Count != Stones * Stones)
        {
            throw new InvalidOperationException($"The counts deal {deck.Count} stones, not {Stones * Stones}.");
        }

        for (var i = deck.Count - 1; i > 0; i--)
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            var j = (int)((state >> 33) % (ulong)(i + 1));
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }

        var table = new int[Stones, Stones];

        for (var i = 0; i < deck.Count; i++)
        {
            table[i / Stones, i % Stones] = deck[i];
        }

        return table;
    }

    /// <summary>The tone of every stone, [row, column], an index into <see cref="Tones"/>.</summary>
    public static int[,] ToneTable() => Deal(ToneCounts, 0x2545f4914f6cdd1dUL);

    /// <summary>How many hairline cracks cross each stone, [row, column]: 0, 1 or 2.</summary>
    public static int[,] CrackTable() => Deal(CrackCounts, 0x9e3779b97f4a7c15UL);

    private static (int Row, int Column) StoneAt(Vector2 uv)
    {
        return (Math.Min((int)MathF.Floor(uv.Y * Stones), Stones - 1), Math.Min((int)MathF.Floor(uv.X * Stones), Stones - 1));
    }

    /// <summary>Each stone's tone as tone / 5, so the greys are below 0.9 and the sandstone is 1.</summary>
    public static Gray ToneImage()
    {
        var table = ToneTable();

        return Pixel.Gray(p =>
        {
            var (row, column) = StoneAt(p.Uv);

            return table[row, column] / (float)(Tones.Count - 1);
        });
    }

    /// <summary>
    /// The hairline cracks: 1 on a crack, 0 elsewhere. The first crack is a line right across its stone, through a
    /// point near the middle at an angle of its own. A second runs off it to one side, as a crack branches, at least
    /// 40 degrees away. Both are bent along their length by Perlin and kinked by the edges of cells, so they wander the
    /// way a crack follows the grain. Only the stones <see cref="CrackTable"/> cracks have them.
    /// </summary>
    public static Gray Cracks()
    {
        var table = CrackTable();

        // How far each pixel is from its stone's nearest crack, in stones, or 1 where the stone has none.
        var distance = Pixel.Gray(p =>
        {
            var (row, column) = StoneAt(p.Uv);
            var count = table[row, column];

            if (count == 0)
            {
                return 1f;
            }

            var local = new Vector2(p.Uv.X * Stones - column, p.Uv.Y * Stones - row);
            var angle = Hash(row, column, 10) * MathF.PI;
            var through = new Vector2(0.3f + 0.4f * Hash(row, column, 20), 0.3f + 0.4f * Hash(row, column, 30));
            var across = new Vector2(MathF.Sin(angle), -MathF.Cos(angle));
            var side = Vector2.Dot(local - through, across);
            var nearest = MathF.Abs(side);

            if (count == 2)
            {
                // The branch starts on the first crack and runs off on the side its hash picks.
                var turn = (40f + 100f * Hash(row, column, 40)) * MathF.PI / 180f;
                var branchAngle = angle + turn;
                var start = through + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (Hash(row, column, 50) - 0.5f) * 0.4f;
                var branchAcross = new Vector2(MathF.Sin(branchAngle), -MathF.Cos(branchAngle));
                var onBranchSide = Hash(row, column, 60) < 0.5f ? side > 0f : side < 0f;

                if (onBranchSide)
                {
                    nearest = MathF.Min(nearest, MathF.Abs(Vector2.Dot(local - start, branchAcross)));
                }
            }

            return MathF.Min(nearest, 1f);
        });

        // A warp moves a pixel by the amount times the slope, and these noises are steep, so the amounts are small:
        // the Perlin bends a crack by a few centimetres, the cells kink it by a few millimetres.
        return distance
            .Warp(Noise.Perlin(scale: 10, octaves: 3, seed: 41), amount: 0.00025f)
            .Warp(Noise.Cells(scale: 60, output: CellOutput.F1, seed: 42), amount: 0.00003f)
            .Levels(0f, 0.006f)
            .Invert();
    }

    private static float Hash(int row, int column, int salt)
    {
        var h = (uint)(row * 73856093) ^ (uint)(column * 19349663) ^ (uint)(salt * 83492791);

        h ^= h >> 16;
        h *= 0x7feb352dU;
        h ^= h >> 15;
        h *= 0x846ca68bU;
        h ^= h >> 16;

        return (h & 0xffffff) / (float)0x1000000;
    }

    public override Material Build()
    {
        // The cracks are made first. A node's noise is seeded by the order it is made in, so made first they come out
        // the same here as in a graph that makes nothing else, which is how FlagstoneTests looks at them.
        var crackLines = Cracks();

        // 8 stones of 65 cm across the 5.2 m, each 1 inside its square and 0 in the joints round it.
        var stones = Tiles.Grid(columns: Stones, rows: Stones, gap: Gap, bevel: 0f);
        var ids = stones.FloodFill(threshold: 0.5f);
        var joint = stones.Invert();

        // The chamfer: within 2 cm of the joint the arris rounds down, steeply at the very edge.
        var nearJoint = joint.Distance(0.004f);
        // The joints themselves are 0, so the dirt fills them.
        var chamfer = nearJoint.Curve((0f, 1f), (0.35f, 0.97f), (0.7f, 0.82f), (0.9f, 0.62f), (1f, 0.45f)).Multiply(stones);

        // The faces: broad, low undulation and a fine grain, pits and the odd shallow spall.
        var undulation = Noise.Perlin(scale: 20, octaves: 4, seed: 3);
        var grain = Noise.White(seed: 4).Blur(0.0002f).Remap(-0.015f, 0.015f);
        var pits = Noise.Cells(scale: 260, output: CellOutput.F1, seed: 5).Levels(0f, 0.25f).Invert().Multiply(0.04f);
        var spalls = Noise.Perlin(scale: 40, octaves: 4, seed: 6).Levels(0.7f, 0.73f);
        var faces = chamfer
            .Add(undulation.Remap(-0.05f, 0.05f).Multiply(stones))
            .Add(grain)
            .Subtract(pits)
            .Subtract(spalls.Multiply(0.08f).Multiply(stones));

        // Chips: nicks bitten out of the arrises, coarser here and there, only within a few centimetres of the joint.
        var nicked = faces.SlopeBlur(Noise.Cells(scale: 90, seed: 7), amount: 0.003f, mode: SlopeBlurMode.Min);
        var bitten = faces.SlopeBlur(Noise.Cells(scale: 40, seed: 8), amount: 0.006f, mode: SlopeBlurMode.Min);
        var edgeBand = joint.Distance(0.008f);
        var chipCover = Noise.Perlin(scale: 28, octaves: 3, seed: 9).HistogramScan(0.35f, contrast: 0.9f);
        faces = faces.Blend(nicked, mask: edgeBand).Blend(bitten, mask: edgeBand.Multiply(chipCover));

        var cracks = crackLines.Multiply(stones);
        var stoneHeight = faces.Subtract(cracks.Multiply(0.2f));

        // Dirt and fine gravel fill the joints and the deepest chips: blurred noise for the dirt, with small round
        // stones from cells standing out of it.
        var gravel = Noise.Cells(scale: 340, output: CellOutput.F1, seed: 10).Levels(0f, 0.6f).Invert();
        var grit = Noise.White(seed: 11).Blur(0.0006f).Remap(0.18f, 0.32f)
            .Add(gravel.Multiply(0.14f))
            .Add(Noise.Perlin(scale: 32, octaves: 3, seed: 12).Remap(0f, 0.08f));
        var fill = Gray.HeightBlend(stoneHeight, grit, offset: 0.5f, contrast: 0.92f);

        // Moss: in the joints, in patches, and creeping onto the low half of each stone by its own height, only
        // near the edge, where water stands and feet don't go.
        var low = undulation.Levels(0.42f, 0.5f).Invert();
        var mossPatches = Noise.Perlin(scale: 9, octaves: 4, seed: 13).HistogramScan(0.4f, contrast: 0.75f);
        var creep = joint.Distance(0.012f).Levels(0.2f, 0.7f);
        var moss = fill.Mask.Max(creep.Multiply(low)).Multiply(mossPatches)
            .Multiply(Noise.Perlin(scale: 90, octaves: 3, seed: 14).Levels(0.35f, 0.6f));

        // Each stone is one of the tones, a touch lighter or darker on its own, mottled, flecked and darkened in
        // its cracks and its chamfer, where dirt works in.
        var flecks = Noise.White(seed: 15).Blur(0.00025f).Levels(0.25f, 0.75f).Remap(0.9f, 1.08f);
        var stoneColor = ToneImage()
            .GradientMap(Ramp.Of(Tones.Select((hex, i) => (i / (float)(Tones.Count - 1), hex)).ToArray()))
            .Multiply(ids.RandomGray(seed: 16).Remap(0.96f, 1.04f))
            .Multiply(Noise.Perlin(scale: 14, octaves: 4, seed: 17).Remap(0.9f, 1.06f))
            .Multiply(Noise.Perlin(scale: 70, octaves: 3, seed: 18).Remap(0.93f, 1.04f))
            .Multiply(flecks)
            .Multiply(spalls.Remap(1f, 1.1f))
            .Multiply(cracks.Remap(1f, 0.45f))
            .Multiply(nearJoint.Levels(0.3f, 1f).Remap(1f, 0.82f));

        // The crowns, where feet go, are rubbed a little lighter and clean.
        var crown = Gradient.Radial().Transform(tile: Stones).Levels(0.35f, 0.85f).Multiply(ids.RandomGray(seed: 19).Remap(0.5f, 1f));
        stoneColor = stoneColor.Multiply(crown.Remap(1f, 1.04f));

        var dirtColor = Noise.White(seed: 20).Blur(0.0003f).GradientMap(Ramp.Of("#3d362e", "#544a3e"))
            .Blend(Noise.White(seed: 21).GradientMap(Ramp.Of("#6f6a60", "#8a8478")), mask: gravel.Levels(0.6f, 0.8f));
        var mossColor = Noise.Perlin(scale: 50, octaves: 3, seed: 22).GradientMap(Ramp.Of("#2f3a1c", "#4a5626"));
        var color = stoneColor.Blend(dirtColor, mask: fill.Mask).Blend(mossColor, mask: moss.Multiply(0.9f));

        // Rough stone, 0.7 to 0.9, polished to 0.5 on the crowns; the dirt and the moss rougher still.
        var roughness = Noise.Perlin(scale: 18, octaves: 4, seed: 23).Remap(0.7f, 0.9f)
            .Blend(Gray.Of(0.5f), mask: crown)
            .Blend(Gray.Of(0.95f), mask: fill.Mask)
            .Blend(Gray.Of(0.9f), mask: moss);

        return new Material
        {
            Width = 5.2f,
            Depth = 0.02f,
            Occlusion = 0.85f,
            Height = fill.Height,
            Color = color,
            Roughness = roughness,
        };
    }
}
