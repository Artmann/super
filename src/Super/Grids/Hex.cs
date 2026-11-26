namespace Super;

/// <summary>
/// A hexagon on a <see cref="HexGrid"/>, in axial coordinates: <see cref="Q"/> runs along one row of hexes and
/// <see cref="R"/> across them, and the third cube coordinate <see cref="S"/> is what keeps the three summing to 0.
/// These are the coordinates of the Red Blob Games hexagon guide. Hexes compare and hash by value. See docs/grids.md.
/// </summary>
public readonly record struct Hex(int Q, int R)
{
    private static readonly Hex[] _sides = [new(1, 0), new(1, -1), new(0, -1), new(-1, 0), new(-1, 1), new(0, 1)];

    /// <summary>The third cube coordinate, −Q − R.</summary>
    public int S => -Q - R;

    /// <summary>The six steps to the hexes that share a side, each one sixth of a turn on from the last.</summary>
    public static ReadOnlySpan<Hex> Sides => _sides;

    public static Hex operator +(Hex a, Hex b) => new(a.Q + b.Q, a.R + b.R);

    public static Hex operator -(Hex a, Hex b) => new(a.Q - b.Q, a.R - b.R);

    /// <summary>How many steps across sides apart two hexes are.</summary>
    public static int Distance(Hex a, Hex b)
    {
        var apart = a - b;
        return Math.Max(Math.Abs(apart.Q), Math.Max(Math.Abs(apart.R), Math.Abs(apart.S)));
    }

    /// <summary>
    /// The hex a point in axial coordinates lies in, such as one partway along a line. Rounds all three cube
    /// coordinates and mends the one that moved most, so the answer always sums to 0.
    /// </summary>
    public static Hex Round(float q, float r)
    {
        float s = -q - r;
        float roundedQ = MathF.Round(q);
        float roundedR = MathF.Round(r);
        float roundedS = MathF.Round(s);
        float movedQ = MathF.Abs(roundedQ - q);
        float movedR = MathF.Abs(roundedR - r);
        float movedS = MathF.Abs(roundedS - s);
        if (movedQ > movedR && movedQ > movedS)
        {
            return new Hex((int)(-roundedR - roundedS), (int)roundedR);
        }

        if (movedR > movedS)
        {
            return new Hex((int)roundedQ, (int)(-roundedQ - roundedS));
        }

        return new Hex((int)roundedQ, (int)roundedR);
    }

    /// <summary>
    /// The hexes a straight line from the middle of <paramref name="from"/> to the middle of <paramref name="to"/>
    /// passes through, from one end to the other, each sharing a side with the last. There is one more hex than
    /// <see cref="Distance"/> steps.
    /// </summary>
    public static IEnumerable<Hex> Line(Hex from, Hex to)
    {
        int steps = Distance(from, to);
        for (int step = 0; step <= steps; step++)
        {
            float t = steps == 0 ? 0f : step / (float)steps;
            // A nudge keeps a line that runs exactly along an edge from flipping between the hexes either side.
            yield return Round(from.Q + (to.Q - from.Q) * t + 1e-4f, from.R + (to.R - from.R) * t + 1e-4f);
        }
    }

    /// <summary>Every hex at most <paramref name="steps"/> from <paramref name="center"/>, a big hexagon of them.</summary>
    public static IEnumerable<Hex> Range(Hex center, int steps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        for (int q = -steps; q <= steps; q++)
        {
            for (int r = Math.Max(-steps, -q - steps); r <= Math.Min(steps, -q + steps); r++)
            {
                yield return new Hex(center.Q + q, center.R + r);
            }
        }
    }

    /// <summary>
    /// The hexes exactly <paramref name="steps"/> from <paramref name="center"/>, six times as many as the steps, going
    /// round the same way as <see cref="Sides"/>. A ring of 0 steps is the centre alone.
    /// </summary>
    public static IEnumerable<Hex> Ring(Hex center, int steps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        if (steps == 0)
        {
            yield return center;
            yield break;
        }

        // Starts out along the fifth side and walks the ring's six edges, each one a side on from the last.
        var hex = new Hex(center.Q + _sides[4].Q * steps, center.R + _sides[4].R * steps);
        for (int edge = 0; edge < 6; edge++)
        {
            for (int i = 0; i < steps; i++)
            {
                yield return hex;
                hex += _sides[edge];
            }
        }
    }

    /// <summary>Every hex at most <paramref name="steps"/> from <paramref name="center"/>, ring by ring outwards.</summary>
    public static IEnumerable<Hex> Spiral(Hex center, int steps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        for (int ring = 0; ring <= steps; ring++)
        {
            foreach (var hex in Ring(center, ring))
            {
                yield return hex;
            }
        }
    }

    /// <summary>
    /// Turns a hex round another by sixths of a turn, the same way as <see cref="System.Numerics.Quaternion"/> turns
    /// about +Y by a positive angle, on a flat-top or a pointy-top grid alike. Negative turns go the other way.
    /// </summary>
    public static Hex Rotate(Hex hex, Hex around, int sixthTurns)
    {
        var offset = hex - around;
        for (int turn = 0; turn < ((sixthTurns % 6) + 6) % 6; turn++)
        {
            offset = new Hex(-offset.S, -offset.Q);
        }

        return around + offset;
    }
}
