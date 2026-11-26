using System.Globalization;
using System.Numerics;

namespace Super;

/// <summary>Which way a hexagon's corners point on a <see cref="HexGrid"/>.</summary>
public enum HexTop
{
    /// <summary>A corner points along the grid's Z, so rows of hexes run along X.</summary>
    Pointy,

    /// <summary>A side faces along the grid's Z, so columns of hexes run along Z.</summary>
    Flat,
}

/// <summary>
/// A grid of hexagons lying on its <see cref="Placement"/>, such as an overworld map or a tactics board. Hex 0, 0 has
/// its middle at the grid's origin. <see cref="Hex.Q"/> steps along X, and <see cref="Hex.R"/> steps towards +Z. A game
/// can have as many as it needs. See docs/grids.md.
/// </summary>
public sealed class HexGrid
{
    private static readonly float Root3 = MathF.Sqrt(3f);

    /// <summary>A grid of hexagons <paramref name="size"/> metres from the middle to each corner.</summary>
    public HexGrid(float size, HexTop top = HexTop.Pointy, GridPlacement placement = default)
    {
        if (!(float.IsFinite(size) && size > 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                string.Create(CultureInfo.InvariantCulture, $"A hex grid's hexagons must be more than 0 metres from the middle to a corner, but they are {size}."));
        }

        if (!Enum.IsDefined(top))
        {
            throw new ArgumentOutOfRangeException(nameof(top), $"A hexagon's top is pointy or flat, but it is {(int)top}.");
        }

        Size = size;
        Top = top;
        Placement = placement;
    }

    /// <summary>How far it is from a hexagon's middle to each of its corners, in metres.</summary>
    public float Size { get; }

    /// <summary>Which way the hexagons' corners point.</summary>
    public HexTop Top { get; }

    /// <summary>Where the grid sits. Set it again to move the grid with whatever it lies on.</summary>
    public GridPlacement Placement { get; set; }

    /// <summary>The hex a world position lies over or under.</summary>
    public Hex HexAt(Vector3 world) => HexOn(Placement.OnGrid(world));

    /// <summary>The hex a ray, such as the mouse's, meets the grid in, or nothing when it misses the grid's plane.</summary>
    public Hex? HexAt(Ray ray) => Placement.Hit(ray) is { } onGrid ? HexOn(onGrid) : null;

    /// <summary>The world position of a hex's middle, on the grid.</summary>
    public Vector3 Center(Hex hex) => Placement.InWorld(Middle(hex));

    /// <summary>A hex's six corners in the world, going round from +X towards +Z.</summary>
    public void Corners(Hex hex, Span<Vector3> corners)
    {
        if (corners.Length < 6)
        {
            throw new ArgumentException($"A hexagon has 6 corners, but there is room for {corners.Length}.", nameof(corners));
        }

        var middle = Middle(hex);
        float start = Top == HexTop.Pointy ? 30f : 0f;
        for (int corner = 0; corner < 6; corner++)
        {
            float angle = float.DegreesToRadians(start + 60f * corner);
            corners[corner] = Placement.InWorld(middle + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * Size);
        }
    }

    private Vector2 Middle(Hex hex) => Top == HexTop.Pointy
        ? new Vector2(Root3 * hex.Q + Root3 / 2f * hex.R, 1.5f * hex.R) * Size
        : new Vector2(1.5f * hex.Q, Root3 / 2f * hex.Q + Root3 * hex.R) * Size;

    private Hex HexOn(Vector2 onGrid)
    {
        var at = onGrid / Size;
        return Top == HexTop.Pointy
            ? Hex.Round(Root3 / 3f * at.X - at.Y / 3f, 2f / 3f * at.Y)
            : Hex.Round(2f / 3f * at.X, -at.X / 3f + Root3 / 3f * at.Y);
    }
}
