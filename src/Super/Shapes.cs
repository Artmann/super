using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// Ready-made meshes: boxes, spheres, capsules, cylinders, cones, planes, quads, tori and
/// prisms. Hand one to <see cref="Content.Mesh"/> to draw it. Every shape is centred on
/// zero with y up, its triangles face outwards, and its normals, tangents and UVs are set.
/// Curved shapes are smooth; <see cref="Flat"/> gives any mesh hard, faceted edges.
/// UVs stretch once across each part unless <c>uvPerMetre</c> is given, which makes them
/// tile in metres instead, so a texture keeps its size whatever the shape's size. A surface
/// from <see cref="Content.Surface(string)"/> tiles at its material's width when
/// <c>uvPerMetre</c> isn't given.
/// </summary>
public static partial class Shapes
{
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
            var uvScale = Tiling(uvPerMetre, width, height);
            builder.Grid(1, 1, (column, row) => new Vertex(
                normal * half + across * ((column - 0.5f) * width) + up * ((0.5f - row) * height),
                normal,
                tangent,
                new Vector2(column, row) * uvScale));
        }

        return builder.Build(surface);
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
        AddArc(profile, Vector2.Zero, radius, 0, rings, rings, startAngle: 0f, endAngle: MathF.PI);
        var builder = new ShapeBuilder();
        builder.Revolve(profile, segments, radius, uvPerMetre);
        return builder.Build(surface);
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
        AddArc(profile, Vector2.Zero, radius, 0, rings, 2 * rings, startAngle: 0f, endAngle: MathF.PI);
        var builder = new ShapeBuilder();
        builder.Revolve(profile, segments, radius, uvPerMetre);
        builder.Disc(0f, radius, segments, facingUp: false, uvPerMetre);
        return builder.Build(surface);
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
            throw new ArgumentOutOfRangeException(nameof(height), height, $"A capsule's height is from end to end, so it must be at least twice its radius ({2f * radius}).");
        }

        float middle = (height - 2f * radius) * 0.5f;
        var profile = new List<ProfilePoint>(2 * rings + 2);
        AddArc(profile, new Vector2(0f, middle), radius, 0, rings, 2 * rings, startAngle: 0f, endAngle: MathF.PI);
        float start = profile[^1].Distance + 2f * middle;
        AddArc(profile, new Vector2(0f, -middle), radius, rings, 2 * rings, 2 * rings, startAngle: 0f, endAngle: MathF.PI, distance: start);
        var builder = new ShapeBuilder();
        builder.Revolve(profile, segments, radius, uvPerMetre);
        return builder.Build(surface);
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
            builder.Disc(top, topRadius, segments, facingUp: true, uvPerMetre);
        }

        if (bottomRadius > 0f)
        {
            builder.Disc(-top, bottomRadius, segments, facingUp: false, uvPerMetre);
        }

        return builder.Build(surface);
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
        var uvScale = Tiling(uvPerMetre, size.X, size.Y);
        var tangent = new Vector4(Vector3.UnitX, 1f);
        builder.Grid(subdivisions, subdivisions, (column, row) =>
        {
            float u = column / (float)subdivisions;
            float v = row / (float)subdivisions;
            return new Vertex(new Vector3((u - 0.5f) * size.X, 0f, (v - 0.5f) * size.Y), Vector3.UnitY, tangent, new Vector2(u, v) * uvScale);
        });
        return builder.Build(surface);
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
        var uvScale = Tiling(uvPerMetre, size.X, size.Y);
        var tangent = new Vector4(Vector3.UnitX, 1f);
        builder.Grid(1, 1, (column, row) => new Vertex(
            new Vector3((column - 0.5f) * size.X, (0.5f - row) * size.Y, 0f),
            Vector3.UnitZ,
            tangent,
            new Vector2(column, row) * uvScale));
        return builder.Build(surface);
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
            throw new ArgumentOutOfRangeException(nameof(tube), tube, $"A torus's tube must be thinner than its radius ({radius}), or it would pass through itself.");
        }

        RequireAtLeast(segments, 3, nameof(segments));
        RequireAtLeast(sides, 3, nameof(sides));

        // Round the tube from the top, out over the far side and back, like the sphere.
        var profile = new List<ProfilePoint>(sides + 1);
        AddArc(profile, new Vector2(radius, 0f), tube, 0, sides, sides, startAngle: 0f, endAngle: 2f * MathF.PI);
        var builder = new ShapeBuilder();
        builder.Revolve(profile, segments, radius + tube, uvPerMetre);
        return builder.Build(surface);
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
            var tangent = FaceTangent(a, b, c, normal);
            vertices[i] = a with { Normal = normal, Tangent = tangent };
            vertices[i + 1] = b with { Normal = normal, Tangent = tangent };
            vertices[i + 2] = c with { Normal = normal, Tangent = tangent };
            indices[i] = (uint)i;
            indices[i + 1] = (uint)(i + 1);
            indices[i + 2] = (uint)(i + 2);
        }

        return mesh with { Vertices = vertices, Indices = indices };
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

    /// <summary>The names of the built-in shapes, in the same order as <see cref="BuiltIn"/>.</summary>
    internal static readonly string[] BuiltInNames = ["sphere", "capsule", "cylinder", "cone", "plane", "quad", "torus"];

    /// <summary>
    /// Which way a triangle's texture runs across, from how its UVs change over it. Bent
    /// to lie flat in the face, and with the handedness the shaders expect.
    /// </summary>
    private static Vector4 FaceTangent(in Vertex a, in Vertex b, in Vertex c, Vector3 normal)
    {
        var edge1 = b.Position - a.Position;
        var edge2 = c.Position - a.Position;
        var uv1 = b.Uv - a.Uv;
        var uv2 = c.Uv - a.Uv;
        float determinant = uv1.X * uv2.Y - uv2.X * uv1.Y;
        if (MathF.Abs(determinant) < 1e-12f)
        {
            // No usable UVs, so any direction in the face will do.
            return new Vector4(Vector3.Normalize(edge1), 1f);
        }

        var alongU = (edge1 * uv2.Y - edge2 * uv1.Y) / determinant;
        var alongV = (edge2 * uv1.X - edge1 * uv2.X) / determinant;
        var tangent = Vector3.Normalize(alongU - normal * Vector3.Dot(normal, alongU));

        // The shaders build the bitangent as cross(normal, tangent) * w, pointing to where v falls.
        float handedness = Vector3.Dot(Vector3.Cross(normal, tangent), alongV) < 0f ? 1f : -1f;
        return new Vector4(tangent, handedness);
    }

    /// <summary>
    /// Adds points round an arc to a profile, from <paramref name="first"/> to
    /// <paramref name="last"/> of <paramref name="steps"/> equal steps between the two
    /// angles. Angles start at the top and turn outwards, so normals face away from the centre.
    /// </summary>
    private static void AddArc(List<ProfilePoint> profile, Vector2 centre, float radius, int first, int last, int steps, float startAngle, float endAngle, float distance = 0f)
    {
        for (int step = first; step <= last; step++)
        {
            var (sin, cos) = SinCos(startAngle + (endAngle - startAngle) * step / steps);
            var normal = new Vector2(sin, cos);
            float travelled = distance + radius * MathF.Abs(endAngle - startAngle) * (step - first) / steps;
            profile.Add(new ProfilePoint(centre.X + radius * sin, centre.Y + radius * cos, normal, travelled));
        }
    }

    /// <summary>Sine and cosine, exact at quarter turns, so poles and seams meet bit for bit.</summary>
    internal static (float Sin, float Cos) SinCos(float angle)
    {
        float quarters = angle / (MathF.PI * 0.5f);
        float nearest = MathF.Round(quarters);
        if (MathF.Abs(quarters - nearest) < 1e-6f)
        {
            int quarter = ((int)nearest % 4 + 4) % 4;
            return quarter switch
            {
                0 => (0f, 1f),
                1 => (1f, 0f),
                2 => (0f, -1f),
                _ => (-1f, 0f),
            };
        }

        return MathF.SinCos(angle);
    }

    /// <summary>The tiling to use: the one given, else one tile per <see cref="Surface.TileWidth"/> metres, else none.</summary>
    internal static float? PerMetre(float? uvPerMetre, Surface? surface)
    {
        if (uvPerMetre is not null)
        {
            return uvPerMetre;
        }

        return surface?.TileWidth is { } width ? 1f / width : null;
    }

    private static Vector2 Tiling(float? uvPerMetre, float width, float height)
    {
        if (uvPerMetre is not { } perMetre)
        {
            return Vector2.One;
        }

        RequirePositive(perMetre, nameof(uvPerMetre), "UV scale");
        return new Vector2(width, height) * perMetre;
    }

    private static void RequirePositive(float value, string field, string what)
    {
        if (!(value > 0f) || !float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(field, value, $"The {what} must be above zero.");
        }
    }

    private static void RequireAtLeastZero(float value, string field)
    {
        if (!(value >= 0f) || !float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(field, value, "The radius can't be below zero.");
        }
    }

    private static void RequireAtLeast(int value, int least, string field)
    {
        if (value < least)
        {
            throw new ArgumentOutOfRangeException(field, value, $"A shape needs at least {least} {field}.");
        }
    }

    /// <summary>
    /// One point on the outline a round shape is spun from: how far out it is, how high,
    /// which way it faces in that half-slice, and how far along the outline it lies.
    /// </summary>
    private readonly record struct ProfilePoint(float Radius, float Y, Vector2 Normal, float Distance);

    private sealed class ShapeBuilder
    {
        private readonly List<Vertex> _vertices = [];
        private readonly List<uint> _indices = [];

        public GeneratedMesh Build(Surface? surface) => new([.. _vertices], [.. _indices], surface ?? new Surface());

        /// <summary>
        /// A grid of vertices, row by row from the top. Rows run down the texture and
        /// columns across it. A triangle whose corners meet, such as at a pole, is left out.
        /// </summary>
        public void Grid(int columns, int rows, Func<int, int, Vertex> vertexAt)
        {
            uint start = (uint)_vertices.Count;
            for (int row = 0; row <= rows; row++)
            {
                for (int column = 0; column <= columns; column++)
                {
                    _vertices.Add(vertexAt(column, row));
                }
            }

            uint stride = (uint)columns + 1;
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    uint corner = start + (uint)row * stride + (uint)column;
                    uint below = corner + stride;
                    Triangle(corner, below, corner + 1);
                    Triangle(corner + 1, below, below + 1);
                }
            }
        }

        /// <summary>
        /// Spins an outline round the y axis. Its points go down the outside of the shape,
        /// and the texture wraps once round, joined at a seam facing along x.
        /// </summary>
        public void Revolve(IReadOnlyList<ProfilePoint> profile, int segments, float widest, float? uvPerMetre)
        {
            float length = profile[^1].Distance;
            var uvScale = uvPerMetre is { } perMetre ? Tiling(perMetre, 2f * MathF.PI * widest, 1f) : new Vector2(1f, 1f / length);
            Grid(segments, profile.Count - 1, (column, row) =>
            {
                // The seam's last column takes the same angle as the first, so they meet exactly.
                var (sin, cos) = Around(column % segments, segments);
                var point = profile[row];
                var tangent = new Vector4(-sin, 0f, -cos, 1f);
                return new Vertex(
                    new Vector3(point.Radius * cos, point.Y, -point.Radius * sin),
                    new Vector3(point.Normal.X * cos, point.Normal.Y, -point.Normal.X * sin),
                    tangent,
                    new Vector2(column / (float)segments, point.Distance) * uvScale);
            });
        }

        /// <summary>A flat round end at this height, facing up or down, with its own sharp-edged normals.</summary>
        public void Disc(float y, float radius, int segments, bool facingUp, float? uvPerMetre)
        {
            var normal = facingUp ? Vector3.UnitY : -Vector3.UnitY;
            var tangent = new Vector4(Vector3.UnitX, 1f);

            // Seen from outside, v runs towards +z on top and towards -z underneath.
            float flip = facingUp ? 1f : -1f;
            var uvScale = uvPerMetre is { } perMetre ? new Vector2(perMetre) : new Vector2(0.5f / radius);
            var uvCentre = uvPerMetre is null ? new Vector2(0.5f) : Vector2.Zero;

            uint centre = (uint)_vertices.Count;
            _vertices.Add(new Vertex(new Vector3(0f, y, 0f), normal, tangent, uvCentre));
            for (int i = 0; i < segments; i++)
            {
                var (sin, cos) = Around(i, segments);
                var position = new Vector3(radius * cos, y, -radius * sin);
                _vertices.Add(new Vertex(position, normal, tangent, uvCentre + new Vector2(position.X, position.Z * flip) * uvScale));
            }

            for (int i = 0; i < segments; i++)
            {
                uint here = centre + 1 + (uint)i;
                uint next = centre + 1 + (uint)((i + 1) % segments);

                // Round the rim goes anticlockwise from above, so the top keeps that order.
                if (facingUp)
                {
                    Triangle(centre, here, next);
                }
                else
                {
                    Triangle(centre, next, here);
                }
            }
        }

        private static (float Sin, float Cos) Around(int step, int steps) => SinCos(2f * MathF.PI * step / steps);

        private void Triangle(uint a, uint b, uint c)
        {
            var pa = _vertices[(int)a].Position;
            var pb = _vertices[(int)b].Position;
            var pc = _vertices[(int)c].Position;
            if (pa == pb || pb == pc || pa == pc)
            {
                return;
            }

            _indices.Add(a);
            _indices.Add(b);
            _indices.Add(c);
        }
    }
}
