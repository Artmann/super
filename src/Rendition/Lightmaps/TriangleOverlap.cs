using System.Runtime.CompilerServices;

namespace Rendition;

/// <summary>
/// Finds triangles, laid out in 2D with three corners each in order, that overlap. Charting runs this for every chart
/// each time it is cut, so the clipping is inlined into one loop: as separate calls it made charting about a tenth
/// slower.
/// </summary>
internal static class TriangleOverlap
{
    /// <summary>
    /// Of every two triangles whose shared area is more than <paramref name="minArea"/>, the pair whose later triangle
    /// comes first, with the middle of their shared area; -1 for both when none. A grid over the triangles' bounds
    /// finds the candidates, so this is close to linear in the triangles.
    /// </summary>
    public static (int A, int B, double X, double Y) First(double[] xs, double[] ys, int count, double minArea)
    {
        if (count < 2 || TriangleGrid.Over(xs, ys, count) is not { } grid)
        {
            return (-1, -1, 0.0, 0.0);
        }

        return grid.FirstOverlap(xs, ys, minArea);
    }

    /// <summary>The two triangles, the earlier first, with the middle of their shared area, when it is more than <paramref name="minArea"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int A, int B, double X, double Y)? Overlap(double[] xs, double[] ys, int a, int b, double minArea)
    {
        var (area, middleX, middleY) = SharedArea(xs, ys, a * 3, b * 3);
        if (area > minArea)
        {
            return (Math.Min(a, b), Math.Max(a, b), middleX, middleY);
        }

        return null;
    }

    /// <summary>
    /// The area two triangles share and its middle, by clipping one to the other (Sutherland-Hodgman). Triangles
    /// that only touch share none.
    /// </summary>
    private static (double Area, double X, double Y) SharedArea(double[] xs, double[] ys, int a, int b)
    {
        if (BoundsApart(xs, ys, a, b))
        {
            return (0.0, 0.0, 0.0);
        }

        double clipArea = UnwrapMath.SignedArea(xs, ys, b);
        if (clipArea == 0.0 || UnwrapMath.SignedArea(xs, ys, a) == 0.0)
        {
            return (0.0, 0.0, 0.0);
        }

        var polygon = new Polygon(stackalloc double[12], stackalloc double[12], stackalloc double[12], stackalloc double[12]);
        polygon.Start(xs, ys, a);
        double sign = clipArea > 0.0 ? 1.0 : -1.0;
        for (int e = 0; e < 3 && polygon.Count > 0; e++)
        {
            double ex = xs[b + e];
            double ey = ys[b + e];
            polygon.Clip(ex, ey, xs[b + (e + 1) % 3] - ex, ys[b + (e + 1) % 3] - ey, sign);
        }

        return polygon.Middle();
    }

    /// <summary>Whether the triangles' bounding boxes meet at most along an edge.</summary>
    private static bool BoundsApart(double[] xs, double[] ys, int a, int b) =>
        UnwrapMath.Max3(xs, a) <= UnwrapMath.Min3(xs, b)
        || UnwrapMath.Max3(xs, b) <= UnwrapMath.Min3(xs, a)
        || UnwrapMath.Max3(ys, a) <= UnwrapMath.Min3(ys, b)
        || UnwrapMath.Max3(ys, b) <= UnwrapMath.Min3(ys, a);

    /// <summary>A polygon of up to 12 corners, clipped one edge at a time.</summary>
    private ref struct Polygon(Span<double> xs, Span<double> ys, Span<double> nextXs, Span<double> nextYs)
    {
        private readonly Span<double> xs = xs;
        private readonly Span<double> ys = ys;
        private readonly Span<double> nextXs = nextXs;
        private readonly Span<double> nextYs = nextYs;

        public int Count { get; private set; }

        /// <summary>Starts as the triangle whose three corners start at <paramref name="at"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Start(double[] cornerXs, double[] cornerYs, int at)
        {
            Count = 3;
            for (int i = 0; i < 3; i++)
            {
                xs[i] = cornerXs[at + i];
                ys[i] = cornerYs[at + i];
            }
        }

        /// <summary>
        /// Keeps the part on the inner side of the edge from (ex, ey) along (dx, dy), where the inside is on the left
        /// when <paramref name="sign"/> is 1 and on the right when it is -1.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clip(double ex, double ey, double dx, double dy, double sign)
        {
            int m = 0;
            for (int i = 0; i < Count; i++)
            {
                int j = (i + 1) % Count;
                double si = sign * (dx * (ys[i] - ey) - dy * (xs[i] - ex));
                double sj = sign * (dx * (ys[j] - ey) - dy * (xs[j] - ex));
                if (si >= 0.0)
                {
                    nextXs[m] = xs[i];
                    nextYs[m] = ys[i];
                    m++;
                }

                if ((si >= 0.0) != (sj >= 0.0))
                {
                    double t = si / (si - sj);
                    nextXs[m] = xs[i] + (xs[j] - xs[i]) * t;
                    nextYs[m] = ys[i] + (ys[j] - ys[i]) * t;
                    m++;
                }
            }

            Count = m;
            nextXs[..m].CopyTo(xs);
            nextYs[..m].CopyTo(ys);
        }

        /// <summary>The polygon's area and middle, or zeros when it has none.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly (double Area, double X, double Y) Middle()
        {
            double twice = 0.0;
            double cx = 0.0;
            double cy = 0.0;
            for (int i = 0; i < Count; i++)
            {
                int j = (i + 1) % Count;
                double c = xs[i] * ys[j] - xs[j] * ys[i];
                twice += c;
                cx += (xs[i] + xs[j]) * c;
                cy += (ys[i] + ys[j]) * c;
            }

            double area = Math.Abs(twice) * 0.5;
            if (area <= 0.0)
            {
                return (0.0, 0.0, 0.0);
            }

            return (area, cx / (3.0 * twice), cy / (3.0 * twice));
        }
    }

    /// <summary>
    /// A grid over the triangles' bounds, up to 1024 cells each way, with the triangles whose bounds reach each
    /// cell.
    /// </summary>
    private sealed class TriangleGrid
    {
        private readonly int columns;
        private readonly int rows;
        private readonly (int X0, int Y0, int X1, int Y1)[] range;
        private readonly int[] start;
        private readonly int[] inCell;

        private TriangleGrid(int columns, int rows, (int X0, int Y0, int X1, int Y1)[] range)
        {
            this.columns = columns;
            this.rows = rows;
            this.range = range;
            start = new int[columns * rows + 1];
            Spread(start, null);
            for (int i = 1; i < start.Length; i++)
            {
                start[i] += start[i - 1];
            }

            inCell = new int[start[^1]];
            Spread((int[])start.Clone(), inCell);
        }

        /// <summary>
        /// Walks every cell each triangle's bounds reach, in triangle order. Counts them into
        /// <c>cursor[cell + 1]</c>, or, given <paramref name="into"/>, puts the triangle at <c>into[cursor[cell]]</c> and
        /// moves that cursor on.
        /// </summary>
        private void Spread(int[] cursor, int[]? into)
        {
            for (int t = 0; t < range.Length; t++)
            {
                for (int y = range[t].Y0; y <= range[t].Y1; y++)
                {
                    for (int x = range[t].X0; x <= range[t].X1; x++)
                    {
                        int cell = y * columns + x;
                        if (into is null)
                        {
                            cursor[cell + 1]++;
                        }
                        else
                        {
                            into[cursor[cell]++] = t;
                        }
                    }
                }
            }
        }

        /// <summary>The grid over the triangles, or null when they all lie on one point.</summary>
        public static TriangleGrid? Over(double[] xs, double[] ys, int count)
        {
            double minX = UnwrapMath.Min(xs);
            double minY = UnwrapMath.Min(ys);
            double width = UnwrapMath.Max(xs) - minX;
            double height = UnwrapMath.Max(ys) - minY;
            double cell = Math.Sqrt(width * height / count);
            cell = Math.Max(cell, Math.Max(width, height) / 1024.0);
            if (cell <= 0.0)
            {
                return null;
            }

            int columns = Math.Clamp((int)(width / cell) + 1, 1, 1024);
            int rows = Math.Clamp((int)(height / cell) + 1, 1, 1024);
            var range = new (int X0, int Y0, int X1, int Y1)[count];
            for (int t = 0; t < count; t++)
            {
                range[t] = (
                    Math.Clamp((int)((UnwrapMath.Min3(xs, t * 3) - minX) / cell), 0, columns - 1),
                    Math.Clamp((int)((UnwrapMath.Min3(ys, t * 3) - minY) / cell), 0, rows - 1),
                    Math.Clamp((int)((UnwrapMath.Max3(xs, t * 3) - minX) / cell), 0, columns - 1),
                    Math.Clamp((int)((UnwrapMath.Max3(ys, t * 3) - minY) / cell), 0, rows - 1));
            }

            return new TriangleGrid(columns, rows, range);
        }

        /// <summary>
        /// Of every two triangles that overlap by more than <paramref name="minArea"/>, the pair whose later triangle
        /// comes first. Each two triangles whose bounds share a cell are tested once, in the first cell they share.
        /// </summary>
        public (int A, int B, double X, double Y) FirstOverlap(double[] xs, double[] ys, double minArea)
        {
            var best = (-1, -1, 0.0, 0.0);
            int bestLater = int.MaxValue;
            for (int c = 0; c < columns * rows; c++)
            {
                var cell = (X: c % columns, Y: c / columns);
                for (int i = start[c]; i < start[c + 1]; i++)
                {
                    for (int j = i + 1; j < start[c + 1]; j++)
                    {
                        int a = inCell[i];
                        int b = inCell[j];
                        if (Math.Max(a, b) < bestLater && FirstShared(cell, a, b) && Overlap(xs, ys, a, b, minArea) is { } found)
                        {
                            bestLater = Math.Max(a, b);
                            best = found;
                        }
                    }
                }
            }

            return best;
        }

        /// <summary>Whether the cell is the first that both triangles' bounds reach.</summary>
        private bool FirstShared((int X, int Y) cell, int a, int b) =>
            cell.X == Math.Max(range[a].X0, range[b].X0) && cell.Y == Math.Max(range[a].Y0, range[b].Y0);
    }
}
