using System.Numerics;
using Rendition;
using static Super.ShapeChecks;

namespace Super;

/// <summary>
/// Ready-made meshes: boxes, spheres, capsules, cylinders, cones, planes, quads, tori and
/// prisms, and meshes made along a <see cref="Spline"/>: roads, rails, pipes and rivers, and
/// models bent to follow one. Hand one to <see cref="Content.Mesh"/> to draw it. Every shape is
/// centred on zero with y up, its triangles face outwards, and its normals, tangents and UVs are set.
/// Curved shapes are smooth; <see cref="Flat"/> gives any mesh hard, faceted edges.
/// UVs stretch once across each part unless <c>uvPerMetre</c> is given, which makes them
/// tile in metres instead, so a texture keeps its size whatever the shape's size. A surface
/// from <see cref="Content.Surface(string)"/> tiles at its material's width when
/// <c>uvPerMetre</c> isn't given.
/// </summary>
public static class Shapes
{
    /// <summary>The names of the built-in shapes, in the same order as <see cref="BuiltIn"/>.</summary>
    internal static readonly string[] BuiltInNames = ["sphere", "capsule", "cylinder", "cone", "plane", "quad", "torus"];

    /// <summary>A box with this width, height and depth. One texture covers each face.</summary>
    public static GeneratedMesh Box(Vector3 size, Surface? surface = null, float? uvPerMetre = null)
    {
        uvPerMetre = PerMetre(uvPerMetre, surface);
        RequirePositive(size.X, nameof(size), "width");
        RequirePositive(size.Y, nameof(size), "height");
        RequirePositive(size.Z, nameof(size), "depth");

        // Each face: its normal, the way its texture runs across, and the way that's up.
        ReadOnlySpan<(Vector3 Normal, Vector3 Across, Vector3 Up)> faces =
        [
            (Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY),
            (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ),
            (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY),
            (-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY),
        ];

        var builder = new ShapeBuilder();
        var half = size * 0.5f;
        foreach (var (normal, across, up) in faces)
        {
            float width = Math.Abs(Vector3.Dot(across, size));
            float height = Math.Abs(Vector3.Dot(up, size));
            var tangent = new Vector4(across, 1f);
            var uvScale = ShapeMath.Tiling(uvPerMetre, width, height);
            builder.AddGrid(1, 1, (column, row) => new Vertex(
                normal * half + across * ((column - 0.5f) * width) + up * ((0.5f - row) * height),
                normal,
                tangent,
                new Vector2(column, row) * uvScale));
        }

        return builder.ToMesh(surface);
    }

    /// <summary>
    /// A sphere. Segments go round the middle and rings go from pole to pole; more of
    /// either makes it rounder and costs more triangles.
    /// </summary>
    public static GeneratedMesh Sphere(float radius = 0.5f, int segments = 32, int rings = 16, Surface? surface = null, float? uvPerMetre = null)
    {
        uvPerMetre = PerMetre(uvPerMetre, surface);
        RequirePositive(radius, nameof(radius), "radius");
        RequireAtLeast(segments, 3, nameof(segments));
        RequireAtLeast(rings, 2, nameof(rings));

        var profile = new List<ProfilePoint>(rings + 1);
        ShapeMath.AddArc(profile, new ProfileArc(Vector2.Zero, radius, 0f, MathF.PI, rings), 0, rings);
        var builder = new ShapeBuilder();
        builder.Revolve(profile, segments, radius, uvPerMetre);
        return builder.ToMesh(surface);
    }

    /// <summary>
    /// The top half of a sphere, closed underneath, with its flat base at zero so it sits on
    /// the ground. Rings go from the top down to the base.
    /// </summary>
    public static GeneratedMesh Hemisphere(float radius = 0.5f, int segments = 32, int rings = 8, Surface? surface = null, float? uvPerMetre = null)
    {
        uvPerMetre = PerMetre(uvPerMetre, surface);
        RequirePositive(radius, nameof(radius), "radius");
        RequireAtLeast(segments, 3, nameof(segments));
        RequireAtLeast(rings, 1, nameof(rings));

        var profile = new List<ProfilePoint>(rings + 1);
        ShapeMath.AddArc(profile, new ProfileArc(Vector2.Zero, radius, 0f, MathF.PI, 2 * rings), 0, rings);
        var builder = new ShapeBuilder();
        builder.Revolve(profile, segments, radius, uvPerMetre);
        builder.AddDisc(0f, radius, segments, facingUp: false, uvPerMetre);
        return builder.ToMesh(surface);
    }

    /// <summary>
    /// A capsule standing up: a cylinder with a half sphere on each end, the usual stand-in
    /// for a person. The height is from end to end, so it must be at least twice the
    /// radius; at exactly twice it's a sphere. Rings are how many each end has.
    /// </summary>
    public static GeneratedMesh Capsule(float radius = 0.5f, float height = 2f, int segments = 32, int rings = 8, Surface? surface = null, float? uvPerMetre = null)
    {
        uvPerMetre = PerMetre(uvPerMetre, surface);
        RequirePositive(radius, nameof(radius), "radius");
        RequireAtLeast(segments, 3, nameof(segments));
        RequireAtLeast(rings, 1, nameof(rings));
        if (!(height >= 2f * radius))
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, FormattableString.Invariant($"A capsule's height is from end to end, so it must be at least twice its radius ({2f * radius})."));
        }

        float middle = (height - 2f * radius) * 0.5f;
        var profile = new List<ProfilePoint>(2 * rings + 2);
        ShapeMath.AddArc(profile, new ProfileArc(new Vector2(0f, middle), radius, 0f, MathF.PI, 2 * rings), 0, rings);
        float start = profile[^1].Distance + 2f * middle;
        ShapeMath.AddArc(profile, new ProfileArc(new Vector2(0f, -middle), radius, 0f, MathF.PI, 2 * rings), rings, 2 * rings, distance: start);
        var builder = new ShapeBuilder();
        builder.Revolve(profile, segments, radius, uvPerMetre);
        return builder.ToMesh(surface);
    }

    /// <summary>
    /// A cylinder standing up, closed at both ends. The ends can differ in size, so a top
    /// radius of zero makes a cone and anything between makes a tapered post or a bucket.
    /// </summary>
    public static GeneratedMesh Cylinder(float topRadius = 0.5f, float bottomRadius = 0.5f, float height = 1f, int segments = 32, Surface? surface = null, float? uvPerMetre = null)
    {
        uvPerMetre = PerMetre(uvPerMetre, surface);
        RequireAtLeastZero(topRadius, nameof(topRadius));
        RequireAtLeastZero(bottomRadius, nameof(bottomRadius));
        if (topRadius == 0f && bottomRadius == 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(topRadius), topRadius, "A cylinder needs a top or bottom radius above zero.");
        }

        RequirePositive(height, nameof(height), "height");
        RequireAtLeast(segments, 3, nameof(segments));

        float top = height * 0.5f;
        float slope = bottomRadius - topRadius;
        float side = MathF.Sqrt(height * height + slope * slope);

        // The side leans by the difference in radius, so its normal tips up for a cone.
        var normal = new Vector2(height, slope) / side;
        var builder = new ShapeBuilder();
        builder.Revolve(
            [
                new ProfilePoint(topRadius, top, normal, 0f),
                new ProfilePoint(bottomRadius, -top, normal, side),
            ],
            segments,
            Math.Max(topRadius, bottomRadius),
            uvPerMetre);
        if (topRadius > 0f)
        {
            builder.AddDisc(top, topRadius, segments, facingUp: true, uvPerMetre);
        }

        if (bottomRadius > 0f)
        {
            builder.AddDisc(-top, bottomRadius, segments, facingUp: false, uvPerMetre);
        }

        return builder.ToMesh(surface);
    }

    /// <summary>A cone standing on its base, pointing up.</summary>
    public static GeneratedMesh Cone(float radius = 0.5f, float height = 1f, int segments = 32, Surface? surface = null, float? uvPerMetre = null) =>
        Cylinder(0f, radius, height, segments, surface, uvPerMetre);

    /// <summary>
    /// A flat square of ground lying on x and z, facing up. It has one side, so it can't be
    /// seen from below. Subdivisions split each side into that many squares.
    /// </summary>
    public static GeneratedMesh Plane(Vector2 size, int subdivisions = 1, Surface? surface = null, float? uvPerMetre = null)
    {
        uvPerMetre = PerMetre(uvPerMetre, surface);
        RequirePositive(size.X, nameof(size), "width");
        RequirePositive(size.Y, nameof(size), "depth");
        RequireAtLeast(subdivisions, 1, nameof(subdivisions));

        var builder = new ShapeBuilder();
        var uvScale = ShapeMath.Tiling(uvPerMetre, size.X, size.Y);
        var tangent = new Vector4(Vector3.UnitX, 1f);
        builder.AddGrid(subdivisions, subdivisions, (column, row) =>
        {
            float u = column / (float)subdivisions;
            float v = row / (float)subdivisions;
            return new Vertex(new Vector3((u - 0.5f) * size.X, 0f, (v - 0.5f) * size.Y), Vector3.UnitY, tangent, new Vector2(u, v) * uvScale);
        });
        return builder.ToMesh(surface);
    }

    /// <summary>
    /// A flat rectangle standing on x and y, facing along z towards a camera at the
    /// default spot. It has one side, like a sign or a picture.
    /// </summary>
    public static GeneratedMesh Quad(Vector2 size, Surface? surface = null, float? uvPerMetre = null)
    {
        uvPerMetre = PerMetre(uvPerMetre, surface);
        RequirePositive(size.X, nameof(size), "width");
        RequirePositive(size.Y, nameof(size), "height");

        var builder = new ShapeBuilder();
        var uvScale = ShapeMath.Tiling(uvPerMetre, size.X, size.Y);
        var tangent = new Vector4(Vector3.UnitX, 1f);
        builder.AddGrid(1, 1, (column, row) => new Vertex(
            new Vector3((column - 0.5f) * size.X, (0.5f - row) * size.Y, 0f),
            Vector3.UnitZ,
            tangent,
            new Vector2(column, row) * uvScale));
        return builder.ToMesh(surface);
    }

    /// <summary>
    /// A ring lying flat on x and z, like a doughnut. The radius is from the middle to the
    /// centre of the tube, and the tube must be thinner than that. Segments go round the
    /// ring and sides go round the tube.
    /// </summary>
    public static GeneratedMesh Torus(float radius = 0.375f, float tube = 0.125f, int segments = 32, int sides = 16, Surface? surface = null, float? uvPerMetre = null)
    {
        uvPerMetre = PerMetre(uvPerMetre, surface);
        RequirePositive(radius, nameof(radius), "radius");
        RequirePositive(tube, nameof(tube), "tube");
        if (tube >= radius)
        {
            throw new ArgumentOutOfRangeException(nameof(tube), tube, FormattableString.Invariant($"A torus's tube must be thinner than its radius ({radius}), or it would pass through itself."));
        }

        RequireAtLeast(segments, 3, nameof(segments));
        RequireAtLeast(sides, 3, nameof(sides));

        // Round the tube from the top, out over the far side and back, like the sphere.
        var profile = new List<ProfilePoint>(sides + 1);
        ShapeMath.AddArc(profile, new ProfileArc(new Vector2(radius, 0f), tube, 0f, 2f * MathF.PI, sides), 0, sides);
        var builder = new ShapeBuilder();
        builder.Revolve(profile, segments, radius + tube, uvPerMetre);
        return builder.ToMesh(surface);
    }

    /// <summary>
    /// A column with this many flat sides, closed at both ends. The radius is from the
    /// middle to a corner, and the first corner points along x.
    /// </summary>
    public static GeneratedMesh Prism(int sides, float radius = 0.5f, float height = 1f, Surface? surface = null, float? uvPerMetre = null)
    {
        RequireAtLeast(sides, 3, nameof(sides));
        return Flat(Cylinder(radius, radius, height, sides, surface, uvPerMetre));
    }

    /// <summary>
    /// A six-sided tile for hex maps. The radius is from the middle to a corner, and corners
    /// point along x, so the flat edges face along z.
    /// </summary>
    public static GeneratedMesh Hex(float radius = 0.5f, float height = 0.2f, Surface? surface = null, float? uvPerMetre = null) =>
        Prism(6, radius, height, surface, uvPerMetre);

    /// <summary>
    /// The same mesh with hard edges: every triangle gets its own three corners, lit as one
    /// flat face. Use it for a low-poly look, such as a sphere with few segments.
    /// </summary>
    public static GeneratedMesh Flat(GeneratedMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var vertices = new Vertex[mesh.Indices.Length];
        var indices = new uint[mesh.Indices.Length];
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            var a = mesh.Vertices[mesh.Indices[i]];
            var b = mesh.Vertices[mesh.Indices[i + 1]];
            var c = mesh.Vertices[mesh.Indices[i + 2]];
            var normal = Vector3.Normalize(Vector3.Cross(b.Position - a.Position, c.Position - a.Position));
            var tangent = ShapeMath.FaceTangent(a, b, c, normal);
            vertices[i] = a with { Normal = normal, Tangent = tangent };
            vertices[i + 1] = b with { Normal = normal, Tangent = tangent };
            vertices[i + 2] = c with { Normal = normal, Tangent = tangent };
            indices[i] = (uint)i;
            indices[i + 1] = (uint)(i + 1);
            indices[i + 2] = (uint)(i + 2);
        }

        return mesh with { Vertices = vertices, Indices = indices };
    }

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
        float? uvPerMetre = null)
    {
        ArgumentNullException.ThrowIfNull(spline);
        ArgumentNullException.ThrowIfNull(outline);
        SplineSweep.CheckOutline(outline, closedOutline);
        RequirePositive(step, nameof(step), "step");
        float perMetre = PerMetre(uvPerMetre, surface) ?? 1f;
        RequirePositive(perMetre, nameof(uvPerMetre), "UV scale");

        int rings = Math.Max(1, (int)MathF.Ceiling(spline.Length / step - 1e-4f));
        var poses = new SplinePose[rings + 1];
        var along = new float[rings + 1];
        for (int ring = 0; ring <= rings; ring++)
        {
            along[ring] = spline.Length * ring / rings;
            poses[ring] = spline.FrameAtDistance(along[ring]);
        }

        var builder = new ShapeBuilder();
        foreach (var strip in SplineSweep.Strips(outline, closedOutline, smooth))
        {
            builder.AddGrid(strip.Length - 1, rings, (column, row) =>
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
                    new Vector2(corner.U, along[row]) * perMetre);
            });
        }

        return builder.ToMesh(surface);
    }

    /// <summary>
    /// A flat strip along a spline, facing up, such as a road or a river. It's
    /// <paramref name="width"/> metres across, centred on the spline.
    /// </summary>
    public static GeneratedMesh Ribbon(Spline spline, float width, float step = 0.5f, Surface? surface = null, float? uvPerMetre = null)
    {
        RequirePositive(width, nameof(width), "width");
        return Extrude(spline, [new Vector2(width * 0.5f, 0f), new Vector2(-width * 0.5f, 0f)], step: step, surface: surface, uvPerMetre: uvPerMetre);
    }

    /// <summary>A round pipe along a spline, for rails, cables, ropes and pipes. Sides go round it.</summary>
    public static GeneratedMesh Tube(Spline spline, float radius = 0.25f, int sides = 12, float step = 0.5f, Surface? surface = null, float? uvPerMetre = null)
    {
        RequirePositive(radius, nameof(radius), "radius");
        RequireAtLeast(sides, 3, nameof(sides));

        var outline = new Vector2[sides];
        for (int i = 0; i < sides; i++)
        {
            var (sin, cos) = ShapeMath.SinCos(2f * MathF.PI * i / sides);
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
    /// The result keeps the mesh's <see cref="Vertex.Uv2"/>, which stretches with the bend, so call
    /// <see cref="GeneratedMesh.WithLightmapUvs"/> on it after a tight bend.
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

    /// <summary>The meshes behind <see cref="ModelRef.Sphere"/> and the other built-in shapes, in index order from -1.</summary>
    internal static GeneratedMesh[] BuiltIn() =>
    [
        Sphere(),
        Capsule(),
        Cylinder(),
        Cone(),
        Plane(Vector2.One),
        Quad(Vector2.One),
        Torus(),
    ];
}
