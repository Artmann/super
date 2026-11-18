using System.Numerics;
using Rendition;

namespace Super;

/// <summary>The checks on what an emitter's nodes are given, each throwing with the reason, or giving the value back.</summary>
internal static class ParticleChecks
{
    public static int Count(int count) =>
        count is >= 1 and <= Particle.MaxPerFrame
            ? count
            : throw new ArgumentOutOfRangeException(nameof(count), count, $"An emitter needs 1 to {Particle.MaxPerFrame} particles.");

    public static float Lifetime(float lifetime) =>
        lifetime > 0f && float.IsFinite(lifetime)
            ? lifetime
            : throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "A particle's lifetime must be more than zero seconds.");

    public static float Positive(float value, string name) =>
        value > 0f && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, $"{name} must be more than zero.");

    public static float NotNegative(float value, string name) =>
        value >= 0f && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, $"{name} can't be negative.");

    public static Vector3 NotNegative(Vector3 value, string name) =>
        value.X >= 0f && value.Y >= 0f && value.Z >= 0f && float.IsFinite(value.X + value.Y + value.Z)
            ? value
            : throw new ArgumentOutOfRangeException(name, value, "A color can't have a negative channel.");

    public static float Fraction(float value, string name, float most) =>
        value >= 0f && value <= most ? value : throw new ArgumentOutOfRangeException(name, value, $"{name} must be from 0 to {most}.");

    public static float FiniteValue(float value, string name) =>
        float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, $"{name} must be a real number.");

    public static Vector3 Finite(Vector3 value, string name) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            ? value
            : throw new ArgumentOutOfRangeException(name, value, $"{name} must be a real number on every axis.");
}
