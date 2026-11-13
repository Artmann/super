using System.Globalization;
using System.Numerics;

namespace Rendition;

/// <summary>
/// Where a static draw's lightmap sits: its page, and the scale and offset that take the draw's second UV set
/// (<see cref="Vertex.Uv2"/>, 0 to 1) to 0 to 1 on that page. A UV set of 0 to 1 covers exactly the rectangle, so the
/// gutter lies outside it.
/// </summary>
public readonly record struct LightmapRect(int Page, Vector2 Scale, Vector2 Offset);

/// <summary>
/// One chart for <see cref="LightmapLayout.Single"/>: the id it is found by, and its surface area in square metres.
/// </summary>
public readonly record struct LightmapChart(ulong BakeId, float Area);

/// <summary>
/// Gives every static draw a square on a lightmap page. See "Lightmap layout" in docs/design.md.
/// </summary>
public sealed class LightmapLayout
{
    /// <summary>The smallest page <see cref="Single"/> uses, in texels.</summary>
    public const int MinSinglePageSide = 64;

    /// <summary>A page's side, in texels.</summary>
    public const int PageSize = 2048;

    /// <summary>The most pages a layout uses. Draws that don't fit in them are in <see cref="Left"/>.</summary>
    public const int MaxPages = 8;

    /// <summary>The smallest side a rectangle gets, in texels.</summary>
    public const int MinSide = 8;

    /// <summary>The largest side a rectangle gets, in texels.</summary>
    public const int MaxSide = 1024;

    private readonly List<(ulong BakeId, LightmapRect Rect)> _draws = [];
    private readonly Dictionary<ulong, LightmapRect> _byId = [];
    private readonly List<ulong> _left = [];

    private LightmapLayout()
    {
    }

    /// <summary>The number of pages used, 0 when no draw has a rectangle.</summary>
    public int Pages { get; private set; }

    /// <summary>A page's side, in texels: <see cref="PageSize"/> for <see cref="Build"/>, the chosen side for <see cref="Single"/>.</summary>
    public int PageSide { get; private set; } = PageSize;

    /// <summary>Every placed draw's bake id and rectangle, in the order of the draws given. Parts that share one share its rectangle.</summary>
    public IReadOnlyList<(ulong BakeId, LightmapRect Rect)> Draws => _draws;

    /// <summary>The bake ids of the draws that didn't fit in <see cref="MaxPages"/> pages, in the order of the draws given.</summary>
    public IReadOnlyList<ulong> Left => _left;

    /// <summary>The rectangle of the draw with this bake id, or false when it has none.</summary>
    public bool TryGet(ulong bakeId, out LightmapRect rect) => _byId.TryGetValue(bakeId, out rect);

    /// <summary>
    /// Lays out every static draw (non-zero <see cref="DrawItem.BakeId"/>) that <paramref name="getsLightmap"/> allows.
    /// Draws of one instance, in one place, with one material share a rectangle, since each material's second UVs fill
    /// their own 0 to 1. A rectangle is square, <see cref="BakeSettings.TexelsPerMetre"/> texels to each metre of the
    /// surface's side, clamped to <see cref="MinSide"/> and <see cref="MaxSide"/>. Rectangles are packed page by page
    /// with <see cref="SkylinePacker"/> and <see cref="LightmapUnwrap.Gutter"/> texels between them.
    /// </summary>
    /// <param name="meshArea">
    /// The surface area, in the mesh's own units, of the triangles a draw covers: the mesh, its first index and its
    /// index count, as <see cref="RangeArea"/> measures them. A rectangle's area is the sum over the draws that share it.
    /// </param>
    /// <param name="getsLightmap">
    /// Whether a material may have a lightmap. Materials that sway in the wind or are see-through don't, and a draw with
    /// one gets no rectangle and isn't in <see cref="Left"/>. The caller decides, since a draw doesn't say.
    /// </param>
    /// <exception cref="InvalidOperationException">Two different meshes share an instance, transform and material.</exception>
    public static LightmapLayout Build(
        ReadOnlySpan<DrawItem> draws, Func<MeshHandle, uint, uint, float> meshArea, BakeSettings settings,
        Func<MaterialHandle, bool> getsLightmap)
    {
        var layout = new LightmapLayout();
        var (groups, drawGroup) = GroupDraws(draws, meshArea, getsLightmap);

        foreach (var group in groups)
        {
            group.Side = SideOf(group.Area, settings.TexelsPerMetre);
        }

        var order = Enumerable.Range(0, groups.Count).OrderByDescending(i => groups[i].Side).ThenBy(i => i).ToList();
        for (int page = 0; page < MaxPages && order.Count > 0; page++)
        {
            int fits = Fit(order, groups, out var positions);
            for (int i = 0; i < fits; i++)
            {
                var group = groups[order[i]];
                group.Placed = true;
                group.Rect = new LightmapRect(
                    page,
                    new Vector2((float)group.Side / PageSize),
                    new Vector2((float)positions[i].X / PageSize, (float)positions[i].Y / PageSize));
            }

            order.RemoveRange(0, fits);
            layout.Pages = page + 1;
        }

        foreach (var (bakeId, index) in drawGroup)
        {
            if (groups[index].Placed)
            {
                layout._draws.Add((bakeId, groups[index].Rect));
                layout._byId[bakeId] = groups[index].Rect;
            }
            else
            {
                layout._left.Add(bakeId);
            }
        }

        return layout;
    }

    /// <summary>
    /// Packs into one square page, the smallest power of two from <see cref="MinSinglePageSide"/> to
    /// <see cref="PageSize"/> that holds every chart at the density. Each chart gets a square of
    /// <paramref name="texelsPerMetre"/> texels to each metre of its side, clamped to <see cref="MinSide"/> and
    /// <see cref="MaxSide"/>, with <see cref="LightmapUnwrap.Gutter"/> texels between squares. Charts that don't fit
    /// even at <see cref="PageSize"/> are in <see cref="Left"/>, in the order given: the largest squares that fit together are kept, and then each smaller one that still fits beside them.
    /// A layout with no charts has <see cref="MinSinglePageSide"/> and no pages.
    /// </summary>
    public static LightmapLayout Single(ReadOnlySpan<LightmapChart> charts, int texelsPerMetre)
    {
        var layout = new LightmapLayout { PageSide = MinSinglePageSide };
        var groups = new List<Group>(charts.Length);
        foreach (var chart in charts)
        {
            groups.Add(new Group { Side = SideOf(chart.Area, texelsPerMetre) });
        }

        var order = Enumerable.Range(0, groups.Count).OrderByDescending(i => groups[i].Side).ThenBy(i => i).ToList();
        if (order.Count == 0)
        {
            return layout;
        }

        var positions = Array.Empty<(int X, int Y)>();
        var fits = 0;
        for (int side = MinSinglePageSide; side <= PageSize; side *= 2)
        {
            fits = Fit(order, groups, side, out positions);
            layout.PageSide = side;
            if (fits == order.Count)
            {
                break;
            }
        }

        // Single has no next page, so a chart the prefix left out is tried again beside the ones kept, in the order
        // given by size and then by index, and stays if everything still packs.
        if (fits < order.Count)
        {
            var kept = order.GetRange(0, fits);
            for (int j = fits; j < order.Count; j++)
            {
                kept.Add(order[j]);
                var sizes = kept.Select(i => (groups[i].Side, groups[i].Side)).ToArray();
                var trial = new (int X, int Y)[sizes.Length];
                if (SkylinePacker.TryPack(sizes, layout.PageSide, layout.PageSide, LightmapUnwrap.Gutter, trial))
                {
                    positions = trial;
                }
                else
                {
                    kept.RemoveAt(kept.Count - 1);
                }
            }

            order = kept;
            fits = kept.Count;
        }

        for (int i = 0; i < fits; i++)
        {
            var group = groups[order[i]];
            group.Placed = true;
            group.Rect = new LightmapRect(
                0,
                new Vector2((float)group.Side / layout.PageSide),
                new Vector2((float)positions[i].X / layout.PageSide, (float)positions[i].Y / layout.PageSide));
        }

        layout.Pages = 1;
        for (int i = 0; i < charts.Length; i++)
        {
            if (groups[i].Placed)
            {
                layout._draws.Add((charts[i].BakeId, groups[i].Rect));
                layout._byId[charts[i].BakeId] = groups[i].Rect;
            }
            else
            {
                layout._left.Add(charts[i].BakeId);
            }
        }

        return layout;
    }

    /// <summary>
    /// Lays out a chunk's static draws on one page with <see cref="Single"/>: draws share rectangles exactly as in
    /// <see cref="Build"/>, and each shared rectangle is one chart. A draw whose chart doesn't fit even at
    /// <see cref="PageSize"/> is in <see cref="Left"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Two different meshes share an instance, transform and material.</exception>
    public static LightmapLayout BuildSingle(
        ReadOnlySpan<DrawItem> draws, Func<MeshHandle, uint, uint, float> meshArea, int texelsPerMetre,
        Func<MaterialHandle, bool> getsLightmap)
    {
        var (groups, drawGroup) = GroupDraws(draws, meshArea, getsLightmap);
        var charts = new LightmapChart[groups.Count];
        for (int i = 0; i < charts.Length; i++)
        {
            charts[i] = new LightmapChart(groups[i].FirstId, groups[i].Area);
        }

        var single = Single(charts, texelsPerMetre);
        var layout = new LightmapLayout { PageSide = single.PageSide, Pages = single.Pages };
        foreach (var (bakeId, index) in drawGroup)
        {
            if (single.TryGet(groups[index].FirstId, out var rect))
            {
                layout._draws.Add((bakeId, rect));
                layout._byId[bakeId] = rect;
            }
            else
            {
                layout._left.Add(bakeId);
            }
        }

        return layout;
    }

    /// <summary>
    /// This one-page layout drawn at the top left of a <see cref="PageSize"/> page: every rectangle at the same texels,
    /// but as a share of <see cref="PageSize"/>. The baker works on pages of <see cref="PageSize"/>, so a smaller page
    /// is baked in that corner and cut out after. Sides are powers of two, so the shares stay exact.
    /// </summary>
    internal LightmapLayout OnFullPage()
    {
        float scale = (float)PageSide / PageSize;
        var layout = new LightmapLayout { Pages = Pages };
        foreach (var (bakeId, rect) in _draws)
        {
            var moved = rect with { Scale = rect.Scale * scale, Offset = rect.Offset * scale };
            layout._draws.Add((bakeId, moved));
            layout._byId[bakeId] = moved;
        }

        return layout;
    }

    /// <summary>
    /// The running total of a mesh's triangle areas, in the mesh's own units, so a transform's scale is not in it:
    /// entry <c>t</c> is the area of the first <c>t</c> triangles, and there is one more entry than triangles.
    /// <see cref="RangeArea"/> reads a range from it.
    /// </summary>
    public static float[] CumulativeAreas(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
    {
        var totals = new float[indices.Length / 3 + 1];
        double total = 0;
        for (int t = 0; t < totals.Length - 1; t++)
        {
            var a = vertices[(int)indices[t * 3]].Position;
            var b = vertices[(int)indices[t * 3 + 1]].Position;
            var c = vertices[(int)indices[t * 3 + 2]].Position;
            total += 0.5 * Vector3.Cross(b - a, c - a).Length();
            totals[t + 1] = (float)total;
        }

        return totals;
    }

    /// <summary>
    /// The area of the triangles in index range <paramref name="firstIndex"/> for <paramref name="indexCount"/>
    /// indices, from <see cref="CumulativeAreas"/>. A range past the mesh's end counts only what the mesh has.
    /// </summary>
    public static float RangeArea(float[] cumulative, uint firstIndex, uint indexCount)
    {
        int last = cumulative.Length - 1;
        int from = (int)Math.Min(firstIndex / 3, (uint)last);
        int to = (int)Math.Min(((ulong)firstIndex + indexCount) / 3, (ulong)last);
        return to > from ? cumulative[to] - cumulative[from] : 0f;
    }

    // The draws that get a lightmap, grouped into the rectangles they share: one instance, in one place, with one material.
    private static (List<Group> Groups, List<(ulong BakeId, int Group)> DrawGroup) GroupDraws(
        ReadOnlySpan<DrawItem> draws, Func<MeshHandle, uint, uint, float> meshArea, Func<MaterialHandle, bool> getsLightmap)
    {
        var groupOf = new Dictionary<(ulong Instance, Matrix4x4 Transform, MaterialHandle Material), int>();
        var groups = new List<Group>();
        var drawGroup = new List<(ulong BakeId, int Group)>();
        var seen = new HashSet<ulong>();

        foreach (ref readonly var draw in draws)
        {
            if (draw.BakeId == 0 || !seen.Add(draw.BakeId) || !getsLightmap(draw.Material))
            {
                continue;
            }

            var key = (BakeIds.Instance(draw.BakeId), draw.Transform, draw.Material);
            float area = meshArea(draw.Mesh, draw.FirstIndex, draw.IndexCount)
                * (float)Math.Pow(Math.Abs(Determinant(draw.Transform)), 2.0 / 3.0);
            if (!groupOf.TryGetValue(key, out int index))
            {
                index = groups.Count;
                groupOf[key] = index;
                groups.Add(new Group { Area = area, Mesh = draw.Mesh, FirstId = draw.BakeId });
            }
            else
            {
                // A rectangle holds one 0 to 1 of one mesh's second UVs. Two meshes in it would write over each other.
                if (groups[index].Mesh != draw.Mesh)
                {
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                        $"Draws of instance 0x{key.Item1:X16} use two different meshes with the same material and transform, so they would share one lightmap rectangle. Each mesh's second UVs fill their own 0 to 1, so give them different materials."));
                }

                // Parts of one mesh with one material are one unwrap, so the rectangle covers all their triangles.
                groups[index].Area += area;
            }

            drawGroup.Add((draw.BakeId, index));
        }

        return (groups, drawGroup);
    }

    // The most groups, from the front of the order, that fit one page together. The packer takes all or nothing, so this
    // tries all of them and then bisects on how many.
    private static int Fit(List<int> order, List<Group> groups, out (int X, int Y)[] positions)
    {
        return Fit(order, groups, PageSize, out positions);
    }

    private static int Fit(List<int> order, List<Group> groups, int pageSide, out (int X, int Y)[] positions)
    {
        var sizes = new (int Width, int Height)[order.Count];
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = (groups[order[i]].Side, groups[order[i]].Side);
        }

        positions = new (int X, int Y)[sizes.Length];
        if (SkylinePacker.TryPack(sizes, pageSide, pageSide, LightmapUnwrap.Gutter, positions))
        {
            return sizes.Length;
        }

        // On a full page one rectangle always fits, since MaxSide is under it. On a smaller one the first may not.
        if (!SkylinePacker.TryPack(sizes.AsSpan(0, 1), pageSide, pageSide, LightmapUnwrap.Gutter, positions))
        {
            return 0;
        }

        int low = 1;
        int high = sizes.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (SkylinePacker.TryPack(sizes.AsSpan(0, middle), pageSide, pageSide, LightmapUnwrap.Gutter, positions))
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        SkylinePacker.TryPack(sizes.AsSpan(0, low), pageSide, pageSide, LightmapUnwrap.Gutter, positions);
        return low;
    }

    private static int SideOf(float area, int texelsPerMetre)
    {
        if (!float.IsFinite(area) || area <= 0f)
        {
            return MinSide;
        }

        double side = Math.Sqrt(area) * texelsPerMetre;
        return (int)Math.Clamp(Math.Round(side), MinSide, MaxSide);
    }

    // The determinant of the transform's 3 by 3, which is how much it scales volume.
    private static double Determinant(in Matrix4x4 m) =>
        (double)m.M11 * ((double)m.M22 * m.M33 - (double)m.M23 * m.M32)
        - (double)m.M12 * ((double)m.M21 * m.M33 - (double)m.M23 * m.M31)
        + (double)m.M13 * ((double)m.M21 * m.M32 - (double)m.M22 * m.M31);

    private sealed class Group
    {
        public float Area;
        public MeshHandle Mesh;
        public ulong FirstId;
        public int Side;
        public bool Placed;
        public LightmapRect Rect;
    }
}
