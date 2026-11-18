namespace Rendition;

/// <summary>Turns a flat chart so its bounding rectangle is the smallest, by its convex hull.</summary>
internal static class SmallestRectangle
{
    /// <summary>
    /// Turns the points about the origin so their bounding rectangle is the smallest, which lines it up with one of
    /// the edges of their convex hull.
    /// </summary>
    public static void Turn(double[] xs, double[] ys)
    {
        var hull = Hull(xs, ys);
        if (hull.Count < 3)
        {
            return;
        }

        double bestArea = double.MaxValue;
        double bestCos = 1.0;
        double bestSin = 0.0;
        for (int e = 0; e < hull.Count; e++)
        {
            var (ax, ay) = hull[e];
            var (bx, by) = hull[(e + 1) % hull.Count];
            double length = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            if (length <= 0.0)
            {
                continue;
            }

            double c = (bx - ax) / length;
            double s = (by - ay) / length;
            double area = TurnedArea(hull, c, s);
            if (area < bestArea * (1.0 - 1e-9))
            {
                bestArea = area;
                bestCos = c;
                bestSin = s;
            }
        }

        for (int i = 0; i < xs.Length; i++)
        {
            double u = xs[i] * bestCos + ys[i] * bestSin;
            double v = -xs[i] * bestSin + ys[i] * bestCos;
            xs[i] = u;
            ys[i] = v;
        }
    }

    /// <summary>The area of the points' bounding rectangle once turned by the angle with this cosine and sine.</summary>
    private static double TurnedArea(List<(double X, double Y)> points, double c, double s)
    {
        double minU = double.MaxValue;
        double maxU = double.MinValue;
        double minV = double.MaxValue;
        double maxV = double.MinValue;
        foreach (var (px, py) in points)
        {
            double u = px * c + py * s;
            double v = -px * s + py * c;
            minU = Math.Min(minU, u);
            maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v);
            maxV = Math.Max(maxV, v);
        }

        return (maxU - minU) * (maxV - minV);
    }

    /// <summary>The convex hull, counter-clockwise, by Andrew's monotone chain.</summary>
    private static List<(double X, double Y)> Hull(double[] xs, double[] ys)
    {
        var points = new (double X, double Y)[xs.Length];
        for (int i = 0; i < xs.Length; i++)
        {
            points[i] = (xs[i], ys[i]);
        }

        Array.Sort(points);
        var hull = new List<(double X, double Y)>(points.Length + 1);
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            for (int k = 0; k < points.Length; k++)
            {
                var p = pass == 0 ? points[k] : points[points.Length - 1 - k];
                while (hull.Count >= start + 2 && Turning(hull[^2], hull[^1], p) <= 0.0)
                {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }

    private static double Turning((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
}
