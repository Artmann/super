using System.Globalization;
using System.Numerics;

namespace Rendition;

/// <summary>
/// Makes a second UV set for lightmaps, in which no two triangles share a texel, and checks one. The mesh is split
/// into charts of triangles that face about the same way, each chart is laid flat and scaled so every square metre
/// gets the same texels, and the charts are packed onto one page with a gutter between them.
/// </summary>
public static class LightmapUnwrap
{
    /// <summary>The lightmap size, in texels square, that gutters and overlaps are measured at.</summary>
    public const int ReferenceSize = 512;

    /// <summary>The empty texels kept between any two charts at <see cref="ReferenceSize"/>.</summary>
    public const int Gutter = 2;

    /// <summary>A chart takes triangles within 60 degrees of its mean normal.</summary>
    private const double ConeCosine = 0.5;

    /// <summary>Vertex normals within about 5 degrees agree; more is a hard edge.</summary>
    private const float NormalAgreement = 0.996f;

    /// <summary>Texel density must vary less than this many times across the mesh.</summary>
    private const double DensityLimit = 4.0;

    /// <summary>A triangle with less area than this share of the mesh's largest has none.</summary>
    private const double ZeroArea = 1e-10;

    /// <summary>
    /// Triangles of one flattened chart overlap when they share more than this share of the chart's extent squared.
    /// Far stricter than <see cref="TexelOverlap"/>, so what the unwrapper accepts the check does too.
    /// </summary>
    private const double ProjectedOverlap = 1e-12;

    /// <summary>Triangles of one chart overlap when they share more than this many texels, at the reference size.</summary>
    private const double TexelOverlap = 0.01;

    /// <summary>When the charts don't fit the page, they're all shrunk by this much and packed again.</summary>
    private const double Shrink = 0.9;

    /// <summary>
    /// Unwraps the mesh: the same triangles in the same order, with vertices split where charts meet and
    /// <see cref="Vertex.Uv2"/> set. Every other vertex field is copied unchanged. The result passes
    /// <see cref="Problem"/>.
    /// </summary>
    public static (Vertex[] Vertices, uint[] Indices) Unwrap(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
    {
        var mesh = new Mesh(vertices, indices);
        var charts = BuildCharts(mesh);
        var layout = Lay(mesh, charts);

        var outIndices = new uint[indices.Length];
        var outVertices = new List<Vertex>(vertices.Length + vertices.Length / 4);
        var split = new Dictionary<long, uint>();
        for (int corner = 0; corner < indices.Length; corner++)
        {
            int chart = charts.ChartOf[corner / 3];
            uint index = indices[corner];
            long key = ((long)chart << 32) | index;
            if (!split.TryGetValue(key, out uint at))
            {
                at = (uint)outVertices.Count;
                split.Add(key, at);
                outVertices.Add(vertices[(int)index] with { Uv2 = layout[corner] });
            }

            outIndices[corner] = at;
        }

        return (outVertices.ToArray(), outIndices);
    }

    /// <summary>
    /// Says what's wrong with <paramref name="uvs"/> as a lightmap set at <see cref="ReferenceSize"/>, in one
    /// sentence, or null when nothing is. The rules, in the order they're checked: every corner is inside 0 to 1; no
    /// texel is touched by triangles of two charts, and no two triangles of one chart overlap; every two charts have
    /// <see cref="Gutter"/> empty texels between them; and texel density, a chart's UV area over its world area,
    /// varies less than 4 times between charts. A chart is the triangles joined by edges whose corners have the same
    /// positions and UVs, and every texel a triangle touches counts as its.
    /// </summary>
    public static string? Problem(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector2> uvs, ReadOnlySpan<uint> indices)
    {
        CheckUvMesh(positions, uvs, indices);

        for (int corner = 0; corner < indices.Length; corner++)
        {
            var uv = uvs[(int)indices[corner]];
            if (!(uv.X >= 0f && uv.X <= 1f && uv.Y >= 0f && uv.Y <= 1f))
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"Triangle {corner / 3} reaches outside 0 to 1, to ({uv.X:0.###}, {uv.Y:0.###}).");
            }
        }

        var (chartOf, chartCount) = UvCharts(positions, uvs, indices);
        int triangleCount = indices.Length / 3;
        const int size = ReferenceSize;

        // Every texel each triangle touches, first come owning it.
        var ownerTriangle = new int[size * size];
        var ownerChart = new int[size * size];
        Array.Fill(ownerChart, -1);
        Span<double> xs = stackalloc double[3];
        Span<double> ys = stackalloc double[3];
        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            for (int i = 0; i < 3; i++)
            {
                var uv = uvs[(int)indices[triangle * 3 + i]];
                xs[i] = uv.X * (double)size;
                ys[i] = uv.Y * (double)size;
            }

            int x0 = Math.Clamp((int)Math.Floor(Min(xs)), 0, size - 1);
            int x1 = Math.Clamp((int)Math.Floor(Max(xs)), 0, size - 1);
            int y0 = Math.Clamp((int)Math.Floor(Min(ys)), 0, size - 1);
            int y1 = Math.Clamp((int)Math.Floor(Max(ys)), 0, size - 1);
            int chart = chartOf[triangle];
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (!TouchesTexel(xs, ys, x, y))
                    {
                        continue;
                    }

                    int texel = y * size + x;
                    if (ownerChart[texel] < 0)
                    {
                        ownerChart[texel] = chart;
                        ownerTriangle[texel] = triangle;
                    }
                    else if (ownerChart[texel] != chart)
                    {
                        return string.Create(CultureInfo.InvariantCulture,
                            $"Triangles {ownerTriangle[texel]} and {triangle} overlap at texel ({x}, {y}).");
                    }
                }
            }
        }

        // Triangles of one chart share texels along their edges, so within a chart only real overlap counts.
        var members = Members(chartOf, chartCount);
        for (int chart = 0; chart < chartCount; chart++)
        {
            var list = members[chart];
            if (list.Count < 2)
            {
                continue;
            }

            var cx = new double[list.Count * 3];
            var cy = new double[list.Count * 3];
            for (int k = 0; k < list.Count; k++)
            {
                for (int i = 0; i < 3; i++)
                {
                    var uv = uvs[(int)indices[list[k] * 3 + i]];
                    cx[k * 3 + i] = uv.X * (double)size;
                    cy[k * 3 + i] = uv.Y * (double)size;
                }
            }

            var overlap = FirstOverlap(cx, cy, list.Count, TexelOverlap);
            if (overlap.A >= 0)
            {
                int a = Math.Min(list[overlap.A], list[overlap.B]);
                int b = Math.Max(list[overlap.A], list[overlap.B]);
                return string.Create(CultureInfo.InvariantCulture,
                    $"Triangles {a} and {b} overlap at texel ({(int)Math.Floor(overlap.X)}, {(int)Math.Floor(overlap.Y)}).");
            }
        }

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int chart = ownerChart[y * size + x];
                if (chart < 0)
                {
                    continue;
                }

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
                            int between = Math.Max(Math.Abs(dx), Math.Abs(dy)) - 1;
                            string texels = between == 1 ? "texel" : "texels";
                            return string.Create(CultureInfo.InvariantCulture,
                                $"Triangles {ownerTriangle[y * size + x]} and {ownerTriangle[ny * size + nx]} are in different charts with only {between} empty {texels} between them at texel ({x}, {y}), less than the {Gutter}-texel gutter.");
                        }
                    }
                }
            }
        }

        var density = Densities(positions, uvs, indices, chartOf, chartCount, members);
        if (density.Sparsest >= 0)
        {
            int sparse = members[density.Sparsest][0];
            int dense = members[density.Densest][0];
            if (density.Low <= 0.0)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"The chart of triangle {sparse} has no area in the UV set, so it gets no texels.");
            }

            double spread = density.High / density.Low;
            if (spread >= DensityLimit)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"Texel density varies {spread:0.##}× across the mesh, from the chart of triangle {sparse} to the chart of triangle {dense}, and must vary less than {DensityLimit}×.");
            }
        }

        return null;
    }

    /// <summary>
    /// The largest chart's texel density over the smallest's, where density is a chart's UV area over its world area,
    /// leaving out triangles with no world area. 1 when there are fewer than two charts with area, and infinity when a
    /// chart with world area has no UV area.
    /// </summary>
    public static double DensitySpread(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector2> uvs, ReadOnlySpan<uint> indices)
    {
        CheckUvMesh(positions, uvs, indices);
        var (chartOf, chartCount) = UvCharts(positions, uvs, indices);
        var density = Densities(positions, uvs, indices, chartOf, chartCount, Members(chartOf, chartCount));
        if (density.Sparsest < 0)
        {
            return 1.0;
        }

        return density.Low <= 0.0 ? double.PositiveInfinity : density.High / density.Low;
    }

    /// <summary>How many charts <see cref="Unwrap"/> splits the mesh into.</summary>
    internal static int Charts(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) =>
        BuildCharts(new Mesh(vertices, indices)).Members.Count;

    /// <summary>The mesh as the unwrapper reads it: corners welded by position, areas and face normals.</summary>
    private sealed class Mesh
    {
        public readonly Vertex[] Vertices;
        public readonly uint[] Indices;
        public readonly int TriangleCount;

        /// <summary>Per corner, the id of its position; corners at the same place share one.</summary>
        public readonly int[] PositionId;

        /// <summary>Per triangle, the cross product of two of its edges: the face normal times twice the area.</summary>
        public readonly Vector3D[] Cross;

        public readonly bool[] Zero;

        /// <summary>Per corner, the group of corners whose edge to the next corner joins the same two positions, or -1.</summary>
        public readonly int[] EdgeGroup;
        public readonly int[] GroupStart;
        public readonly int[] GroupCorners;

        public Mesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
        {
            if (indices.Length % 3 != 0)
            {
                throw new ArgumentException($"There are {indices.Length} indices, which isn't a whole number of triangles.", nameof(indices));
            }

            Vertices = vertices.ToArray();
            Indices = indices.ToArray();
            TriangleCount = indices.Length / 3;
            foreach (uint index in Indices)
            {
                if (index >= Vertices.Length)
                {
                    throw new ArgumentException($"Index {index} is past the {Vertices.Length} vertices.", nameof(indices));
                }
            }

            var positions = new Vector3[Indices.Length];
            for (int corner = 0; corner < Indices.Length; corner++)
            {
                positions[corner] = Vertices[Indices[corner]].Position;
            }

            PositionId = Weld(positions);
            (Cross, Zero) = Areas(positions);
            (EdgeGroup, GroupStart, GroupCorners) = Edges(PositionId);
        }
    }

    private sealed record Charting(int[] ChartOf, List<List<int>> Members);

    /// <summary>
    /// Grows charts over edges whose corners are at the same positions with agreeing normals, taking triangles within
    /// 60 degrees of the chart's running mean normal, then cuts a chart back where its flattening overlaps itself and
    /// grows the rest again from there. Triangles with no area join a neighbour's chart, or make one of their own.
    /// </summary>
    private static Charting BuildCharts(Mesh mesh)
    {
        int count = mesh.TriangleCount;
        var chartOf = new int[count];
        Array.Fill(chartOf, -1);
        var charts = new List<List<int>>();

        var pending = new Stack<int>();
        int next = 0;
        while (true)
        {
            int seed = -1;
            while (pending.Count > 0)
            {
                int candidate = pending.Pop();
                if (chartOf[candidate] < 0)
                {
                    seed = candidate;
                    break;
                }
            }

            if (seed < 0)
            {
                while (next < count && (chartOf[next] >= 0 || mesh.Zero[next]))
                {
                    next++;
                }

                if (next == count)
                {
                    break;
                }

                seed = next;
            }

            int chart = charts.Count;
            var members = Grow(mesh, seed, chart, chartOf);
            while (true)
            {
                int cut = FirstBadRank(mesh, members);
                if (cut < 0)
                {
                    break;
                }

                for (int k = cut; k < members.Count; k++)
                {
                    chartOf[members[k]] = -1;
                    next = Math.Min(next, members[k]);
                }

                pending.Push(members[cut]);
                members.RemoveRange(cut, members.Count - cut);
            }

            charts.Add(members);
        }

        // Triangles with no area take a neighbour's chart; chains of them pass it along.
        bool joined = true;
        while (joined)
        {
            joined = false;
            for (int triangle = 0; triangle < count; triangle++)
            {
                if (chartOf[triangle] >= 0)
                {
                    continue;
                }

                int neighbour = FirstNeighbourWithChart(mesh, triangle, chartOf);
                if (neighbour >= 0)
                {
                    chartOf[triangle] = chartOf[neighbour];
                    charts[chartOf[triangle]].Add(triangle);
                    joined = true;
                }
            }
        }

        for (int triangle = 0; triangle < count; triangle++)
        {
            if (chartOf[triangle] < 0)
            {
                chartOf[triangle] = charts.Count;
                charts.Add([triangle]);
            }
        }

        return new Charting(chartOf, charts);
    }

    private static List<int> Grow(Mesh mesh, int seed, int chart, int[] chartOf)
    {
        var members = new List<int> { seed };
        chartOf[seed] = chart;
        var sum = mesh.Cross[seed];
        for (int k = 0; k < members.Count; k++)
        {
            int triangle = members[k];
            for (int i = 0; i < 3; i++)
            {
                int corner = triangle * 3 + i;
                int group = mesh.EdgeGroup[corner];
                if (group < 0)
                {
                    continue;
                }

                for (int g = mesh.GroupStart[group]; g < mesh.GroupStart[group + 1]; g++)
                {
                    int other = mesh.GroupCorners[g];
                    int neighbour = other / 3;
                    if (neighbour == triangle || chartOf[neighbour] >= 0 || mesh.Zero[neighbour])
                    {
                        continue;
                    }

                    if (!SmoothEdge(mesh, corner, other))
                    {
                        continue;
                    }

                    var face = mesh.Cross[neighbour];
                    if (Vector3D.Dot(face, sum) < ConeCosine * face.Length() * sum.Length())
                    {
                        continue;
                    }

                    chartOf[neighbour] = chart;
                    members.Add(neighbour);
                    sum += face;
                }
            }
        }

        return members;
    }

    /// <summary>Whether the vertex normals at both ends of the edge agree on both sides of it.</summary>
    private static bool SmoothEdge(Mesh mesh, int corner, int other)
    {
        int cornerNext = corner / 3 * 3 + (corner + 1) % 3;
        int otherNext = other / 3 * 3 + (other + 1) % 3;
        if (mesh.PositionId[corner] == mesh.PositionId[other])
        {
            return Agree(mesh, corner, other) && Agree(mesh, cornerNext, otherNext);
        }

        return Agree(mesh, corner, otherNext) && Agree(mesh, cornerNext, other);
    }

    private static bool Agree(Mesh mesh, int a, int b)
    {
        var na = mesh.Vertices[mesh.Indices[a]].Normal;
        var nb = mesh.Vertices[mesh.Indices[b]].Normal;
        float la = na.LengthSquared();
        float lb = nb.LengthSquared();
        if (la < 1e-12f || lb < 1e-12f)
        {
            return la < 1e-12f && lb < 1e-12f;
        }

        return Vector3.Dot(na, nb) >= NormalAgreement * MathF.Sqrt(la * lb);
    }

    private static int FirstNeighbourWithChart(Mesh mesh, int triangle, int[] chartOf)
    {
        for (int i = 0; i < 3; i++)
        {
            int group = mesh.EdgeGroup[triangle * 3 + i];
            if (group < 0)
            {
                continue;
            }

            for (int g = mesh.GroupStart[group]; g < mesh.GroupStart[group + 1]; g++)
            {
                int neighbour = mesh.GroupCorners[g] / 3;
                if (neighbour != triangle && chartOf[neighbour] >= 0)
                {
                    return neighbour;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// The first place, in the order the chart grew, from which it has to be cut back: the later of two triangles
    /// that overlap once the chart is laid flat. A triangle that folds over flips there, so it overlaps its
    /// neighbour and is cut too. -1 when the chart is fine as it is. Never 0, so a cut chart keeps its seed.
    /// </summary>
    private static int FirstBadRank(Mesh mesh, List<int> members)
    {
        if (members.Count < 2)
        {
            return -1;
        }

        var flat = Flatten(mesh, members);
        double extent = Math.Max(Max(flat.X) - Min(flat.X), Max(flat.Y) - Min(flat.Y));
        var overlap = FirstOverlap(flat.X, flat.Y, members.Count, ProjectedOverlap * extent * extent);
        if (overlap.A < 0)
        {
            return -1;
        }

        return Math.Max(1, Math.Max(overlap.A, overlap.B));
    }

    private readonly record struct Flat(Vector3D Normal, double[] X, double[] Y);

    /// <summary>
    /// Lays the triangles onto the plane of their area-weighted mean normal, three corners each in order, measured
    /// from the first corner so far-off meshes keep their precision.
    /// </summary>
    private static Flat Flatten(Mesh mesh, List<int> members)
    {
        var sum = default(Vector3D);
        foreach (int triangle in members)
        {
            sum += mesh.Cross[triangle];
        }

        double length = sum.Length();
        var normal = length > 0.0 ? sum / length : new Vector3D(0.0, 0.0, 1.0);
        var helper = Math.Abs(normal.X) < 0.7 ? new Vector3D(1.0, 0.0, 0.0) : new Vector3D(0.0, 1.0, 0.0);
        var u = Vector3D.Cross(helper, normal).Normalized();
        var v = Vector3D.Cross(normal, u);

        var origin = new Vector3D(mesh.Vertices[mesh.Indices[members[0] * 3]].Position);
        var xs = new double[members.Count * 3];
        var ys = new double[members.Count * 3];
        for (int k = 0; k < members.Count; k++)
        {
            for (int i = 0; i < 3; i++)
            {
                var p = new Vector3D(mesh.Vertices[mesh.Indices[members[k] * 3 + i]].Position) - origin;
                xs[k * 3 + i] = Vector3D.Dot(p, u);
                ys[k * 3 + i] = Vector3D.Dot(p, v);
            }
        }

        return new Flat(normal, xs, ys);
    }

    /// <summary>
    /// Flattens every chart, turns it to its smallest bounding rectangle, scales it so a square metre covers the same
    /// UV area in every chart, and packs the charts onto the page. Each chart's rectangle has half a texel or more of
    /// margin round the chart, so every texel the chart touches is inside its rectangle and the packer's gutter lies
    /// between touched texels. Returns each corner's second UV.
    /// </summary>
    private static Vector2[] Lay(Mesh mesh, Charting charts)
    {
        int chartCount = charts.Members.Count;
        var flats = new Flat[chartCount];
        var scale = new double[chartCount];
        var bounds = new (double MinX, double MinY, double MaxX, double MaxY)[chartCount];
        var hasArea = new bool[chartCount];
        double boxArea = 0.0;
        double longest = 0.0;
        for (int chart = 0; chart < chartCount; chart++)
        {
            var members = charts.Members[chart];
            double world = 0.0;
            foreach (int triangle in members)
            {
                if (!mesh.Zero[triangle])
                {
                    world += mesh.Cross[triangle].Length() * 0.5;
                }
            }

            if (world <= 0.0)
            {
                continue;
            }

            hasArea[chart] = true;
            var flat = Flatten(mesh, members);
            double flatArea = 0.0;
            for (int k = 0; k < members.Count; k++)
            {
                if (!mesh.Zero[members[k]])
                {
                    flatArea += SignedArea(flat.X, flat.Y, k * 3);
                }
            }

            Turn(flat.X, flat.Y);
            flats[chart] = flat;

            // Flattening shrinks tilted triangles, so each chart grows back by its own share to keep density even.
            scale[chart] = Math.Sqrt(world / flatArea);
            bounds[chart] = (Min(flat.X), Min(flat.Y), Max(flat.X), Max(flat.Y));
            double width = (bounds[chart].MaxX - bounds[chart].MinX) * scale[chart];
            double height = (bounds[chart].MaxY - bounds[chart].MinY) * scale[chart];
            boxArea += width * height;
            longest = Math.Max(longest, Math.Max(width, height));
        }

        // UV units per metre: start where the charts' boxes would fill the page and the longest just fits.
        double perMetre = longest > 0.0 ? (ReferenceSize - 1.0) / (ReferenceSize * longest) : 1.0;
        if (boxArea > 0.0)
        {
            perMetre = Math.Min(perMetre, Math.Sqrt(1.0 / boxArea));
        }

        var sizes = new (int Width, int Height)[chartCount];
        var places = new (int X, int Y)[chartCount];
        for (int attempt = 0; ; attempt++)
        {
            bool smallest = true;
            for (int chart = 0; chart < chartCount; chart++)
            {
                if (!hasArea[chart])
                {
                    sizes[chart] = (1, 1);
                    continue;
                }

                double texels = perMetre * scale[chart] * ReferenceSize;
                int width = (int)Math.Ceiling((bounds[chart].MaxX - bounds[chart].MinX) * texels) + 1;
                int height = (int)Math.Ceiling((bounds[chart].MaxY - bounds[chart].MinY) * texels) + 1;
                sizes[chart] = (width, height);
                smallest &= width <= 2 && height <= 2;
            }

            if (SkylinePacker.TryPack(sizes, ReferenceSize, ReferenceSize, Gutter, places))
            {
                break;
            }

            if (smallest || attempt > 500)
            {
                throw new InvalidOperationException(
                    $"The mesh's {chartCount} lightmap charts don't fit a {ReferenceSize}-texel page with a {Gutter}-texel gutter, even at their smallest.");
            }

            perMetre *= Shrink;
        }

        var uvs = new Vector2[mesh.Indices.Length];
        for (int chart = 0; chart < chartCount; chart++)
        {
            var members = charts.Members[chart];
            var (x, y) = places[chart];
            if (!hasArea[chart])
            {
                var middle = new Vector2((x + 0.5f) / ReferenceSize, (y + 0.5f) / ReferenceSize);
                foreach (int triangle in members)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        uvs[triangle * 3 + i] = middle;
                    }
                }

                continue;
            }

            var flat = flats[chart];
            var box = bounds[chart];
            double texels = perMetre * scale[chart] * ReferenceSize;
            double left = x + (sizes[chart].Width - (box.MaxX - box.MinX) * texels) * 0.5;
            double top = y + (sizes[chart].Height - (box.MaxY - box.MinY) * texels) * 0.5;
            for (int k = 0; k < members.Count; k++)
            {
                for (int i = 0; i < 3; i++)
                {
                    double u = left + (flat.X[k * 3 + i] - box.MinX) * texels;
                    double v = top + (flat.Y[k * 3 + i] - box.MinY) * texels;
                    uvs[members[k] * 3 + i] = new Vector2((float)(u / ReferenceSize), (float)(v / ReferenceSize));
                }
            }
        }

        return uvs;
    }

    /// <summary>
    /// Turns the points about the origin so their bounding rectangle is the smallest, which lines it up with one of
    /// the edges of their convex hull.
    /// </summary>
    private static void Turn(double[] xs, double[] ys)
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
            double minU = double.MaxValue;
            double maxU = double.MinValue;
            double minV = double.MaxValue;
            double maxV = double.MinValue;
            foreach (var (px, py) in hull)
            {
                double u = px * c + py * s;
                double v = -px * s + py * c;
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
            }

            double area = (maxU - minU) * (maxV - minV);
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

    /// <summary>
    /// Of every two triangles (three corners each, in order) whose shared area is more than
    /// <paramref name="minArea"/>, the pair whose later triangle comes first, with the middle of their shared area.
    /// A grid over the triangles' bounds finds the candidates, so this is close to linear in the triangles.
    /// </summary>
    private static (int A, int B, double X, double Y) FirstOverlap(double[] xs, double[] ys, int count, double minArea)
    {
        var none = (-1, -1, 0.0, 0.0);
        if (count < 2)
        {
            return none;
        }

        double minX = Min(xs);
        double minY = Min(ys);
        double width = Max(xs) - minX;
        double height = Max(ys) - minY;
        double cell = Math.Sqrt(width * height / count);
        cell = Math.Max(cell, Math.Max(width, height) / 1024.0);
        if (cell <= 0.0)
        {
            return none;
        }

        int columns = Math.Clamp((int)(width / cell) + 1, 1, 1024);
        int rows = Math.Clamp((int)(height / cell) + 1, 1, 1024);
        var range = new (int X0, int Y0, int X1, int Y1)[count];
        var cellCount = new int[columns * rows + 1];
        for (int t = 0; t < count; t++)
        {
            double tMinX = Math.Min(xs[t * 3], Math.Min(xs[t * 3 + 1], xs[t * 3 + 2]));
            double tMaxX = Math.Max(xs[t * 3], Math.Max(xs[t * 3 + 1], xs[t * 3 + 2]));
            double tMinY = Math.Min(ys[t * 3], Math.Min(ys[t * 3 + 1], ys[t * 3 + 2]));
            double tMaxY = Math.Max(ys[t * 3], Math.Max(ys[t * 3 + 1], ys[t * 3 + 2]));
            range[t] = (
                Math.Clamp((int)((tMinX - minX) / cell), 0, columns - 1),
                Math.Clamp((int)((tMinY - minY) / cell), 0, rows - 1),
                Math.Clamp((int)((tMaxX - minX) / cell), 0, columns - 1),
                Math.Clamp((int)((tMaxY - minY) / cell), 0, rows - 1));
            for (int y = range[t].Y0; y <= range[t].Y1; y++)
            {
                for (int x = range[t].X0; x <= range[t].X1; x++)
                {
                    cellCount[y * columns + x + 1]++;
                }
            }
        }

        for (int i = 1; i < cellCount.Length; i++)
        {
            cellCount[i] += cellCount[i - 1];
        }

        var fill = (int[])cellCount.Clone();
        var inCell = new int[cellCount[^1]];
        for (int t = 0; t < count; t++)
        {
            for (int y = range[t].Y0; y <= range[t].Y1; y++)
            {
                for (int x = range[t].X0; x <= range[t].X1; x++)
                {
                    inCell[fill[y * columns + x]++] = t;
                }
            }
        }

        var best = none;
        int bestLater = int.MaxValue;
        for (int c = 0; c < columns * rows; c++)
        {
            int cx = c % columns;
            int cy = c / columns;
            for (int i = cellCount[c]; i < cellCount[c + 1]; i++)
            {
                int a = inCell[i];
                for (int j = i + 1; j < cellCount[c + 1]; j++)
                {
                    int b = inCell[j];
                    if (Math.Max(a, b) >= bestLater)
                    {
                        continue;
                    }

                    // Test each pair once, in the first cell both cover.
                    if (cx != Math.Max(range[a].X0, range[b].X0) || cy != Math.Max(range[a].Y0, range[b].Y0))
                    {
                        continue;
                    }

                    var (area, middleX, middleY) = SharedArea(xs, ys, a * 3, b * 3);
                    if (area > minArea)
                    {
                        bestLater = Math.Max(a, b);
                        best = (Math.Min(a, b), Math.Max(a, b), middleX, middleY);
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The area two triangles share and its middle, by clipping one to the other (Sutherland-Hodgman). Triangles
    /// that only touch share none.
    /// </summary>
    private static (double Area, double X, double Y) SharedArea(double[] xs, double[] ys, int a, int b)
    {
        if (Math.Max(xs[a], Math.Max(xs[a + 1], xs[a + 2])) <= Math.Min(xs[b], Math.Min(xs[b + 1], xs[b + 2]))
            || Math.Max(xs[b], Math.Max(xs[b + 1], xs[b + 2])) <= Math.Min(xs[a], Math.Min(xs[a + 1], xs[a + 2]))
            || Math.Max(ys[a], Math.Max(ys[a + 1], ys[a + 2])) <= Math.Min(ys[b], Math.Min(ys[b + 1], ys[b + 2]))
            || Math.Max(ys[b], Math.Max(ys[b + 1], ys[b + 2])) <= Math.Min(ys[a], Math.Min(ys[a + 1], ys[a + 2])))
        {
            return (0.0, 0.0, 0.0);
        }

        double clipArea = SignedArea(xs, ys, b);
        if (clipArea == 0.0 || SignedArea(xs, ys, a) == 0.0)
        {
            return (0.0, 0.0, 0.0);
        }

        Span<double> px = stackalloc double[12];
        Span<double> py = stackalloc double[12];
        Span<double> qx = stackalloc double[12];
        Span<double> qy = stackalloc double[12];
        int n = 3;
        for (int i = 0; i < 3; i++)
        {
            px[i] = xs[a + i];
            py[i] = ys[a + i];
        }

        double sign = clipArea > 0.0 ? 1.0 : -1.0;
        for (int e = 0; e < 3 && n > 0; e++)
        {
            double ex = xs[b + e];
            double ey = ys[b + e];
            double dx = xs[b + (e + 1) % 3] - ex;
            double dy = ys[b + (e + 1) % 3] - ey;
            int m = 0;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double si = sign * (dx * (py[i] - ey) - dy * (px[i] - ex));
                double sj = sign * (dx * (py[j] - ey) - dy * (px[j] - ex));
                if (si >= 0.0)
                {
                    qx[m] = px[i];
                    qy[m] = py[i];
                    m++;
                }

                if ((si >= 0.0) != (sj >= 0.0))
                {
                    double t = si / (si - sj);
                    qx[m] = px[i] + (px[j] - px[i]) * t;
                    qy[m] = py[i] + (py[j] - py[i]) * t;
                    m++;
                }
            }

            n = m;
            qx[..n].CopyTo(px);
            qy[..n].CopyTo(py);
        }

        double twice = 0.0;
        double cx = 0.0;
        double cy = 0.0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            double c = px[i] * py[j] - px[j] * py[i];
            twice += c;
            cx += (px[i] + px[j]) * c;
            cy += (py[i] + py[j]) * c;
        }

        double area = Math.Abs(twice) * 0.5;
        if (area <= 0.0)
        {
            return (0.0, 0.0, 0.0);
        }

        return (area, cx / (3.0 * twice), cy / (3.0 * twice));
    }

    private static double SignedArea(double[] xs, double[] ys, int at) =>
        ((xs[at + 1] - xs[at]) * (ys[at + 2] - ys[at]) - (xs[at + 2] - xs[at]) * (ys[at + 1] - ys[at])) * 0.5;

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

    private static void CheckUvMesh(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector2> uvs, ReadOnlySpan<uint> indices)
    {
        if (positions.Length != uvs.Length)
        {
            throw new ArgumentException($"There are {positions.Length} positions but {uvs.Length} UVs.", nameof(uvs));
        }

        if (indices.Length % 3 != 0)
        {
            throw new ArgumentException($"There are {indices.Length} indices, which isn't a whole number of triangles.", nameof(indices));
        }

        foreach (uint index in indices)
        {
            if (index >= positions.Length)
            {
                throw new ArgumentException($"Index {index} is past the {positions.Length} vertices.", nameof(indices));
            }
        }
    }

    /// <summary>Charts of a UV set: triangles joined by edges whose corners have the same positions and UVs.</summary>
    private static (int[] ChartOf, int Count) UvCharts(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector2> uvs, ReadOnlySpan<uint> indices)
    {
        var ids = new Dictionary<(Vector3, Vector2), int>();
        var id = new int[indices.Length];
        for (int corner = 0; corner < indices.Length; corner++)
        {
            int index = (int)indices[corner];
            var key = (Clean(positions[index]), new Vector2(uvs[index].X + 0f, uvs[index].Y + 0f));
            if (!ids.TryGetValue(key, out int value))
            {
                value = ids.Count;
                ids.Add(key, value);
            }

            id[corner] = value;
        }

        int triangleCount = indices.Length / 3;
        var parent = new int[triangleCount];
        for (int t = 0; t < triangleCount; t++)
        {
            parent[t] = t;
        }

        var firstOnEdge = new Dictionary<(int, int), int>();
        for (int corner = 0; corner < indices.Length; corner++)
        {
            int a = id[corner];
            int b = id[corner / 3 * 3 + (corner + 1) % 3];
            if (a == b)
            {
                continue;
            }

            var edge = (Math.Min(a, b), Math.Max(a, b));
            if (firstOnEdge.TryGetValue(edge, out int first))
            {
                Union(parent, first, corner / 3);
            }
            else
            {
                firstOnEdge.Add(edge, corner / 3);
            }
        }

        // Number the charts in the order of their first triangles.
        var chartOf = new int[triangleCount];
        var number = new Dictionary<int, int>();
        for (int t = 0; t < triangleCount; t++)
        {
            int root = Find(parent, t);
            if (!number.TryGetValue(root, out int chart))
            {
                chart = number.Count;
                number.Add(root, chart);
            }

            chartOf[t] = chart;
        }

        return (chartOf, number.Count);
    }

    private static int Find(int[] parent, int t)
    {
        while (parent[t] != t)
        {
            parent[t] = parent[parent[t]];
            t = parent[t];
        }

        return t;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a);
        int rb = Find(parent, b);
        if (ra != rb)
        {
            parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
    }

    /// <summary>Each chart's triangles, in order.</summary>
    private static List<int>[] Members(int[] chartOf, int chartCount)
    {
        var members = new List<int>[chartCount];
        for (int chart = 0; chart < chartCount; chart++)
        {
            members[chart] = [];
        }

        for (int t = 0; t < chartOf.Length; t++)
        {
            members[chartOf[t]].Add(t);
        }

        return members;
    }

    /// <summary>The sparsest and densest charts that have world area, or -1 for both when none has.</summary>
    private static (int Sparsest, int Densest, double Low, double High) Densities(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector2> uvs,
        ReadOnlySpan<uint> indices,
        int[] chartOf,
        int chartCount,
        List<int>[] members)
    {
        var corners = new Vector3[indices.Length];
        for (int corner = 0; corner < indices.Length; corner++)
        {
            corners[corner] = positions[(int)indices[corner]];
        }

        var (cross, zero) = Areas(corners);
        int sparsest = -1;
        int densest = -1;
        double low = double.MaxValue;
        double high = double.MinValue;
        for (int chart = 0; chart < chartCount; chart++)
        {
            double world = 0.0;
            double uvArea = 0.0;
            foreach (int t in members[chart])
            {
                if (zero[t])
                {
                    continue;
                }

                world += cross[t].Length() * 0.5;
                var a = uvs[(int)indices[t * 3]];
                var b = uvs[(int)indices[t * 3 + 1]];
                var c = uvs[(int)indices[t * 3 + 2]];
                uvArea += Math.Abs(((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)c.X - a.X) * ((double)b.Y - a.Y)) * 0.5;
            }

            if (world <= 0.0)
            {
                continue;
            }

            double density = uvArea / world;
            if (density < low)
            {
                low = density;
                sparsest = chart;
            }

            if (density > high)
            {
                high = density;
                densest = chart;
            }
        }

        return (sparsest, densest, low, high);
    }

    /// <summary>Each triangle's cross product, and whether it has no area next to the largest.</summary>
    private static (Vector3D[] Cross, bool[] Zero) Areas(Vector3[] corners)
    {
        int count = corners.Length / 3;
        var cross = new Vector3D[count];
        double largest = 0.0;
        for (int t = 0; t < count; t++)
        {
            var a = new Vector3D(corners[t * 3]);
            cross[t] = Vector3D.Cross(new Vector3D(corners[t * 3 + 1]) - a, new Vector3D(corners[t * 3 + 2]) - a);
            largest = Math.Max(largest, cross[t].Length());
        }

        var zero = new bool[count];
        for (int t = 0; t < count; t++)
        {
            double length = cross[t].Length();
            zero[t] = length <= 0.0 || length <= largest * ZeroArea;
        }

        return (cross, zero);
    }

    /// <summary>An id per corner, the same for corners at exactly the same position.</summary>
    private static int[] Weld(Vector3[] corners)
    {
        var ids = new Dictionary<Vector3, int>();
        var id = new int[corners.Length];
        for (int corner = 0; corner < corners.Length; corner++)
        {
            var key = Clean(corners[corner]);
            if (!ids.TryGetValue(key, out int value))
            {
                value = ids.Count;
                ids.Add(key, value);
            }

            id[corner] = value;
        }

        return id;
    }

    /// <summary>Groups the corners whose edge to the next corner joins the same two positions, as lists.</summary>
    private static (int[] EdgeGroup, int[] GroupStart, int[] GroupCorners) Edges(int[] positionId)
    {
        var groups = new Dictionary<(int, int), int>();
        var edgeGroup = new int[positionId.Length];
        var sizes = new List<int>();
        for (int corner = 0; corner < positionId.Length; corner++)
        {
            int a = positionId[corner];
            int b = positionId[corner / 3 * 3 + (corner + 1) % 3];
            if (a == b)
            {
                edgeGroup[corner] = -1;
                continue;
            }

            var key = (Math.Min(a, b), Math.Max(a, b));
            if (!groups.TryGetValue(key, out int group))
            {
                group = groups.Count;
                groups.Add(key, group);
                sizes.Add(0);
            }

            edgeGroup[corner] = group;
            sizes[group]++;
        }

        var start = new int[sizes.Count + 1];
        for (int g = 0; g < sizes.Count; g++)
        {
            start[g + 1] = start[g] + sizes[g];
        }

        var fill = (int[])start.Clone();
        var members = new int[start[^1]];
        for (int corner = 0; corner < positionId.Length; corner++)
        {
            if (edgeGroup[corner] >= 0)
            {
                members[fill[edgeGroup[corner]]++] = corner;
            }
        }

        return (edgeGroup, start, members);
    }

    /// <summary>The same position with any -0 made 0, so they weld.</summary>
    private static Vector3 Clean(Vector3 p) => new(p.X + 0f, p.Y + 0f, p.Z + 0f);

    private static double Min(ReadOnlySpan<double> values)
    {
        double min = double.MaxValue;
        foreach (double value in values)
        {
            min = Math.Min(min, value);
        }

        return min;
    }

    private static double Max(ReadOnlySpan<double> values)
    {
        double max = double.MinValue;
        foreach (double value in values)
        {
            max = Math.Max(max, value);
        }

        return max;
    }

    /// <summary>A vector of doubles, for areas and flattening where floats lose too much.</summary>
    private readonly record struct Vector3D(double X, double Y, double Z)
    {
        public Vector3D(Vector3 v)
            : this(v.X, v.Y, v.Z)
        {
        }

        public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static Vector3D operator /(Vector3D a, double s) => new(a.X / s, a.Y / s, a.Z / s);

        public static double Dot(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vector3D Cross(Vector3D a, Vector3D b) =>
            new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public double Length() => Math.Sqrt(Dot(this, this));

        public Vector3D Normalized() => this / Length();
    }
}
