using System.Numerics;

namespace Rendition;

/// <summary>Small sums and tests the lightmap unwrap and its check share.</summary>
internal static class UnwrapMath
{
    /// <summary>The same position with any -0 made 0, so they weld.</summary>
    public static Vector3 Clean(Vector3 p) => new(p.X + 0f, p.Y + 0f, p.Z + 0f);

    public static double Min(ReadOnlySpan<double> values)
    {
        double min = double.MaxValue;
        foreach (double value in values)
        {
            min = Math.Min(min, value);
        }

        return min;
    }

    public static double Max(ReadOnlySpan<double> values)
    {
        double max = double.MinValue;
        foreach (double value in values)
        {
            max = Math.Max(max, value);
        }

        return max;
    }

    /// <summary>The smallest of the three values from <paramref name="at"/>, one triangle's corners.</summary>
    public static double Min3(double[] values, int at) => Math.Min(values[at], Math.Min(values[at + 1], values[at + 2]));

    /// <summary>The largest of the three values from <paramref name="at"/>, one triangle's corners.</summary>
    public static double Max3(double[] values, int at) => Math.Max(values[at], Math.Max(values[at + 1], values[at + 2]));

    /// <summary>The signed area of the triangle whose three corners start at <paramref name="at"/>.</summary>
    public static double SignedArea(double[] xs, double[] ys, int at) =>
        ((xs[at + 1] - xs[at]) * (ys[at + 2] - ys[at]) - (xs[at + 2] - xs[at]) * (ys[at + 1] - ys[at])) * 0.5;

    /// <summary>
    /// Sorts items into buckets, keeping their order within each: bucket b holds <c>Items[Start[b]]</c> up to
    /// <c>Items[Start[b + 1]]</c>.
    /// </summary>
    public static (int[] Start, int[] Items) Bucket(List<(int Bucket, int Item)> entries, int bucketCount)
    {
        var start = new int[bucketCount + 1];
        foreach (var (bucket, _) in entries)
        {
            start[bucket + 1]++;
        }

        for (int i = 1; i < start.Length; i++)
        {
            start[i] += start[i - 1];
        }

        var fill = (int[])start.Clone();
        var items = new int[entries.Count];
        foreach (var (bucket, item) in entries)
        {
            items[fill[bucket]++] = item;
        }

        return (start, items);
    }

    /// <summary>
    /// Fills <paramref name="texels"/> with every texel, as y times <paramref name="size"/> plus x, that the triangle
    /// touches on a page <paramref name="size"/> texels square, row by row.
    /// </summary>
    public static void Touched(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, int size, List<int> texels)
    {
        texels.Clear();
        int x0 = Math.Clamp((int)Math.Floor(Min(xs)), 0, size - 1);
        int x1 = Math.Clamp((int)Math.Floor(Max(xs)), 0, size - 1);
        int y0 = Math.Clamp((int)Math.Floor(Min(ys)), 0, size - 1);
        int y1 = Math.Clamp((int)Math.Floor(Max(ys)), 0, size - 1);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (TouchesTexel(xs, ys, x, y))
                {
                    texels.Add(y * size + x);
                }
            }
        }
    }

    /// <summary>Whether the triangle touches texel (x, y), edges and corners included, by separating axes.</summary>
    private static bool TouchesTexel(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, int x, int y)
    {
        for (int e = 0; e < 3; e++)
        {
            double nx = -(ys[(e + 1) % 3] - ys[e]);
            double ny = xs[(e + 1) % 3] - xs[e];
            if (nx == 0.0 && ny == 0.0)
            {
                continue;
            }

            double triangleMin = double.MaxValue;
            double triangleMax = double.MinValue;
            for (int i = 0; i < 3; i++)
            {
                double d = xs[i] * nx + ys[i] * ny;
                triangleMin = Math.Min(triangleMin, d);
                triangleMax = Math.Max(triangleMax, d);
            }

            double boxMin = double.MaxValue;
            double boxMax = double.MinValue;
            for (int corner = 0; corner < 4; corner++)
            {
                double d = (x + (corner & 1)) * nx + (y + (corner >> 1)) * ny;
                boxMin = Math.Min(boxMin, d);
                boxMax = Math.Max(boxMax, d);
            }

            if (triangleMax < boxMin || boxMax < triangleMin)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A vector of doubles, for areas and flattening where floats lose too much.</summary>
internal readonly record struct Vector3D(double X, double Y, double Z)
{
    public Vector3D(Vector3 v)
        : this(v.X, v.Y, v.Z)
    {
    }

    public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vector3D operator /(Vector3D a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public static Vector3D operator *(Vector3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static double Dot(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vector3D Cross(Vector3D a, Vector3D b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public double Length() => Math.Sqrt(Dot(this, this));

    public Vector3D Normalized() => this / Length();
}
