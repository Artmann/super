using System.Numerics;

namespace Super.Physics;

/// <summary>The number checks physics settings share. NaN and infinity pass none of them.</summary>
internal static class Numbers
{
    /// <summary>Whether the value is a finite number above 0.</summary>
    public static bool IsAboveZero(float value) => value > 0f && float.IsFinite(value);

    /// <summary>Whether the value is a finite number of at least 0.</summary>
    public static bool IsZeroOrMore(float value) => value >= 0f && float.IsFinite(value);

    /// <summary>Whether all three parts are finite.</summary>
    public static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>Throws, naming the argument <paramref name="name"/>, unless all three parts are finite.</summary>
    public static void RequireFinite(Vector3 value, string name)
    {
        if (!IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, value, $"The {name} must be finite.");
        }
    }
}
