namespace Super;

/// <summary>
/// How <see cref="DebugGrids.GridHeat{TCell}"/> colours a value: <see cref="Low"/> and below are <see cref="Cold"/>,
/// <see cref="High"/> and above are <see cref="Hot"/>, and values between blend from one to the other.
/// </summary>
public readonly record struct HeatScale(float Low, float High)
{
    /// <summary>The colour of <see cref="Low"/>. Blue unless set.</summary>
    public Color Cold { get; init; } = Color.Blue;

    /// <summary>The colour of <see cref="High"/>. Red unless set.</summary>
    public Color Hot { get; init; } = Color.Red;

    /// <summary>The colour of a value.</summary>
    public Color At(float value)
    {
        float t = Math.Clamp((value - Low) / (High - Low), 0f, 1f);
        return new Color(
            Cold.R + (Hot.R - Cold.R) * t,
            Cold.G + (Hot.G - Cold.G) * t,
            Cold.B + (Hot.B - Cold.B) * t,
            Cold.A + (Hot.A - Cold.A) * t);
    }

    internal void Check()
    {
        if (!(float.IsFinite(Low) && float.IsFinite(High) && High > Low))
        {
            throw new ArgumentException(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"A heat scale runs from a low number up to a higher one, but this one runs from {Low} to {High}."));
        }
    }
}
