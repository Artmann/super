using System.Numerics;

namespace Rendition;

/// <summary>
/// Lays the charts out on one page. Flattens every chart, turns it to its smallest bounding rectangle, scales it so a
/// square metre covers the same UV area in every chart, and packs the charts onto the page. Each chart's rectangle has
/// half a texel or more of margin round the chart, so every texel the chart touches is inside its rectangle and the
/// packer's gutter lies between touched texels.
/// </summary>
internal sealed class ChartPage
{
    /// <summary>The empty texels kept between any two charts, which <see cref="LightmapUnwrap.Gutter"/> names.</summary>
    public const int Gutter = 2;

    /// <summary>
    /// The scale search steps by this much until it has one scale that fits and one that doesn't, then halves the
    /// gap between them <see cref="Halvings"/> times.
    /// </summary>
    private const double Shrink = 0.9;

    private const int Halvings = 6;

    private readonly int size;

    /// <summary>Per chart, how it lies flat, or null when it has no area.</summary>
    private readonly LaidChart?[] laid;

    private readonly (int Width, int Height)[] sizes;
    private readonly (int X, int Y)[] places;
    private readonly (int X, int Y)[] bestPlaces;

    /// <summary>The largest scale, in UV units per metre, that fits so far, or -1.</summary>
    private double fits = -1.0;

    /// <summary>The smallest scale that doesn't fit so far, or -1.</summary>
    private double fails = -1.0;

    private ChartPage(UnwrapMesh mesh, List<List<int>> members, int size)
    {
        this.size = size;
        laid = new LaidChart?[members.Count];
        for (int chart = 0; chart < members.Count; chart++)
        {
            laid[chart] = Measure(mesh, members[chart]);
        }

        sizes = new (int Width, int Height)[members.Count];
        places = new (int X, int Y)[members.Count];
        bestPlaces = new (int X, int Y)[members.Count];
    }

    /// <summary>A chart turned to its smallest rectangle: its flat corners, its own scale and its bounds.</summary>
    private sealed record LaidChart(FlatChart Flat, double Scale, double MinX, double MinY, double MaxX, double MaxY);

    /// <summary>Each corner's second UV, with the charts packed at the largest scale that fits.</summary>
    public static Vector2[] Lay(UnwrapMesh mesh, UnwrapCharts charts, int size)
    {
        var page = new ChartPage(mesh, charts.Members, size);
        var (perMetre, cap) = page.FirstScale();
        page.Bracket(perMetre, cap);
        page.Narrow();
        return page.Uvs(mesh.Indices.Length, charts.Members);
    }

    /// <summary>
    /// The texels a chart's rectangle takes along one side, for a chart that many texels long, and where the chart
    /// starts in it. The rectangle is the length rounded up plus one, with the chart in the middle, so at least half a
    /// texel lies on each side. Without it, a chart a whole number of texels long has its ends on texel lines, and the
    /// texels just past them count as touched, outside the rectangle and into the gutter.
    /// </summary>
    internal static (int Size, double Offset) ChartRectangle(double extentTexels)
    {
        int size = (int)Math.Ceiling(extentTexels) + 1;
        return (size, (size - extentTexels) * 0.5);
    }

    private static LaidChart? Measure(UnwrapMesh mesh, List<int> members)
    {
        double world = UnwrapMesh.WorldArea(mesh.Cross, mesh.Zero, members);
        if (world <= 0.0)
        {
            return null;
        }

        var flat = mesh.Flatten(members);
        double flatArea = 0.0;
        for (int k = 0; k < members.Count; k++)
        {
            if (!mesh.Zero[members[k]])
            {
                flatArea += UnwrapMath.SignedArea(flat.X, flat.Y, k * 3);
            }
        }

        SmallestRectangle.Turn(flat.X, flat.Y);

        // Flattening shrinks tilted triangles, so each chart grows back by its own share to keep density even.
        double scale = Math.Sqrt(world / flatArea);
        return new LaidChart(
            flat, scale, UnwrapMath.Min(flat.X), UnwrapMath.Min(flat.Y), UnwrapMath.Max(flat.X), UnwrapMath.Max(flat.Y));
    }

    /// <summary>
    /// UV units per metre to start from: where the charts' rectangles, with their margins and gutters, would fill the
    /// page, and no more than the cap at which the longest just fits.
    /// </summary>
    private (double PerMetre, double Cap) FirstScale()
    {
        double boxArea = 0.0;
        double longest = 0.0;
        foreach (var chart in laid)
        {
            if (chart is null)
            {
                continue;
            }

            double width = (chart.MaxX - chart.MinX) * chart.Scale;
            double height = (chart.MaxY - chart.MinY) * chart.Scale;
            boxArea += width * height;
            longest = Math.Max(longest, Math.Max(width, height));
        }

        double cap = longest > 0.0 ? (size - 1.0) / (size * longest) : 1.0;
        double perMetre = cap;
        if (boxArea > 0.0)
        {
            perMetre = Math.Min(cap, FillingTexels(boxArea) / size);
        }

        return (perMetre, cap);
    }

    /// <summary>
    /// Texels per metre at which the charts would fill the page. In texels t, the page holds
    /// Σ (w·t + 3)(h·t + 3) = size², for the charts' sides w and h in metres.
    /// </summary>
    private double FillingTexels(double boxArea)
    {
        const double margin = 1.0 + Gutter;
        double sumSides = 0.0;
        foreach (var chart in laid)
        {
            if (chart is not null)
            {
                sumSides += (chart.MaxX - chart.MinX + chart.MaxY - chart.MinY) * chart.Scale;
            }
        }

        double page = (double)size * size - laid.Length * margin * margin;
        double b = sumSides * margin;
        double t = page > 0.0 ? (-b + Math.Sqrt(b * b + 4.0 * boxArea * page)) / (2.0 * boxArea) : 1e-6;
        return Math.Max(t, 1e-6);
    }

    /// <summary>
    /// Steps the scale by <see cref="Shrink"/> until it has one that fits and one that doesn't, or the cap fits.
    /// Throws when the charts don't fit even at their smallest.
    /// </summary>
    private void Bracket(double perMetre, double cap)
    {
        for (int attempts = 1; ; attempts++)
        {
            bool smallest = Size(perMetre);
            if (Packs())
            {
                fits = perMetre;
                if (fails > 0.0 || perMetre >= cap)
                {
                    return;
                }

                perMetre = Math.Min(cap, perMetre / Shrink);
                continue;
            }

            fails = perMetre;
            if (fits > 0.0)
            {
                return;
            }

            if (smallest || attempts > 500)
            {
                throw new InvalidOperationException(
                    $"The mesh's {laid.Length} lightmap charts don't fit a {size}-texel page with a {Gutter}-texel gutter, even at their smallest.");
            }

            perMetre *= Shrink;
        }
    }

    /// <summary>Narrows the gap between the largest scale that fits and the smallest that doesn't.</summary>
    private void Narrow()
    {
        for (int halving = 0; halving < Halvings && fails > 0.0; halving++)
        {
            double perMetre = (fits + fails) * 0.5;
            Size(perMetre);
            if (Packs())
            {
                fits = perMetre;
            }
            else
            {
                fails = perMetre;
            }
        }
    }

    /// <summary>Sizes every chart's rectangle at the given scale; whether all are already as small as they get.</summary>
    private bool Size(double perMetre)
    {
        bool smallest = true;
        for (int chart = 0; chart < laid.Length; chart++)
        {
            if (laid[chart] is not { } flat)
            {
                sizes[chart] = (1, 1);
                continue;
            }

            double texels = perMetre * flat.Scale * size;
            int width = ChartRectangle((flat.MaxX - flat.MinX) * texels).Size;
            int height = ChartRectangle((flat.MaxY - flat.MinY) * texels).Size;
            sizes[chart] = (width, height);
            smallest &= width <= 2 && height <= 2;
        }

        return smallest;
    }

    /// <summary>Packs the rectangles as sized, keeping where they went when they fit.</summary>
    private bool Packs()
    {
        if (!SkylinePacker.TryPack(sizes, size, size, Gutter, places))
        {
            return false;
        }

        places.CopyTo(bestPlaces, 0);
        return true;
    }

    /// <summary>
    /// Each corner's second UV, at the largest scale that fit. A chart with no area sits in the middle of its one
    /// texel.
    /// </summary>
    private Vector2[] Uvs(int cornerCount, List<List<int>> members)
    {
        var uvs = new Vector2[cornerCount];
        for (int chart = 0; chart < laid.Length; chart++)
        {
            var place = bestPlaces[chart];
            if (laid[chart] is { } flat)
            {
                Place(uvs, members[chart], flat, place);
                continue;
            }

            var middle = new Vector2((place.X + 0.5f) / size, (place.Y + 0.5f) / size);
            foreach (int triangle in members[chart])
            {
                for (int i = 0; i < 3; i++)
                {
                    uvs[triangle * 3 + i] = middle;
                }
            }
        }

        return uvs;
    }

    private void Place(Vector2[] uvs, List<int> members, LaidChart chart, (int X, int Y) place)
    {
        double texels = fits * chart.Scale * size;
        double left = place.X + ChartRectangle((chart.MaxX - chart.MinX) * texels).Offset;
        double top = place.Y + ChartRectangle((chart.MaxY - chart.MinY) * texels).Offset;
        for (int k = 0; k < members.Count; k++)
        {
            for (int i = 0; i < 3; i++)
            {
                double u = left + (chart.Flat.X[k * 3 + i] - chart.MinX) * texels;
                double v = top + (chart.Flat.Y[k * 3 + i] - chart.MinY) * texels;
                uvs[members[k] * 3 + i] = new Vector2((float)(u / size), (float)(v / size));
            }
        }
    }
}
