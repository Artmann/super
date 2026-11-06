namespace Super.Materials;

/// <summary>Brick and tile patterns. Every one tiles.</summary>
public static class Tiles
{
    /// <summary>
    /// Rows of bricks. <paramref name="gap"/> and <paramref name="bevel"/> are fractions of a brick's height.
    /// Each row shifts by <paramref name="offset"/> of a brick times its row number. The output is 0 in the gaps,
    /// rises over the bevel to 1, then each brick is lowered by up to <paramref name="heightVariation"/> at random.
    /// For the rows to meet at the top and bottom edge, use an even number of rows with an offset of 0.5.
    /// </summary>
    public static Gray Bricks(int columns, int rows, float gap = 0.04f, float bevel = 0.1f, float offset = 0.5f, float heightVariation = 0f, int? seed = null)
    {
        Arguments.AtLeast(columns, 1, nameof(columns));
        Arguments.AtLeast(rows, 1, nameof(rows));

        return new Gray(new BricksNode(columns, rows, gap, bevel, offset, heightVariation, seed));
    }

    /// <summary><see cref="Bricks"/> with no offset between rows.</summary>
    public static Gray Grid(int columns, int rows, float gap = 0.04f, float bevel = 0.1f, float heightVariation = 0f, int? seed = null)
        => Bricks(columns, rows, gap, bevel, 0f, heightVariation, seed);
}

sealed class BricksNode : Node
{
    readonly int _columns;
    readonly int _rows;
    readonly float _gap;
    readonly float _bevel;
    readonly float _offset;
    readonly float _heightVariation;

    public BricksNode(int columns, int rows, float gap, float bevel, float offset, float heightVariation, int? seed) : base(1)
    {
        _columns = columns;
        _rows = rows;
        _gap = gap;
        _bevel = bevel;
        _offset = offset;
        _heightVariation = heightVariation;

        if (seed is { } s)
        {
            UseSeed(s);
        }
    }

    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;

        // Distances are measured in brick heights, so a brick that is wider than tall has a longer way to its side.
        var widthInHeights = (float)_rows / _columns;
        var halfGap = _gap / 2f;

        context.Rows(y =>
        {
            var row = Math.Min((int)MathF.Floor((y + 0.5f) / size * _rows), _rows - 1);
            var fy = (y + 0.5f) / size * _rows - row;
            var distanceY = Math.Min(fy, 1f - fy);

            for (var x = 0; x < size; x++)
            {
                var along = (x + 0.5f) / size * _columns - _offset * row;
                var brick = MathF.Floor(along);
                var fx = along - brick;
                var column = (((int)brick % _columns) + _columns) % _columns;
                var distance = Math.Min(Math.Min(fx, 1f - fx) * widthInHeights, distanceY);
                float value;

                if (distance < halfGap)
                {
                    value = 0f;
                }
                else if (_bevel > 0f)
                {
                    value = Math.Min((distance - halfGap) / _bevel, 1f);
                }
                else
                {
                    value = 1f;
                }

                if (_heightVariation != 0f)
                {
                    value *= 1f - _heightVariation * Hash.Unit(Seed, column, row);
                }

                output[y * size + x] = value;
            }
        });
    }
}
