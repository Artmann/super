using System.Numerics;

namespace Super;

/// <summary>One column of an extrusion: where it is in the outline, which way it faces, which way the outline runs there, and its u.</summary>
internal readonly record struct OutlineCorner(Vector2 Point, Vector2 Normal, Vector2 Across, float U);

/// <summary>The outline side of <see cref="Shapes.Extrude"/>: checking an outline and cutting it into strips.</summary>
internal static class SplineSweep
{
    /// <summary>
    /// The columns of an extrusion, as strips laid side by side. A sharp outline has one
    /// strip per edge, each with its own flat normal; a smooth one is a single strip.
    /// </summary>
    public static List<OutlineCorner[]> Strips(IReadOnlyList<Vector2> outline, bool closed, bool smooth)
    {
        int count = outline.Count;
        int edges = closed ? count : count - 1;
        var directions = new Vector2[edges];
        var normals = new Vector2[edges];
        for (int i = 0; i < edges; i++)
        {
            directions[i] = Vector2.Normalize(outline[(i + 1) % count] - outline[i]);

            // To the right of the way the edge runs.
            normals[i] = new Vector2(directions[i].Y, -directions[i].X);
        }

        return smooth ? [SmoothStrip(outline, closed, directions, normals)] : SharpStrips(outline, directions, normals);
    }

    /// <summary>Throws unless the outline has enough points, all finite, and no two in a row in the same place.</summary>
    public static void CheckOutline(IReadOnlyList<Vector2> outline, bool closed)
    {
        int least = closed ? 3 : 2;
        if (outline.Count < least)
        {
            throw new ArgumentException(closed ? "A closed outline needs at least three points." : "An outline needs at least two points.", nameof(outline));
        }

        for (int i = 0; i < outline.Count; i++)
        {
            if (!float.IsFinite(outline[i].X) || !float.IsFinite(outline[i].Y))
            {
                throw new ArgumentException($"Outline point {i} must be finite.", nameof(outline));
            }

            var next = outline[(i + 1) % outline.Count];
            if ((i + 1 < outline.Count || closed) && next == outline[i])
            {
                throw new ArgumentException($"Outline points {i} and {(i + 1) % outline.Count} are in the same place.", nameof(outline));
            }
        }
    }

    /// <summary>One strip for each edge, so each edge keeps its own flat normal.</summary>
    private static List<OutlineCorner[]> SharpStrips(IReadOnlyList<Vector2> outline, Vector2[] directions, Vector2[] normals)
    {
        int count = outline.Count;
        var strips = new List<OutlineCorner[]>();
        float u = 0f;
        for (int i = 0; i < directions.Length; i++)
        {
            var from = outline[i];
            var to = outline[(i + 1) % count];
            float length = Vector2.Distance(from, to);
            strips.Add([new OutlineCorner(from, normals[i], directions[i], u), new OutlineCorner(to, normals[i], directions[i], u + length)]);
            u += length;
        }

        return strips;
    }

    /// <summary>One strip round the whole outline, each point facing between the edges either side of it.</summary>
    private static OutlineCorner[] SmoothStrip(IReadOnlyList<Vector2> outline, bool closed, Vector2[] directions, Vector2[] normals)
    {
        int count = outline.Count;
        int edges = directions.Length;
        float u = 0f;
        var corners = new OutlineCorner[closed ? count + 1 : count];
        for (int i = 0; i < corners.Length; i++)
        {
            int point = i % count;
            int before = closed ? (point - 1 + edges) % edges : Math.Max(point - 1, 0);
            int after = closed ? point % edges : Math.Min(point, edges - 1);
            var normal = Vector2.Normalize(normals[before] + normals[after]);
            var across = Vector2.Normalize(directions[before] + directions[after]);
            if (i > 0)
            {
                u += Vector2.Distance(outline[(i - 1) % count], outline[point]);
            }

            corners[i] = new OutlineCorner(outline[point], normal, across, u);
        }

        return corners;
    }
}
