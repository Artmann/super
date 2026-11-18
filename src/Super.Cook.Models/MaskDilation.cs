using System.Numerics;

namespace Super.Cook.Models;

/// <summary>Bleeds a baked mask's islands out over the empty texels around them, so filtering doesn't pull in the background.</summary>
internal static class MaskDilation
{
    /// <summary>How many pixels each island bleeds out past its edge.</summary>
    private const int Dilation = 8;

    /// <summary>Each empty texel beside a filled one takes their average, a ring at a time.</summary>
    public static void Dilate(byte[] rgba, bool[] covered, int size)
    {
        var filled = (bool[])covered.Clone();
        for (int ring = 0; ring < Dilation; ring++)
        {
            var next = (bool[])filled.Clone();
            for (int texel = 0; texel < size * size; texel++)
            {
                if (filled[texel] || NeighbourAverage(rgba, filled, size, texel) is not { } average)
                {
                    continue;
                }

                int i = texel * 4;
                rgba[i] = (byte)MathF.Round(average.X);
                rgba[i + 1] = (byte)MathF.Round(average.Y);
                rgba[i + 2] = (byte)MathF.Round(average.Z);
                rgba[i + 3] = (byte)MathF.Round(average.W);
                next[texel] = true;
            }

            filled = next;
        }
    }

    /// <summary>The average of a texel's filled neighbours, the eight around it, or null when none is filled.</summary>
    private static Vector4? NeighbourAverage(byte[] rgba, bool[] filled, int size, int texel)
    {
        int x = texel % size;
        int y = texel / size;
        int count = 0;
        var sum = Vector4.Zero;
        for (int ny = Math.Max(y - 1, 0); ny <= Math.Min(y + 1, size - 1); ny++)
        {
            for (int nx = Math.Max(x - 1, 0); nx <= Math.Min(x + 1, size - 1); nx++)
            {
                if (filled[ny * size + nx])
                {
                    int n = (ny * size + nx) * 4;
                    sum += new Vector4(rgba[n], rgba[n + 1], rgba[n + 2], rgba[n + 3]);
                    count++;
                }
            }
        }

        return count > 0 ? sum / count : null;
    }
}
