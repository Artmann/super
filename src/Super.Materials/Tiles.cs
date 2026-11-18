namespace Super.Materials;

/// <summary>Brick and tile patterns. Every one tiles.</summary>
public static class Tiles
{
    /// <summary>
    /// Rows of bricks. <paramref name="gap"/> and <paramref name="bevel"/> are fractions of a brick's height.
    /// Each row shifts by <paramref name="offset"/> of a brick times its row number. The output is 0 in the gaps,
    /// rises over the bevel to 1, then each brick is lowered by up to <paramref name="heightVariation"/> at random.
    /// So that the bricks tile, <paramref name="offset"/> times <paramref name="rows"/> must be a whole number
    /// (an even number of rows with the default offset of 0.5), or an <see cref="ArgumentException"/> is thrown.
    /// </summary>
    public static Gray Bricks(int columns, int rows, float gap = 0.04f, float bevel = 0.1f, float offset = 0.5f, float heightVariation = 0f, int? seed = null)
    {
        Arguments.AtLeast(columns, 1, nameof(columns));
        Arguments.AtLeast(rows, 1, nameof(rows));

        Arguments.Finite(offset, nameof(offset));

        // The top row meets the bottom one, so the shift must come round to a whole number of bricks.
        var shift = offset * rows;

        if (MathF.Abs(shift - MathF.Round(shift)) > 1e-4f)
        {
            throw new ArgumentException(FormattableString.Invariant($"offset × rows must be a whole number so the bricks tile, not {offset} × {rows} = {shift}. Use an even number of rows, or an offset of 0 or 1."));
        }

        return new Gray(new BricksNode(new BrickLayout(columns, rows, gap, bevel, offset, heightVariation), seed));
    }

    /// <summary><see cref="Bricks"/> with no offset between rows.</summary>
    public static Gray Grid(int columns, int rows, float gap = 0.04f, float bevel = 0.1f, float heightVariation = 0f, int? seed = null)
        => Bricks(columns, rows, gap, bevel, 0f, heightVariation, seed);
}

/// <summary>
/// The shape of a wall of bricks, and the height it has at each pixel. <see cref="Gap"/> and <see cref="Bevel"/> are
/// fractions of a brick's height, and each row shifts by <see cref="Offset"/> of a brick times its row number.
/// </summary>
readonly record struct BrickLayout(int Columns, int Rows, float Gap, float Bevel, float Offset, float HeightVariation)
{
    /// <summary>The row of bricks pixel row <paramref name="y"/> crosses.</summary>
    public int BrickRowAt(int y, int size) => Math.Min((int)MathF.Floor((y + 0.5f) / size * Rows), Rows - 1);

    /// <summary>How far pixel row <paramref name="y"/> is from the top or bottom of its brick, in brick heights.</summary>
    public float BrickEdgeDistanceY(int y, int size, int row)
    {
        var fy = (y + 0.5f) / size * Rows - row;

        return Math.Min(fy, 1f - fy);
    }

    /// <summary>
    /// The height at pixel <paramref name="x"/> of a row that crosses brick row <paramref name="row"/>
    /// <paramref name="distanceY"/> from its top or bottom: 0 in the gap, rising over the bevel to 1, and lowered at
    /// random per brick by up to <see cref="HeightVariation"/>.
    /// </summary>
    public float BrickHeightAt(int x, int size, int row, float distanceY, ulong seed)
    {
        // Distances are measured in brick heights, so a brick that is wider than tall has a longer way to its side.
        var widthInHeights = (float)Rows / Columns;
        var along = (x + 0.5f) / size * Columns - Offset * row;
        var brick = MathF.Floor(along);
        var fx = along - brick;
        var column = (((int)brick % Columns) + Columns) % Columns;
        var value = Profile(Math.Min(Math.Min(fx, 1f - fx) * widthInHeights, distanceY));

        if (HeightVariation != 0f)
        {
            value *= 1f - HeightVariation * Hash.Unit(seed, column, row);
        }

        return value;
    }

    /// <summary>0 in the gap, rising over the bevel to 1, for a distance from the brick's nearest edge.</summary>
    float Profile(float distance)
    {
        var halfGap = Gap / 2f;

        if (distance < halfGap)
        {
            return 0f;
        }

        if (Bevel > 0f)
        {
            return Math.Min((distance - halfGap) / Bevel, 1f);
        }

        return 1f;
    }
}

sealed class BricksNode : Node
{
    readonly BrickLayout _layout;

    public BricksNode(BrickLayout layout, int? seed) : base(1)
    {
        _layout = layout;

        if (seed is { } s)
        {
            UseSeed(s);
        }
    }

    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;

        context.Rows(y =>
        {
            var row = _layout.BrickRowAt(y, size);
            var distanceY = _layout.BrickEdgeDistanceY(y, size, row);

            for (var x = 0; x < size; x++)
            {
                output[y * size + x] = _layout.BrickHeightAt(x, size, row, distanceY, Seed);
            }
        });
    }
}
