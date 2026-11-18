using Super.Terrain;

namespace Terrain;

/// <summary>Endless hills made from noise: a broad swell with rolling hills on top.</summary>
internal sealed class Hills
{
    /// <summary>How far apart the ground's points are, in metres.</summary>
    public const float Spacing = 2f;

    private readonly Noise _ground;

    public Hills(Noise ground)
    {
        _ground = ground;
        Field = new Heightfield(Height, Spacing);
    }

    /// <summary>The hills as a heightfield, which makes the chunks' meshes.</summary>
    public Heightfield Field { get; }

    /// <summary>How high the hills are at a point.</summary>
    public float Height(float x, float z)
    {
        float swell = 10f * MathF.Sin(x * 0.011f) * MathF.Cos(z * 0.009f);
        float hills = (_ground.Layered(x * 0.0035f, z * 0.0035f, 5) - 0.45f) * 120f;
        return swell + hills;
    }
}
