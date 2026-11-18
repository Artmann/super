using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>Two triangles of one chart touching one texel while far apart on the surface, and how far.</summary>
internal readonly record struct FarShare(int A, int B, int X, int Y, double Distance);

/// <summary>
/// Finds two triangles of one chart that touch the same texel but are too far apart on the surface to be neighbours.
/// For each triangle it takes the point nearest the texel's middle and finds where that is on the surface. Neighbours
/// put those within a texel and a half of each other, so they are far apart when more than
/// <see cref="NeighbourReach"/> texels of either triangle's largest stretch lie between them. Triangles with no area,
/// on the surface or in the UVs, are left out.
/// </summary>
internal sealed class FarShareSearch
{
    /// <summary>
    /// Two triangles of one chart touching one texel are neighbours when their points there are within this many
    /// texels of the more stretched one's surface; neighbours are within about 1.5.
    /// </summary>
    private const double NeighbourReach = 4.0;

    private readonly Vector3[] positions;
    private readonly int[] chartOf;
    private readonly int size;

    /// <summary>Per corner, its UV in texels.</summary>
    private readonly double[] xs;
    private readonly double[] ys;

    /// <summary>Metres per texel along each triangle's most stretched direction, or NaN to leave it out.</summary>
    private readonly double[] stretch;

    private FarShareSearch(Vector3[] cornerPositions, Vector2[] cornerUvs, int[] chartOf, int size)
    {
        positions = cornerPositions;
        this.chartOf = chartOf;
        this.size = size;
        xs = new double[cornerUvs.Length];
        ys = new double[cornerUvs.Length];
        for (int corner = 0; corner < cornerUvs.Length; corner++)
        {
            xs[corner] = cornerUvs[corner].X * (double)size;
            ys[corner] = cornerUvs[corner].Y * (double)size;
        }

        var (_, zero) = UnwrapMesh.Areas(cornerPositions);
        stretch = new double[zero.Length];
        for (int t = 0; t < zero.Length; t++)
        {
            stretch[t] = zero[t] ? double.NaN : Stretch(t * 3);
        }
    }

    /// <summary>
    /// Every two triangles of one chart, A before B, that touch the same texel at <paramref name="size"/> but are too
    /// far apart on the surface to be neighbours, texel by texel, row by row; or only the first when
    /// <paramref name="firstOnly"/>.
    /// </summary>
    public static List<FarShare> Find(Vector3[] cornerPositions, Vector2[] cornerUvs, int[] chartOf, bool firstOnly, int size)
    {
        var search = new FarShareSearch(cornerPositions, cornerUvs, chartOf, size);
        var (start, inTexel) = search.TexelTriangles();
        var found = new List<FarShare>();
        for (int texel = 0; texel < size * size; texel++)
        {
            int count = start[texel + 1] - start[texel];
            if (count >= 2 && search.FindIn(texel, inTexel.AsSpan(start[texel], count), found, firstOnly))
            {
                break;
            }
        }

        return found;
    }

    /// <summary>The triangles that touch each texel, sorted by texel and within a texel by triangle.</summary>
    private (int[] Start, int[] Triangles) TexelTriangles()
    {
        var touches = new List<(int Bucket, int Item)>();
        var texels = new List<int>();
        for (int t = 0; t < stretch.Length; t++)
        {
            if (double.IsNaN(stretch[t]))
            {
                continue;
            }

            UnwrapMath.Touched(xs.AsSpan(t * 3, 3), ys.AsSpan(t * 3, 3), size, texels);
            foreach (int texel in CollectionsMarshal.AsSpan(texels))
            {
                touches.Add((texel, t));
            }
        }

        return UnwrapMath.Bucket(touches, size * size);
    }

    /// <summary>Adds the far shares among the triangles touching one texel; whether to stop, having found the first.</summary>
    private bool FindIn(int texel, ReadOnlySpan<int> triangles, List<FarShare> found, bool firstOnly)
    {
        int x = texel % size;
        int y = texel / size;
        for (int i = 0; i < triangles.Length; i++)
        {
            int a = triangles[i];
            var pointA = SurfacePoint(a * 3, x + 0.5, y + 0.5);
            for (int j = i + 1; j < triangles.Length; j++)
            {
                int b = triangles[j];
                if (chartOf[a] != chartOf[b])
                {
                    continue;
                }

                var pointB = SurfacePoint(b * 3, x + 0.5, y + 0.5);
                double distance = (pointA - pointB).Length();
                if (distance > NeighbourReach * Math.Max(stretch[a], stretch[b]))
                {
                    found.Add(new FarShare(a, b, x, y, distance));
                    if (firstOnly)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// How many metres one texel covers along the triangle's most stretched direction: the largest singular value of
    /// the map from its UVs, in texels, to its corners. NaN when it has no area in the UVs.
    /// </summary>
    private double Stretch(int at)
    {
        double e1x = xs[at + 1] - xs[at];
        double e1y = ys[at + 1] - ys[at];
        double e2x = xs[at + 2] - xs[at];
        double e2y = ys[at + 2] - ys[at];
        double det = e1x * e2y - e2x * e1y;
        if (Math.Abs(det) <= 1e-9)
        {
            return double.NaN;
        }

        var p0 = new Vector3D(positions[at]);
        var p1 = new Vector3D(positions[at + 1]) - p0;
        var p2 = new Vector3D(positions[at + 2]) - p0;

        // The map's columns: how far the surface moves per texel along u and along v.
        var ju = new Vector3D(p1.X * e2y - p2.X * e1y, p1.Y * e2y - p2.Y * e1y, p1.Z * e2y - p2.Z * e1y) / det;
        var jv = new Vector3D(p2.X * e1x - p1.X * e2x, p2.Y * e1x - p1.Y * e2x, p2.Z * e1x - p1.Z * e2x) / det;
        double uu = Vector3D.Dot(ju, ju);
        double uv = Vector3D.Dot(ju, jv);
        double vv = Vector3D.Dot(jv, jv);
        double half = (uu + vv) * 0.5;
        double largest = half + Math.Sqrt(Math.Max(0.0, half * half - (uu * vv - uv * uv)));
        return Math.Sqrt(largest);
    }

    /// <summary>The point on the surface where the triangle comes nearest, in its UVs, to (px, py).</summary>
    private Vector3D SurfacePoint(int at, double px, double py)
    {
        double ax = xs[at];
        double ay = ys[at];
        double e1x = xs[at + 1] - ax;
        double e1y = ys[at + 1] - ay;
        double e2x = xs[at + 2] - ax;
        double e2y = ys[at + 2] - ay;
        double det = e1x * e2y - e2x * e1y;
        double qx = px - ax;
        double qy = py - ay;
        double s = (qx * e2y - e2x * qy) / det;
        double t = (e1x * qy - qx * e1y) / det;

        var (w0, w1, w2) = s >= 0.0 && t >= 0.0 && s + t <= 1.0 ? (1.0 - s - t, s, t) : NearestOnEdges(at, px, py);
        return new Vector3D(positions[at]) * w0 + new Vector3D(positions[at + 1]) * w1 + new Vector3D(positions[at + 2]) * w2;
    }

    /// <summary>
    /// For a point outside the triangle in its UVs, the weights of its corners at the nearest point on its nearest
    /// edge.
    /// </summary>
    private (double W0, double W1, double W2) NearestOnEdges(int at, double px, double py)
    {
        double best = double.MaxValue;
        var weights = (1.0, 0.0, 0.0);
        for (int e = 0; e < 3; e++)
        {
            int i = e;
            int j = (e + 1) % 3;
            double dx = xs[at + j] - xs[at + i];
            double dy = ys[at + j] - ys[at + i];
            double lengthSquared = dx * dx + dy * dy;
            double along = lengthSquared > 0.0
                ? Math.Clamp(((px - xs[at + i]) * dx + (py - ys[at + i]) * dy) / lengthSquared, 0.0, 1.0)
                : 0.0;
            double nx = xs[at + i] + dx * along - px;
            double ny = ys[at + i] + dy * along - py;
            double distance = nx * nx + ny * ny;
            if (distance < best)
            {
                best = distance;
                weights = e switch
                {
                    0 => (1.0 - along, along, 0.0),
                    1 => (0.0, 1.0 - along, along),
                    _ => (along, 0.0, 1.0 - along),
                };
            }
        }

        return weights;
    }
}
