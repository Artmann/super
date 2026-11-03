using System.Numerics;
using Rendition;

namespace Super;

/// <summary>Meshes made along a <see cref="Spline"/>: roads, rails, pipes and rivers, and models bent to follow one.</summary>
public static partial class Shapes
{
    /// <summary>
    /// Sweeps an outline along a spline, as a road, a rail, a wall or a river bed. The
    /// outline is in metres, seen looking along the spline from its start: x to the right
    /// and y up. Each edge faces to the right of the way it runs, so an outline going
    /// anticlockwise faces outwards, and a flat road listed from right to left faces up.
    /// A closed outline joins its last point back to its first. Rings are placed every
    /// <paramref name="step"/> metres, and UVs are in metres: u across the outline and v
    /// along the spline. Edges are sharp across the outline unless it's
    /// <paramref name="smooth"/>, as for a pipe. The ends are left open.
    /// </summary>
    public static GeneratedMesh Extrude(
        Spline spline,
        IReadOnlyList<Vector2> outline,
        bool closedOutline = false,
        bool smooth = false,
        float step = 0.5f,
        Surface? surface = null,
        float uvPerMetre = 1f)
    {
        ArgumentNullException.ThrowIfNull(spline);
        ArgumentNullException.ThrowIfNull(outline);
        CheckOutline(outline, closedOutline);
        RequirePositive(step, nameof(step), "step");
        RequirePositive(uvPerMetre, nameof(uvPerMetre), "UV scale");

        int rings = Math.Max(1, (int)MathF.Ceiling(spline.Length / step - 1e-4f));
        var poses = new SplinePose[rings + 1];
        var along = new float[rings + 1];
        for (int ring = 0; ring <= rings; ring++)
        {
            along[ring] = spline.Length * ring / rings;
            poses[ring] = spline.FrameAtDistance(along[ring]);
        }

        var builder = new ShapeBuilder();
        foreach (var strip in Strips(outline, closedOutline, smooth))
        {
            builder.Grid(strip.Length - 1, rings, (column, row) =>
            {
                var corner = strip[column];
                var pose = poses[row];
                var normal = Vector3.Transform(new Vector3(-corner.Normal.X, corner.Normal.Y, 0f), pose.Rotation);
                var across = Vector3.Transform(new Vector3(-corner.Across.X, corner.Across.Y, 0f), pose.Rotation);

                // The shaders build the bitangent as cross(normal, tangent) * w, pointing to where v falls.
                float handedness = Vector3.Dot(Vector3.Cross(normal, across), pose.Forward) > 0f ? -1f : 1f;
                return new Vertex(
                    pose.Position + Vector3.Transform(new Vector3(-corner.Point.X, corner.Point.Y, 0f), pose.Rotation),
                    normal,
                    new Vector4(across, handedness),
                    new Vector2(corner.U, along[row]) * uvPerMetre);
            });
        }

        return builder.Build(surface);
    }

    /// <summary>
    /// A flat strip along a spline, facing up, such as a road or a river. It's
    /// <paramref name="width"/> metres across, centred on the spline.
    /// </summary>
    public static GeneratedMesh Ribbon(Spline spline, float width, float step = 0.5f, Surface? surface = null, float uvPerMetre = 1f)
    {
        RequirePositive(width, nameof(width), "width");
        return Extrude(spline, [new Vector2(width * 0.5f, 0f), new Vector2(-width * 0.5f, 0f)], step: step, surface: surface, uvPerMetre: uvPerMetre);
    }

    /// <summary>A round pipe along a spline, for rails, cables, ropes and pipes. Sides go round it.</summary>
    public static GeneratedMesh Tube(Spline spline, float radius = 0.25f, int sides = 12, float step = 0.5f, Surface? surface = null, float uvPerMetre = 1f)
    {
        RequirePositive(radius, nameof(radius), "radius");
        RequireAtLeast(sides, 3, nameof(sides));

        var outline = new Vector2[sides];
        for (int i = 0; i < sides; i++)
        {
            var (sin, cos) = SinCos(2f * MathF.PI * i / sides);
            outline[i] = new Vector2(cos * radius, sin * radius);
        }

        return Extrude(spline, outline, closedOutline: true, smooth: true, step, surface, uvPerMetre);
    }

    /// <summary>
    /// Bends a mesh to follow a spline, such as a straight plank into a bridge or a pipe
    /// into a bend. The mesh's +z runs along the spline from its start, measured from the
    /// mesh's back end, and its x and y keep their place across it, so it keeps its length.
    /// Past the end of an open spline it carries on straight. Give it plenty of vertices
    /// along z, since only vertices move. Loaded models can't be bent, only generated meshes.
    /// </summary>
    public static GeneratedMesh Bend(GeneratedMesh mesh, Spline spline)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(spline);

        float back = float.MaxValue;
        foreach (var vertex in mesh.Vertices)
        {
            back = MathF.Min(back, vertex.Position.Z);
        }

        var vertices = new Vertex[mesh.Vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            var vertex = mesh.Vertices[i];
            float distance = vertex.Position.Z - back;
            var pose = spline.FrameAtDistance(distance);
            if (!spline.Closed && distance > spline.Length)
            {
                pose = pose with { Position = pose.Position + pose.Forward * (distance - spline.Length) };
            }

            var tangent = Vector3.Transform(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), pose.Rotation);
            vertices[i] = vertex with
            {
                Position = pose.Position + Vector3.Transform(vertex.Position with { Z = 0f }, pose.Rotation),
                Normal = Vector3.Transform(vertex.Normal, pose.Rotation),
                Tangent = new Vector4(tangent, vertex.Tangent.W),
            };
        }

        return mesh with { Vertices = vertices };
    }

    /// <summary>
    /// The columns of an extrusion, as strips laid side by side. A sharp outline has one
    /// strip per edge, each with its own flat normal; a smooth one is a single strip.
    /// </summary>
    private static List<OutlineCorner[]> Strips(IReadOnlyList<Vector2> outline, bool closed, bool smooth)
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

        var strips = new List<OutlineCorner[]>();
        float u = 0f;
        if (!smooth)
        {
            for (int i = 0; i < edges; i++)
            {
                var from = outline[i];
                var to = outline[(i + 1) % count];
                float length = Vector2.Distance(from, to);
                strips.Add([new OutlineCorner(from, normals[i], directions[i], u), new OutlineCorner(to, normals[i], directions[i], u + length)]);
                u += length;
            }

            return strips;
        }

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

        strips.Add(corners);
        return strips;
    }

    private static void CheckOutline(IReadOnlyList<Vector2> outline, bool closed)
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

    /// <summary>One column of an extrusion: where it is in the outline, which way it faces, which way the outline runs there, and its u.</summary>
    private readonly record struct OutlineCorner(Vector2 Point, Vector2 Normal, Vector2 Across, float U);
}
