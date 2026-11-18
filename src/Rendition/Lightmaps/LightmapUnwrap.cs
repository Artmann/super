using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace Rendition;

/// <summary>
/// Makes a second UV set for lightmaps, in which no two triangles share a texel, and checks one. The mesh is split
/// into charts of triangles that face about the same way, each chart is laid flat and scaled so every square metre
/// gets the same texels, and the charts are packed onto one page with a gutter between them.
/// </summary>
public static class LightmapUnwrap
{
    /// <summary>The lightmap size, in texels square, that gutters and overlaps are measured at unless a caller names another.</summary>
    public const int ReferenceSize = 512;

    /// <summary>
    /// The largest page a mesh is laid out on, after <see cref="ReferenceSize"/> and twice that, when its charts don't fit
    /// a smaller one. The model cooker, built-in shapes and <c>GeneratedMesh.WithLightmapUvs</c> all stop here.
    /// </summary>
    public const int LargestSize = 4 * ReferenceSize;

    /// <summary>The empty texels kept between any two charts at the size they are laid out or checked at.</summary>
    public const int Gutter = ChartPage.Gutter;

    /// <summary>
    /// Names this code, for a cache of unwrapped meshes: a hash of the source files in this folder and of the one
    /// that holds <see cref="Vertex"/>, written into the assembly by the build. It changes when they do, and not with
    /// the rest of the renderer.
    /// </summary>
    public static string Version { get; } = typeof(LightmapUnwrap).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "LightmapUnwrapVersion").Value!;

    /// <summary>Texel density must vary less than this many times across the mesh.</summary>
    private const double DensityLimit = 4.0;

    /// <summary>Triangles of one chart overlap when they share more than this many texels, at the reference size.</summary>
    private const double TexelOverlap = 0.01;

    /// <summary>
    /// Unwraps the mesh: the same triangles in the same order, with vertices split where charts meet and
    /// <see cref="Vertex.Uv2"/> set. Every other vertex field is copied unchanged. The charts are packed on a page
    /// <paramref name="size"/> texels square with <see cref="Gutter"/> texels between them, and the result passes
    /// <see cref="Problem"/> at that size.
    /// </summary>
    public static (Vertex[] Vertices, uint[] Indices) Unwrap(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices, int size = ReferenceSize)
    {
        var mesh = new UnwrapMesh(vertices, indices);
        var charts = UnwrapCharts.Build(mesh);

        // A chart can curl round until two far-apart parts of it land within a texel of each other, which only shows
        // once its texel size is known. Cut each such chart back to before the later of the two, and lay out again.
        while (true)
        {
            var layout = ChartPage.Lay(mesh, charts, size);
            var shares = FarShareSearch.Find(mesh.Corners, layout, charts.ChartOf, firstOnly: false, size);
            if (shares.Count == 0)
            {
                return SplitAtCharts(mesh, charts.ChartOf, layout);
            }

            charts.CutBack(shares);
        }
    }

    /// <summary>
    /// Says what's wrong with <paramref name="uvs"/> as a lightmap set at <paramref name="size"/> texels square, in one
    /// sentence, or null when nothing is. The rules, in the order they're checked: every corner is inside 0 to 1; no
    /// texel is touched by triangles of two charts, and no two triangles of one chart overlap; no two triangles of one
    /// chart share a texel while lying more than four texels apart on the surface; every two charts have
    /// <see cref="Gutter"/> empty texels between them; and texel density, a chart's UV area over its world area,
    /// varies less than 4 times between charts. A chart is the triangles joined by edges whose corners have the same
    /// positions and UVs, and every texel a triangle touches counts as its.
    /// </summary>
    public static string? Problem(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector2> uvs, ReadOnlySpan<uint> indices, int size = ReferenceSize)
    {
        CheckUvMesh(positions, uvs, indices);
        if (OutsideProblem(uvs, indices) is { } outside)
        {
            return outside;
        }

        var set = new UvSet(positions, uvs, indices);
        var owners = new TexelOwners(size);
        return owners.Claim(set)
            ?? OverlapProblem(set, size)
            ?? FarShareProblem(set, size)
            ?? owners.GutterProblem()
            ?? DensityProblem(set);
    }

    /// <summary>
    /// The largest chart's texel density over the smallest's, where density is a chart's UV area over its world area,
    /// leaving out triangles with no world area. 1 when there are fewer than two charts with area, and infinity when a
    /// chart with world area has no UV area.
    /// </summary>
    public static double DensitySpread(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector2> uvs, ReadOnlySpan<uint> indices)
    {
        CheckUvMesh(positions, uvs, indices);
        var density = new UvSet(positions, uvs, indices).Densities();
        if (density.Sparsest < 0)
        {
            return 1.0;
        }

        return density.Low <= 0.0 ? double.PositiveInfinity : density.High / density.Low;
    }

    /// <summary>How many charts <see cref="Unwrap"/> splits the mesh into.</summary>
    internal static int Charts(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) =>
        UnwrapCharts.Build(new UnwrapMesh(vertices, indices)).Members.Count;

    /// <summary>The mesh's vertices with their second UVs, one copy per chart a vertex is in, and the new indices.</summary>
    private static (Vertex[] Vertices, uint[] Indices) SplitAtCharts(UnwrapMesh mesh, int[] chartOf, Vector2[] layout)
    {
        var indices = mesh.Indices;
        var outIndices = new uint[indices.Length];
        var outVertices = new List<Vertex>(mesh.Vertices.Length + mesh.Vertices.Length / 4);
        var split = new Dictionary<long, uint>();
        for (int corner = 0; corner < indices.Length; corner++)
        {
            int chart = chartOf[corner / 3];
            uint index = indices[corner];
            long key = ((long)chart << 32) | index;
            if (!split.TryGetValue(key, out uint at))
            {
                at = (uint)outVertices.Count;
                split.Add(key, at);
                outVertices.Add(mesh.Vertices[(int)index] with { Uv2 = layout[corner] });
            }

            outIndices[corner] = at;
        }

        return (outVertices.ToArray(), outIndices);
    }

    private static void CheckUvMesh(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector2> uvs, ReadOnlySpan<uint> indices)
    {
        if (positions.Length != uvs.Length)
        {
            throw new ArgumentException($"There are {positions.Length} positions but {uvs.Length} UVs.", nameof(uvs));
        }

        UnwrapMesh.CheckIndices(indices, positions.Length);
    }

    private static string? OutsideProblem(ReadOnlySpan<Vector2> uvs, ReadOnlySpan<uint> indices)
    {
        for (int corner = 0; corner < indices.Length; corner++)
        {
            var uv = uvs[(int)indices[corner]];
            if (!(uv.X >= 0f && uv.X <= 1f && uv.Y >= 0f && uv.Y <= 1f))
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"Triangle {corner / 3} reaches outside 0 to 1, to ({uv.X:0.###}, {uv.Y:0.###}).");
            }
        }

        return null;
    }

    /// <summary>
    /// Two triangles of one chart that overlap by more than <see cref="TexelOverlap"/> texels. Triangles of one chart
    /// share texels along their edges, so within a chart only real overlap counts.
    /// </summary>
    private static string? OverlapProblem(UvSet set, int size)
    {
        foreach (var triangles in set.Members)
        {
            if (triangles.Count < 2)
            {
                continue;
            }

            var (xs, ys) = set.Texels(triangles, size);
            var overlap = TriangleOverlap.First(xs, ys, triangles.Count, TexelOverlap);
            if (overlap.A >= 0)
            {
                int a = Math.Min(triangles[overlap.A], triangles[overlap.B]);
                int b = Math.Max(triangles[overlap.A], triangles[overlap.B]);
                return string.Create(CultureInfo.InvariantCulture,
                    $"Triangles {a} and {b} overlap at texel ({(int)Math.Floor(overlap.X)}, {(int)Math.Floor(overlap.Y)}).");
            }
        }

        return null;
    }

    /// <summary>One chart curled round so that two far-apart parts of it touch one texel, even without overlapping.</summary>
    private static string? FarShareProblem(UvSet set, int size)
    {
        var far = FarShareSearch.Find(set.Positions, set.Uvs, set.ChartOf, firstOnly: true, size);
        if (far.Count == 0)
        {
            return null;
        }

        var share = far[0];
        return string.Create(CultureInfo.InvariantCulture,
            $"Triangles {share.A} and {share.B} of one chart share texel ({share.X}, {share.Y}) but are {share.Distance:0.###} m apart.");
    }

    private static string? DensityProblem(UvSet set)
    {
        var density = set.Densities();
        if (density.Sparsest < 0)
        {
            return null;
        }

        int sparse = set.Members[density.Sparsest][0];
        int dense = set.Members[density.Densest][0];
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

        return null;
    }
}
