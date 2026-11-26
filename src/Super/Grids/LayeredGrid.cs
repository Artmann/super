using System.Globalization;
using System.Numerics;

namespace Super;

/// <summary>
/// One grid repeated at several heights, such as the floors of a building or the levels of a dungeon. Every layer has
/// the ground grid's cells and turn, raised along its <see cref="GridPlacement.Up"/>, so moving the ground moves them
/// all. See docs/grids.md.
/// </summary>
public sealed class LayeredGrid<TCell>
    where TCell : struct, IGridCell<TCell>
{
    private readonly IGrid<TCell>[] _layers;
    private readonly float[] _heights;

    /// <summary>
    /// Layers of <paramref name="ground"/>'s cells at these heights above it, in metres, lowest first. The ground grid
    /// is kept as it is, so a first height of 0 makes layer 0 the ground itself.
    /// </summary>
    public LayeredGrid(IGrid<TCell> ground, params float[] heights)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(heights);
        if (heights.Length == 0)
        {
            throw new ArgumentException("A layered grid needs at least one height.", nameof(heights));
        }

        for (int i = 0; i < heights.Length; i++)
        {
            if (!float.IsFinite(heights[i]) || (i > 0 && heights[i] <= heights[i - 1]))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"A layered grid's heights must be numbers that go up, but height {i} is {heights[i]}."),
                    nameof(heights));
            }
        }

        _heights = [.. heights];
        _layers = [.. heights.Select(height => ground.MovedTo(Raised(ground.Placement, height)))];
        Placement = ground.Placement;
    }

    /// <summary>How many layers there are.</summary>
    public int Count => _layers.Length;

    /// <summary>The grid of one layer, 0 lowest.</summary>
    public IGrid<TCell> this[int layer] => _layers[layer];

    /// <summary>Where the ground sits. Setting it moves every layer with it.</summary>
    public GridPlacement Placement
    {
        get;
        set
        {
            field = value;
            for (int i = 0; i < _layers.Length; i++)
            {
                _layers[i].Placement = Raised(value, _heights[i]);
            }
        }
    }

    /// <summary>How high a layer is above the ground, in metres.</summary>
    public float HeightOf(int layer) => _heights[layer];

    /// <summary>
    /// The layer a world position stands on: the highest one at or below it, or layer 0 for a point below them all. A
    /// point a centimetre under a floor still counts as on it, so feet that sink a little don't drop a floor.
    /// </summary>
    public int LayerAt(Vector3 world)
    {
        float height = Vector3.Dot(world - Placement.Origin, Placement.Up);
        int layer = 0;
        while (layer + 1 < _heights.Length && _heights[layer + 1] <= height + 0.01f)
        {
            layer++;
        }

        return layer;
    }

    /// <summary>The layer a world position stands on and its cell there.</summary>
    public (int Layer, TCell Cell) CellAt(Vector3 world)
    {
        int layer = LayerAt(world);
        return (layer, _layers[layer].CellAt(world));
    }

    private static GridPlacement Raised(GridPlacement ground, float height) =>
        ground with { Origin = ground.Origin + ground.Up * height };
}
