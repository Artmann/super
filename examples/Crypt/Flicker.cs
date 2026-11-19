using System.Numerics;

namespace Crypt;

/// <summary>
/// A fire's light that flickers: its <see cref="Super.PointLight"/> takes <paramref name="Color"/> times a brightness
/// that wavers round 1, by <paramref name="Depth"/> at <paramref name="Speed"/>, and by a faster
/// <paramref name="Ripple"/> on top when there is one.
/// </summary>
internal readonly record struct Flicker(Vector3 Color, float Depth, float Speed, float Phase, float Ripple = 0f, float RippleSpeed = 0f, float RipplePhase = 0f)
{
    /// <summary>The light's color at <paramref name="seconds"/> of game time.</summary>
    public Vector3 At(float seconds) =>
        Color * (1f + (Depth * MathF.Sin((seconds * Speed) + Phase)) + (Ripple * MathF.Sin((seconds * RippleSpeed) + RipplePhase)));
}
