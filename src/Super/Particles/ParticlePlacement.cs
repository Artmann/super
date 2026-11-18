using System.Numerics;

namespace Super;

/// <summary>
/// Where an emitter is drawn: the transform to the world, its offset in the effect, and how
/// much a line drawn from and to stretches the offset's and the shapes' x.
/// </summary>
internal readonly struct ParticlePlacement
{
    public ParticlePlacement(in Matrix4x4 toWorld, Vector3 offset, float stretch)
    {
        ToWorld = toWorld;
        EffectOffset = offset;
        LineStretch = stretch;
        MotionCentre = new ParticleFrame(
            Vector3.Transform(new Vector3(offset.X * stretch, offset.Y, offset.Z), toWorld),
            SafeNormalize(Vector3.TransformNormal(Vector3.UnitY, toWorld)));
    }

    public Matrix4x4 ToWorld { get; }

    public Vector3 EffectOffset { get; }

    public float LineStretch { get; }

    /// <summary>The emitter's middle and upright axis in the world, for the motions that move round them.</summary>
    public ParticleFrame MotionCentre { get; }

    /// <summary>A start from the shape, moved by the offset, with its x stretched.</summary>
    public Vector3 Stretched(Vector3 start)
    {
        var local = start + EffectOffset;
        local.X = (start.X + EffectOffset.X) * LineStretch;
        return local;
    }

    private static Vector3 SafeNormalize(Vector3 value) =>
        value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.UnitY;
}
