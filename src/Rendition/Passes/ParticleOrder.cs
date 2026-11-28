using System.Numerics;

namespace Rendition.Passes;

/// <summary>A frame's particles in the order <see cref="ParticlePass"/> draws them, furthest first, and the room to sort them.</summary>
internal sealed class ParticleOrder
{
    private readonly float[] _keys = new float[ParticlePass.MaxParticles];
    private readonly int[] _order = new int[ParticlePass.MaxParticles];
    private readonly Particle[] _sorted = new Particle[ParticlePass.MaxParticles];
    private int _count;

    /// <summary>The particles the last <see cref="Sort"/> was given, furthest first.</summary>
    public ReadOnlySpan<Particle> Sorted => _sorted.AsSpan(0, _count);

    /// <summary>Sorts as many of <paramref name="particles"/> as fit, furthest first, and returns them.</summary>
    public ReadOnlySpan<Particle> Sort(ReadOnlySpan<Particle> particles, Vector3 camera, Vector3 forward)
    {
        var fitting = particles[..Math.Min(particles.Length, ParticlePass.MaxParticles)];
        SortBackToFront(fitting, (camera, forward), _keys, _order, _sorted);
        _count = fitting.Length;
        return Sorted;
    }

    /// <summary>
    /// Writes particles into a buffer furthest from the camera first, so each blends over
    /// the ones behind it. Distance is measured along the view, which suits both kinds of
    /// camera. Keys and order are scratch space at least as long as the particles.
    /// </summary>
    internal static void SortBackToFront(
        ReadOnlySpan<Particle> particles, (Vector3 Camera, Vector3 Forward) view, Span<float> keys, Span<int> order, Span<Particle> into)
    {
        var (camera, forward) = view;
        int count = particles.Length;
        var sortKeys = keys[..count];
        var sortOrder = order[..count];
        for (int i = 0; i < count; i++)
        {
            // Negated, so the furthest has the smallest key and comes first.
            sortKeys[i] = -Vector3.Dot(particles[i].Position - camera, forward);
            sortOrder[i] = i;
        }

        sortKeys.Sort(sortOrder);
        for (int i = 0; i < count; i++)
        {
            into[i] = particles[sortOrder[i]];
        }
    }
}
