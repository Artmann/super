using System.Numerics;

namespace Rendition;

/// <summary>
/// Where a static draw's lightmap sits: its page, and the scale and offset that take the draw's second UV set
/// (<see cref="Vertex.Uv2"/>, 0 to 1) to 0 to 1 on that page. A UV set of 0 to 1 covers exactly the rectangle, so the
/// gutter lies outside it.
/// </summary>
public readonly record struct LightmapRect(int Page, Vector2 Scale, Vector2 Offset);

/// <summary>
/// Gives every static draw a square on a lightmap page. See "Lightmap layout" in docs/design.md.
/// </summary>
public sealed class LightmapLayout
{
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
    /// <param name="meshArea">A mesh's surface area in its own units, as <see cref="SurfaceArea"/> measures it.</param>
    /// <param name="getsLightmap">
    /// Whether a material may have a lightmap, or null for all. Materials that sway in the wind or are see-through
    /// don't, and a draw with one gets no rectangle and isn't in <see cref="Left"/>.
    /// </param>
    public static LightmapLayout Build(
        ReadOnlySpan<DrawItem> draws, Func<MeshHandle, float> meshArea, BakeSettings settings,
        Func<MaterialHandle, bool>? getsLightmap = null)
    {
        var layout = new LightmapLayout();
        var groupOf = new Dictionary<(ulong Instance, Matrix4x4 Transform, MaterialHandle Material), int>();
        var groups = new List<Group>();
        var drawGroup = new List<(ulong BakeId, int Group)>();
        var seen = new HashSet<ulong>();

        foreach (ref readonly var draw in draws)
        {
            if (draw.BakeId == 0 || !seen.Add(draw.BakeId) || (getsLightmap is not null && !getsLightmap(draw.Material)))
            {
                continue;
            }

            var key = (BakeIds.Instance(draw.BakeId), draw.Transform, draw.Material);
            float area = meshArea(draw.Mesh) * (float)Math.Pow(Math.Abs(Determinant(draw.Transform)), 2.0 / 3.0);
            if (!groupOf.TryGetValue(key, out int index))
            {
                index = groups.Count;
                groupOf[key] = index;
                groups.Add(new Group { Area = area });
            }
            else
            {
                // Parts with different meshes share the rectangle, so it must hold the biggest.
                groups[index].Area = Math.Max(groups[index].Area, area);
            }

            drawGroup.Add((draw.BakeId, index));
        }

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
    /// A mesh's surface area: its triangles' areas added up, in the mesh's own units, so a transform's scale is not in
    /// it. <see cref="Build"/> multiplies by the transform's.
    /// </summary>
    public static float SurfaceArea(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
    {
        double total = 0;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            var a = vertices[(int)indices[i]].Position;
            var b = vertices[(int)indices[i + 1]].Position;
            var c = vertices[(int)indices[i + 2]].Position;
            total += 0.5 * Vector3.Cross(b - a, c - a).Length();
        }

        return (float)total;
    }

    // The most groups, from the front of the order, that fit one page together. The packer takes all or nothing, so this
    // tries all of them and then bisects on how many.
    private static int Fit(List<int> order, List<Group> groups, out (int X, int Y)[] positions)
    {
        var sizes = new (int Width, int Height)[order.Count];
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = (groups[order[i]].Side, groups[order[i]].Side);
        }

        positions = new (int X, int Y)[sizes.Length];
        if (SkylinePacker.TryPack(sizes, PageSize, PageSize, LightmapUnwrap.Gutter, positions))
        {
            return sizes.Length;
        }

        // One rectangle always fits, since MaxSide is under the page, so the low end holds.
        int low = 1;
        int high = sizes.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (SkylinePacker.TryPack(sizes.AsSpan(0, middle), PageSize, PageSize, LightmapUnwrap.Gutter, positions))
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        SkylinePacker.TryPack(sizes.AsSpan(0, low), PageSize, PageSize, LightmapUnwrap.Gutter, positions);
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
        public int Side;
        public bool Placed;
        public LightmapRect Rect;
    }
}
