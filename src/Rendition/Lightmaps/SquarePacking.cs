using System.Numerics;

namespace Rendition;

/// <summary>
/// One rectangle of a <see cref="LightmapLayout"/>: the draws or chart it holds, its side in texels, and once packed
/// where it went.
/// </summary>
internal sealed class LightmapSquare
{
    public float Area;
    public MeshHandle Mesh;
    public ulong FirstId;
    public int Side;

    /// <summary>The square's rectangle, or null until it finds a place.</summary>
    public LightmapRect? Rect;
}

/// <summary>
/// Packs <see cref="LightmapSquare"/>s onto square pages with <see cref="SkylinePacker"/>, largest first, with
/// <see cref="LightmapUnwrap.Gutter"/> texels between them. Part of <see cref="LightmapLayout"/>.
/// </summary>
internal static class SquarePacking
{
    /// <summary>
    /// Packs the squares page by page, largest first, onto at most <paramref name="maxPages"/> pages of
    /// <paramref name="pageSide"/> texels, and gives the pages used. Squares left over stay unplaced.
    /// </summary>
    public static int PackPages(List<LightmapSquare> squares, int pageSide, int maxPages)
    {
        var order = BySide(squares);
        int pages = 0;
        for (int page = 0; page < maxPages && order.Count > 0; page++)
        {
            int fits = Fit(order, squares, pageSide, out var positions);
            for (int i = 0; i < fits; i++)
            {
                Place(squares[order[i]], positions[i], page, pageSide);
            }

            order.RemoveRange(0, fits);
            pages = page + 1;
        }

        return pages;
    }

    /// <summary>
    /// Packs the squares onto one page, the smallest power of two from <paramref name="minSide"/> to
    /// <paramref name="maxSide"/> that holds them all, and gives its side. When none does, the largest squares that fit
    /// together are kept, and then each smaller one that still fits beside them; the rest stay unplaced.
    /// </summary>
    public static int PackOnePage(List<LightmapSquare> squares, int minSide, int maxSide)
    {
        var (kept, positions, side) = FitOnePage(BySide(squares), squares, minSide, maxSide);
        for (int i = 0; i < kept.Count; i++)
        {
            Place(squares[kept[i]], positions[i], 0, side);
        }

        return side;
    }

    // The smallest page side that holds every square, or maxSide when none does, with the squares kept on it and
    // where they go.
    private static (List<int> Kept, (int X, int Y)[] Positions, int Side) FitOnePage(
        List<int> order, List<LightmapSquare> squares, int minSide, int maxSide)
    {
        int side = minSide;
        int fits = Fit(order, squares, side, out var positions);
        while (fits < order.Count && side < maxSide)
        {
            side *= 2;
            fits = Fit(order, squares, side, out positions);
        }

        if (fits == order.Count)
        {
            return (order, positions, side);
        }

        // One page has no next page, so a square the prefix left out is tried again beside the ones kept, in the order
        // given by size and then by index, and stays if everything still packs.
        var kept = order.GetRange(0, fits);
        for (int j = fits; j < order.Count; j++)
        {
            kept.Add(order[j]);
            var sizes = kept.Select(i => (squares[i].Side, squares[i].Side)).ToArray();
            var trial = new (int X, int Y)[sizes.Length];
            if (SkylinePacker.TryPack(sizes, side, side, LightmapUnwrap.Gutter, trial))
            {
                positions = trial;
            }
            else
            {
                kept.RemoveAt(kept.Count - 1);
            }
        }

        return (kept, positions, side);
    }

    // The squares' indices, largest side first, then in the order given.
    private static List<int> BySide(List<LightmapSquare> squares) =>
        Enumerable.Range(0, squares.Count).OrderByDescending(i => squares[i].Side).ThenBy(i => i).ToList();

    // Gives a square its rectangle on a page of the given side, its top left corner at the position.
    private static void Place(LightmapSquare square, (int X, int Y) position, int page, int pageSide)
    {
        square.Rect = new LightmapRect(
            page,
            new Vector2((float)square.Side / pageSide),
            new Vector2((float)position.X / pageSide, (float)position.Y / pageSide));
    }

    // The most squares, from the front of the order, that fit one page together. The packer takes all or nothing, so
    // this tries all of them and then bisects on how many.
    private static int Fit(List<int> order, List<LightmapSquare> squares, int pageSide, out (int X, int Y)[] positions)
    {
        var sizes = new (int Width, int Height)[order.Count];
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = (squares[order[i]].Side, squares[order[i]].Side);
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
}
