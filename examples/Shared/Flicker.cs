using System.Numerics;
using Super;

namespace Examples;

/// <summary>
/// A fire's light that flickers: its <see cref="PointLight"/> takes <paramref name="Color"/> times a brightness that
/// wavers round 1, by <paramref name="Depth"/> at <paramref name="Speed"/>, and by a faster <paramref name="Ripple"/>
/// on top when there is one. The Crypt and the Gallery share it: this file is linked into both.
/// </summary>
internal readonly record struct Flicker(Vector3 Color, float Depth, float Speed, float Phase, float Ripple = 0f, float RippleSpeed = 0f, float RipplePhase = 0f)
{
    /// <summary>The light's color at <paramref name="seconds"/> of game time.</summary>
    public Vector3 At(float seconds) =>
        Color * (1f + (Depth * MathF.Sin((seconds * Speed) + Phase)) + (Ripple * MathF.Sin((seconds * RippleSpeed) + RipplePhase)));

    /// <summary>Sets every flickering light in <paramref name="world"/> to its color at <paramref name="seconds"/>.</summary>
    public static void Update(World world, float seconds)
    {
        foreach (var row in world.Query<Flicker, PointLight>())
        {
            row.Second = row.Second with { Color = row.First.At(seconds) };
        }
    }
}
