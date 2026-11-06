namespace Super.Materials;

/// <summary>Argument checks that name the parameter and the range, so a mistake in a graph explains itself.</summary>
internal static class Arguments
{
    public static void AtLeast(int value, int minimum, string name)
    {
        if (value < minimum)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be at least {minimum}, not {value}.");
        }
    }

    public static void Between(float value, float minimum, float maximum, string name)
    {
        if (!(value >= minimum && value <= maximum))
        {
            var text = FormattableString.Invariant($"{name} must be from {minimum} to {maximum}, not {value}.");

            throw new ArgumentOutOfRangeException(name, value, text);
        }
    }

    public static void Between(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be from {minimum} to {maximum}, not {value}.");
        }
    }
}
