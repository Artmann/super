namespace Super.Materials;

/// <summary>Argument checks that name the parameter and the range, so a mistake in a graph explains itself.</summary>
internal static class Arguments
{
    public static void AtLeast(int value, int minimum, string name)
    {
        if (value < minimum)
        {
            throw new ArgumentOutOfRangeException(name, $"{name} must be at least {minimum}, not {value}.");
        }
    }

    public static void AtLeast(float value, float minimum, string name)
    {
        if (!(value >= minimum))
        {
            var text = FormattableString.Invariant($"{name} must be at least {minimum}, not {value}.");

            throw new ArgumentOutOfRangeException(name, text);
        }
    }

    public static void MoreThan(float value, float minimum, string name)
    {
        if (!(value > minimum))
        {
            var text = FormattableString.Invariant($"{name} must be more than {minimum}, not {value}.");

            throw new ArgumentOutOfRangeException(name, text);
        }
    }

    public static void MoreThanUpTo(float value, float minimum, float maximum, string name)
    {
        if (!(value > minimum && value <= maximum))
        {
            var text = FormattableString.Invariant($"{name} must be more than {minimum} and at most {maximum}, not {value}.");

            throw new ArgumentOutOfRangeException(name, text);
        }
    }

    public static void Finite(float value, string name)
    {
        if (!float.IsFinite(value))
        {
            var text = FormattableString.Invariant($"{name} must be a finite number, not {value}.");

            throw new ArgumentOutOfRangeException(name, text);
        }
    }

    public static void Between(float value, float minimum, float maximum, string name)
    {
        if (!(value >= minimum && value <= maximum))
        {
            var text = FormattableString.Invariant($"{name} must be from {minimum} to {maximum}, not {value}.");

            throw new ArgumentOutOfRangeException(name, text);
        }
    }

    public static void Between(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"{name} must be from {minimum} to {maximum}, not {value}.");
        }
    }
}
