using System.Globalization;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>
/// For each texel of a page, the first triangle of a <see cref="UvSet"/> that touches it and its chart, for the checks
/// that two charts never share a texel and keep the gutter <see cref="ChartPage"/> leaves between them.
/// </summary>
internal sealed class TexelOwners
{
    private const int Gutter = ChartPage.Gutter;

    private readonly int size;
    private readonly int[] ownerTriangle;
    private readonly int[] ownerChart;

    /// <summary>A page <paramref name="size"/> texels square.</summary>
    public TexelOwners(int size)
    {
        this.size = size;
        ownerTriangle = new int[size * size];
        ownerChart = new int[size * size];
        Array.Fill(ownerChart, -1);
    }

    /// <summary>
    /// Gives every texel each triangle touches to the first triangle there. The first texel that triangles of two
    /// charts touch, as a sentence, or null when there is none.
    /// </summary>
    public string? Claim(UvSet set)
    {
        Span<double> xs = stackalloc double[3];
        Span<double> ys = stackalloc double[3];
        var texels = new List<int>();
        for (int triangle = 0; triangle < set.ChartOf.Length; triangle++)
        {
            for (int i = 0; i < 3; i++)
            {
                var uv = set.Uvs[triangle * 3 + i];
                xs[i] = uv.X * (double)size;
                ys[i] = uv.Y * (double)size;
            }

            UnwrapMath.Touched(xs, ys, size, texels);
            if (Claim(CollectionsMarshal.AsSpan(texels), triangle, set.ChartOf[triangle]) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>
    /// The first texel at which two charts have fewer than the gutter's empty texels between them, row by row, as a
    /// sentence, or null when there is none.
    /// </summary>
    public string? GutterProblem()
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int chart = ownerChart[y * size + x];
                int near = chart < 0 ? -1 : OtherChartNear(x, y, chart);
                if (near >= 0)
                {
                    int between = Math.Max(Math.Abs(near % size - x), Math.Abs(near / size - y)) - 1;
                    string texels = between == 1 ? "texel" : "texels";
                    return string.Create(CultureInfo.InvariantCulture,
                        $"Triangles {ownerTriangle[y * size + x]} and {ownerTriangle[near]} are in different charts with only {between} empty {texels} between them at texel ({x}, {y}), less than the {Gutter}-texel gutter.");
                }
            }
        }

        return null;
    }

    private string? Claim(ReadOnlySpan<int> texels, int triangle, int chart)
    {
        foreach (int texel in texels)
        {
            if (ownerChart[texel] < 0)
            {
                ownerChart[texel] = chart;
                ownerTriangle[texel] = triangle;
            }
            else if (ownerChart[texel] != chart)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"Triangles {ownerTriangle[texel]} and {triangle} overlap at texel ({texel % size}, {texel / size}).");
            }
        }

        return null;
    }

    /// <summary>The first texel within the gutter of (x, y), row by row, that another chart owns, or -1.</summary>
    private int OtherChartNear(int x, int y, int chart)
    {
        for (int dy = -Gutter; dy <= Gutter; dy++)
        {
            for (int dx = -Gutter; dx <= Gutter; dx++)
            {
                int nx = x + dx;
                int ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= size || ny >= size)
                {
                    continue;
                }

                int other = ownerChart[ny * size + nx];
                if (other >= 0 && other != chart)
                {
                    return ny * size + nx;
                }
            }
        }

        return -1;
    }
}
