using System.Globalization;
using System.Numerics;

namespace Super.Cook.Models;

/// <summary>
/// A mesh as the mask baker reads it: corners in metres, the UV set the masks are laid out by, and three indices
/// per triangle. Triangles are wound counter-clockwise seen from outside. <paramref name="Parts"/> says which of the
/// model's meshes each triangle belongs to, since height is measured from each mesh's own foot; null is one mesh.
/// </summary>
public sealed record MeshData(Vector3[] Positions, Vector2[] Uvs, int[] Indices, int[]? Parts = null);

/// <summary>
/// A baked mask: <see cref="Size"/> pixels square, RGBA8 row by row, row 0 at v = 0. See <see cref="MaskBaker.Bake"/>.
/// </summary>
public sealed record MaskImage(byte[] Rgba, int Size);

/// <summary>
/// Bakes masks from a mesh's shape into its UVs, for wear and dirt to be laid by: curvature in red, how much of the
/// sky each point sees in green, height above the mesh's foot in blue, and in alpha a slow noise that lets worn
/// edges come and go.
/// </summary>
public static class MaskBaker
{
    /// <summary>The size a mask is baked at, in pixels across.</summary>
    public const int DefaultSize = 1024;

    /// <summary>How far from a point an edge or a corner still counts toward its curvature, in metres.</summary>
    public const float CurvatureRadius = 0.025f;

    /// <summary>
    /// How high above a mesh's lowest point dirt reaches, in metres: blue is 0 at the foot and 1 this high up, or
    /// at the top of a mesh less tall than this.
    /// </summary>
    public const float DirtHeight = 0.3f;

    /// <summary>The size of the patches the wear breakup comes and goes in, in metres.</summary>
    public const float BreakupSize = 0.08f;

    /// <summary>How far a ray from the surface looks for something that blocks the sky, in metres.</summary>
    public const float OcclusionDistance = 0.5f;

    /// <summary>How many rays each texel sends toward the sky.</summary>
    public const int OcclusionRays = 64;

    /// <summary>How many directions along the surface each texel looks for an edge in.</summary>
    private const int CurvatureRays = 16;

    // How far off the surface rays start, so they don't hit the triangle they start on.
    private const float Lift = 0.001f;

    // Edges are found from a fraction this far below the surface, and walls from this far above, so a ray along the
    // surface meets them. Kept well under the thickness of a thin board.
    private const float Offset = 0.001f;

    // A surface that turns by 45 degrees or more counts as a whole edge, and one that turns by 20 or less as none, so
    // the folds between the triangles of a rough stone face or the facets of a barrel don't wear. Between, it counts
    // for a share.
    private static readonly float WholeEdge = 1f - MathF.Cos(MathF.PI / 4f);
    private static readonly float NoEdge = 1f - MathF.Cos(20f * MathF.PI / 180f);

    /// <summary>
    /// Says why the mesh's UVs can't hold masks, or returns null when they can: a UV outside 0 to 1, or two triangles
    /// that cover the same texel at <paramref name="size"/>, which would give it two places' masks.
    /// </summary>
    public static string? Problem(MeshData mesh, int size)
    {
        foreach (int index in mesh.Indices)
        {
            var uv = mesh.Uvs[index];
            if (uv.X < 0f || uv.X > 1f || uv.Y < 0f || uv.Y > 1f)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"its second UV set reaches outside 0 to 1, to ({uv.X:0.###}, {uv.Y:0.###}), so the masks can't hold it");
            }
        }

        var owner = new int[size * size];
        Array.Fill(owner, -1);
        string? problem = null;
        Rasterize(mesh, size, strict: true, (texel, triangle, _) =>
        {
            if (owner[texel] >= 0 && owner[texel] != triangle)
            {
                problem ??= $"triangles {owner[texel]} and {triangle} of its second UV set overlap at texel ({texel % size}, {texel / size}). "
                    + "Each texel can belong to one place on the mesh only, so give the masks a UV set whose islands don't overlap";
                return;
            }

            owner[texel] = triangle;
        });
        return problem;
    }

    /// <summary>
    /// Bakes <paramref name="mesh"/>'s masks at <paramref name="size"/> pixels square. Red is curvature: 0.5 is flat,
    /// 1 a convex edge within a few millimetres and 0 a concave corner. Green is the share of the sky a point sees past
    /// the mesh and <paramref name="occluders"/>, such as the rest of the model, within
    /// <see cref="OcclusionDistance"/>; only occluders that come that close to the mesh are looked at. Blue is the
    /// height above the lowest point of the point's own mesh (see <see cref="MeshData.Parts"/>), over
    /// <see cref="DirtHeight"/> or the mesh's own height if it's less, up to 1; a mesh with no height at all is 1.
    /// Alpha is the breakup, a noise from 0 to 1 in patches about <see cref="BreakupSize"/> across, laid by position,
    /// so the two faces at an edge agree. The texels around each island take its edge's values, and the rest read as
    /// flat, open, high and fully worn, which is clean. Check <see cref="Problem"/> first.
    /// </summary>
    public static MaskImage Bake(MeshData mesh, int size, MeshData? occluders = null)
    {
        var texels = new TexelBaker(mesh, size, occluders);
        var rgba = Background(size);
        var covered = new bool[size * size];
        Parallel.For(0, size, y =>
        {
            for (int x = 0; x < size; x++)
            {
                int texel = y * size + x;
                if (texels.Bake(texel) is not { } masks)
                {
                    continue;
                }

                rgba[texel * 4] = ToByte(masks.X);
                rgba[texel * 4 + 1] = ToByte(masks.Y);
                rgba[texel * 4 + 2] = ToByte(masks.Z);
                rgba[texel * 4 + 3] = ToByte(masks.W);
                covered[texel] = true;
            }
        });

        MaskDilation.Dilate(rgba, covered, size);
        return new MaskImage(rgba, size);
    }

    /// <summary>A point on the surface, two directions along it, and the texel's own turn of its rays.</summary>
    private readonly record struct SurfacePoint(Vector3 Point, Vector3 Normal, Vector3 Tangent, Vector3 Bitangent, float Turn);

    /// <summary>
    /// What one bake knows of its mesh: the trees rays are traced in, which triangle covers each texel and where, and
    /// each part's foot and height. Bakes each texel on its own, so texels can be baked in parallel.
    /// </summary>
    private sealed class TexelBaker
    {
        private readonly MeshData _mesh;
        private readonly Bvh _own;
        private readonly Bvh _everything;
        private readonly int[] _triangles;
        private readonly Vector3[] _weights;
        private readonly Dictionary<int, float> _bottoms = [];
        private readonly Dictionary<int, float> _spans;
        private readonly Vector2[] _occlusion = Hammersley(OcclusionRays);

        public TexelBaker(MeshData mesh, int size, MeshData? occluders)
        {
            _mesh = mesh;
            _own = new Bvh(mesh.Positions, mesh.Indices);
            var near = occluders is null ? null : Near(occluders, Bounds(mesh), OcclusionDistance);
            _everything = near is null || near.Indices.Length == 0 ? _own : Merged(mesh, near);
            _spans = Feet();
            (_triangles, _weights) = Cover(size);
        }

        /// <summary>The texel's curvature, sky, height and breakup, or null when no triangle with any area covers it.</summary>
        public Vector4? Bake(int texel)
        {
            int triangle = _triangles[texel];
            if (triangle < 0)
            {
                return null;
            }

            var a = _mesh.Positions[_mesh.Indices[triangle * 3]];
            var b = _mesh.Positions[_mesh.Indices[triangle * 3 + 1]];
            var c = _mesh.Positions[_mesh.Indices[triangle * 3 + 2]];
            var cross = Vector3.Cross(b - a, c - a);
            if (cross.LengthSquared() < 1e-20f)
            {
                return null;
            }

            var normal = Vector3.Normalize(cross);
            var w = _weights[texel];
            var point = a * w.X + b * w.Y + c * w.Z;
            var (tangent, bitangent) = Frame(normal);
            var at = new SurfacePoint(point, normal, tangent, bitangent, Hash(texel));

            float curvature = Curvature(_own, at);
            float sky = Sky(_everything, at, _occlusion);
            int part = _mesh.Parts?[triangle] ?? 0;
            float span = _spans[part];
            float above = span > 0f ? (point.Y - _bottoms[part]) / span : 1f;
            return new Vector4(curvature, sky, above, Breakup(point));
        }

        // Each mesh's foot, kept, and how much of its height blue spans, returned.
        private Dictionary<int, float> Feet()
        {
            int triangleCount = _mesh.Indices.Length / 3;
            var tops = new Dictionary<int, float>();
            for (int triangle = 0; triangle < triangleCount; triangle++)
            {
                int part = _mesh.Parts?[triangle] ?? 0;
                for (int corner = 0; corner < 3; corner++)
                {
                    float y = _mesh.Positions[_mesh.Indices[triangle * 3 + corner]].Y;
                    _bottoms[part] = _bottoms.TryGetValue(part, out float bottom) ? MathF.Min(bottom, y) : y;
                    tops[part] = tops.TryGetValue(part, out float top) ? MathF.Max(top, y) : y;
                }
            }

            return _bottoms.ToDictionary(entry => entry.Key, entry => MathF.Min(DirtHeight, tops[entry.Key] - entry.Value));
        }

        // Which triangle covers each texel, and where on it.
        private (int[] Triangles, Vector3[] Weights) Cover(int size)
        {
            var triangles = new int[size * size];
            var weights = new Vector3[size * size];
            Array.Fill(triangles, -1);
            Rasterize(_mesh, size, strict: false, (texel, triangle, weight) =>
            {
                if (triangles[texel] < 0)
                {
                    triangles[texel] = triangle;
                    weights[texel] = weight;
                }
            });

            return (triangles, weights);
        }
    }

    // The background reads as flat, open and high, which is clean, so where a lower mip or the filter reaches it, it
    // adds neither wear nor dirt.
    private static byte[] Background(int size)
    {
        var rgba = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            rgba[i * 4] = 128;
            rgba[i * 4 + 1] = 255;
            rgba[i * 4 + 2] = 255;
            rgba[i * 4 + 3] = 255;
        }

        return rgba;
    }

    /// <summary>
    /// How much an edge at <paramref name="point"/> wears, from 0 to 1, as baked into alpha: two octaves of value noise
    /// over the position, pushed apart so most of an edge is clearly worn or clearly not, with soft ends between.
    /// </summary>
    public static float Breakup(Vector3 point)
    {
        var p = point / BreakupSize;
        float noise = (ValueNoise(p) * 2f + ValueNoise(p * 2.03f + new Vector3(17.1f, 5.3f, 9.7f))) / 3f;
        float t = Math.Clamp((noise - 0.35f) / 0.3f, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // Smooth noise from 0 to 1 with a random value at each whole-numbered point, blended between them.
    private static float ValueNoise(Vector3 p)
    {
        var cell = new Vector3(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
        var f = p - cell;
        f = f * f * (new Vector3(3f) - 2f * f);
        int x = (int)cell.X;
        int y = (int)cell.Y;
        int z = (int)cell.Z;
        float Corner(int dx, int dy, int dz) => Hash(unchecked((x + dx) * 73856093 ^ (y + dy) * 19349663 ^ (z + dz) * 83492791));
        float Lerp(float a, float b, float t) => a + (b - a) * t;
        float x00 = Lerp(Corner(0, 0, 0), Corner(1, 0, 0), f.X);
        float x10 = Lerp(Corner(0, 1, 0), Corner(1, 1, 0), f.X);
        float x01 = Lerp(Corner(0, 0, 1), Corner(1, 0, 1), f.X);
        float x11 = Lerp(Corner(0, 1, 1), Corner(1, 1, 1), f.X);
        return Lerp(Lerp(x00, x10, f.Y), Lerp(x01, x11, f.Y), f.Z);
    }

    private static (Vector3 Min, Vector3 Max) Bounds(MeshData mesh)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (int index in mesh.Indices)
        {
            min = Vector3.Min(min, mesh.Positions[index]);
            max = Vector3.Max(max, mesh.Positions[index]);
        }

        return (min, max);
    }

    // The occluders' triangles whose bounds come within reach of the box: nothing further can stop a ray.
    private static MeshData Near(MeshData occluders, (Vector3 Min, Vector3 Max) box, float reach)
    {
        var min = box.Min - new Vector3(reach);
        var max = box.Max + new Vector3(reach);
        var kept = new List<int>();
        for (int triangle = 0; triangle * 3 + 2 < occluders.Indices.Length; triangle++)
        {
            var a = occluders.Positions[occluders.Indices[triangle * 3]];
            var b = occluders.Positions[occluders.Indices[triangle * 3 + 1]];
            var c = occluders.Positions[occluders.Indices[triangle * 3 + 2]];
            var low = Vector3.Min(a, Vector3.Min(b, c));
            var high = Vector3.Max(a, Vector3.Max(b, c));
            if (low.X <= max.X && high.X >= min.X && low.Y <= max.Y && high.Y >= min.Y && low.Z <= max.Z && high.Z >= min.Z)
            {
                kept.Add(occluders.Indices[triangle * 3]);
                kept.Add(occluders.Indices[triangle * 3 + 1]);
                kept.Add(occluders.Indices[triangle * 3 + 2]);
            }
        }

        return occluders with { Indices = kept.ToArray(), Parts = null };
    }

    // 0.5 plus half of how close the nearest convex edge is, less how close the nearest concave corner is. Rays run
    // along the surface: from just under it they meet the far side of a convex edge, and from just over it the wall of
    // a concave one. A board's opposite face runs parallel to them, so it never counts.
    private static float Curvature(Bvh bvh, SurfacePoint at)
    {
        var (point, normal, tangent, bitangent, turn) = at;
        float convex = 0f;
        float concave = 0f;
        for (int k = 0; k < CurvatureRays; k++)
        {
            float angle = (k + turn) * MathF.Tau / CurvatureRays;
            var direction = tangent * MathF.Cos(angle) + bitangent * MathF.Sin(angle);
            convex = MathF.Max(convex, Closeness(bvh, point - normal * Offset, direction, normal));
            concave = MathF.Max(concave, Closeness(bvh, point + normal * Offset, direction, normal));
        }

        return 0.5f + 0.5f * Math.Clamp(convex - concave, -1f, 1f);
    }

    // 1 for a whole edge right beside the point, falling to 0 at the curvature radius or for a fold of 20 degrees or less.
    private static float Closeness(Bvh bvh, Vector3 origin, Vector3 direction, Vector3 normal)
    {
        if (!bvh.Closest(origin, direction, CurvatureRadius, out float distance, out var hitNormal))
        {
            return 0f;
        }

        float turn = 1f - MathF.Abs(Vector3.Dot(hitNormal, normal));
        float edge = Math.Clamp((turn - NoEdge) / (WholeEdge - NoEdge), 0f, 1f);
        return edge * (1f - distance / CurvatureRadius);
    }

    // The share of cosine-weighted rays over the surface that nothing stops within the occlusion distance.
    private static float Sky(Bvh bvh, SurfacePoint at, Vector2[] samples)
    {
        var (point, normal, tangent, bitangent, turn) = at;
        var origin = point + normal * Lift;
        int open = 0;
        foreach (var sample in samples)
        {
            float radius = MathF.Sqrt(sample.X);
            float angle = (sample.Y + turn) * MathF.Tau;
            var direction = tangent * (radius * MathF.Cos(angle))
                + bitangent * (radius * MathF.Sin(angle))
                + normal * MathF.Sqrt(MathF.Max(1f - sample.X, 0f));
            if (!bvh.Any(origin, Vector3.Normalize(direction), OcclusionDistance))
            {
                open++;
            }
        }

        return (float)open / samples.Length;
    }

    // Calls back for each texel whose centre a triangle covers, with the triangle and the centre's barycentric
    // weights. Strict leaves out centres on an edge, so neighbours that share one don't both claim it.
    private static void Rasterize(MeshData mesh, int size, bool strict, Action<int, int, Vector3> covered)
    {
        float tolerance = strict ? 1e-4f : -1e-5f;
        for (int triangle = 0; triangle * 3 + 2 < mesh.Indices.Length; triangle++)
        {
            var a = mesh.Uvs[mesh.Indices[triangle * 3]] * size;
            var b = mesh.Uvs[mesh.Indices[triangle * 3 + 1]] * size;
            var c = mesh.Uvs[mesh.Indices[triangle * 3 + 2]] * size;
            float area = Edge(a, b, c);
            if (MathF.Abs(area) < 1e-9f)
            {
                continue;
            }

            int x0 = Math.Max((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0);
            int x1 = Math.Min((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))), size - 1);
            int y0 = Math.Max((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), 0);
            int y1 = Math.Min((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), size - 1);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var weight = new Vector3(Edge(b, c, p), Edge(c, a, p), Edge(a, b, p)) / area;
                    if (weight.X > tolerance && weight.Y > tolerance && weight.Z > tolerance)
                    {
                        covered(y * size + x, triangle, weight);
                    }
                }
            }
        }
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    private static Bvh Merged(MeshData mesh, MeshData other)
    {
        if (ReferenceEquals(mesh.Positions, other.Positions))
        {
            return new Bvh(mesh.Positions, mesh.Indices.Concat(other.Indices).ToArray());
        }

        var positions = mesh.Positions.Concat(other.Positions).ToArray();
        var indices = mesh.Indices.Concat(other.Indices.Select(i => i + mesh.Positions.Length)).ToArray();
        return new Bvh(positions, indices);
    }

    // Two directions along the surface, at right angles to the normal and each other.
    private static (Vector3 Tangent, Vector3 Bitangent) Frame(Vector3 normal)
    {
        var helper = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var tangent = Vector3.Normalize(Vector3.Cross(helper, normal));
        return (tangent, Vector3.Cross(normal, tangent));
    }

    // Evenly spread points in the unit square, the same for every texel. Each texel turns them by its own amount, so
    // neighbours' rays don't line up into bands.
    private static Vector2[] Hammersley(int count)
    {
        var points = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            uint bits = (uint)i;
            bits = (bits << 16) | (bits >> 16);
            bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
            bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
            bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
            bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
            points[i] = new Vector2((i + 0.5f) / count, bits * (1f / 4294967296f));
        }

        return points;
    }

    // A fixed number from 0 to 1 for each texel, so a bake comes out the same every time.
    private static float Hash(int texel)
    {
        uint h = (uint)texel * 0x9E3779B1u;
        h ^= h >> 15;
        h *= 0x85EBCA77u;
        h ^= h >> 13;
        return (h & 0xFFFFFF) / 16777216f;
    }

    private static byte ToByte(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
