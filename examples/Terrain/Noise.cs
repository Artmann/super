using Super;

namespace Terrain;

/// <summary>
/// Smooth random values over a plane, from a seeded lattice. Every call with the same
/// point gives the same answer, so a seed replays the same ground.
/// </summary>
internal sealed class Noise
{
    private const int Size = 256;
    private const int Mask = Size - 1;

    private readonly float[] _values = new float[Size * Size];

    public Noise(RandomStream stream)
    {
        for (int i = 0; i < _values.Length; i++)
        {
            _values[i] = stream.Float();
        }
    }

    /// <summary>A value from 0 to 1 that changes smoothly, with a bump about every unit.</summary>
    public float Sample(float x, float z)
    {
        int cellX = (int)MathF.Floor(x);
        int cellZ = (int)MathF.Floor(z);
        float fx = Smooth(x - cellX);
        float fz = Smooth(z - cellZ);

        float top = Lerp(Lattice(cellX, cellZ), Lattice(cellX + 1, cellZ), fx);
        float bottom = Lerp(Lattice(cellX, cellZ + 1), Lattice(cellX + 1, cellZ + 1), fx);
        return Lerp(top, bottom, fz);
    }

    /// <summary>Several octaves added up, each twice as fine and half as strong. From 0 to 1.</summary>
    public float Layered(float x, float z, int octaves)
    {
        float sum = 0f;
        float strength = 1f;
        float total = 0f;
        for (int octave = 0; octave < octaves; octave++)
        {
            sum += Sample(x, z) * strength;
            total += strength;
            x = x * 2f + 17.3f;
            z = z * 2f + 31.7f;
            strength *= 0.5f;
        }

        return sum / total;
    }

    private float Lattice(int x, int z) => _values[(z & Mask) * Size + (x & Mask)];

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
