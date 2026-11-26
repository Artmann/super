using System.Numerics;

namespace Super;

/// <summary>Where a ray met one of the grids in <see cref="Grids"/>.</summary>
/// <param name="Name">The name the grid was added under.</param>
/// <param name="Grid">The grid itself.</param>
/// <param name="Point">Where the ray met the grid, in the world.</param>
/// <param name="Distance">How far along the ray that is, in lengths of its direction.</param>
public readonly record struct GridHit(string Name, IGrid Grid, Vector3 Point, float Distance)
{
    /// <summary>The cell the ray met, such as a <see cref="Cell"/> or a <see cref="Hex"/>, for the grid's kind of cell.</summary>
    public TCell Cell<TCell>()
        where TCell : struct, IGridCell<TCell> =>
        Grid is IGrid<TCell> grid
            ? grid.CellAt(Point)
            : throw new InvalidOperationException($"Grid {Name} is a {Grid.GetType().Name}, which has no {typeof(TCell).Name} cells.");
}

/// <summary>
/// The grids a game names, such as a 2 m building grid and a 0.5 m farm grid, so any code can find one by name and
/// the mouse can find which grid it's over. It's a service, so ask for it in a constructor. See docs/grids.md.
/// </summary>
public sealed class Grids
{
    private readonly Dictionary<string, Entry> _grids = [];

    /// <summary>The names of the grids.</summary>
    public IEnumerable<string> Names => _grids.Keys;

    /// <summary>
    /// Adds a grid under a name. A grid's plane has no edge, so <paramref name="covers"/> can say which cells it has,
    /// such as only those on a ship's deck, and <see cref="Under"/> then looks past the rest. Throws for a name in use.
    /// </summary>
    public void Add<TCell>(string name, IGrid<TCell> grid, Func<TCell, bool>? covers = null)
        where TCell : struct, IGridCell<TCell>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(grid);
        Func<Vector3, bool> has = covers is null ? _ => true : world => covers(grid.CellAt(world));
        if (!_grids.TryAdd(name, new Entry(grid, has)))
        {
            throw new ArgumentException($"There is already a grid called {name}. Remove it first to replace it.", nameof(name));
        }
    }

    /// <summary>Removes a grid. Says whether there was one by that name.</summary>
    public bool Remove(string name) => _grids.Remove(name);

    /// <summary>The grid with this name. Throws when there is none, or when its cells aren't <typeparamref name="TCell"/>.</summary>
    public IGrid<TCell> Get<TCell>(string name)
        where TCell : struct, IGridCell<TCell>
    {
        if (!_grids.TryGetValue(name, out var entry))
        {
            string known = _grids.Count == 0 ? "There are no grids yet." : $"There are: {string.Join(", ", _grids.Keys)}.";
            throw new KeyNotFoundException($"There is no grid called {name}. {known}");
        }

        return entry.Grid as IGrid<TCell>
            ?? throw new InvalidOperationException($"Grid {name} is a {entry.Grid.GetType().Name}, which has no {typeof(TCell).Name} cells.");
    }

    /// <summary>
    /// The grid a ray meets first, such as the mouse's from <see cref="Camera.Ray"/>, looking past cells a grid doesn't
    /// cover, or nothing when it meets none.
    /// </summary>
    public GridHit? Under(Ray ray)
    {
        GridHit? nearest = null;
        foreach (var (name, entry) in _grids)
        {
            if (entry.Grid.Placement.Along(ray) is not { } along || along >= (nearest?.Distance ?? float.PositiveInfinity))
            {
                continue;
            }

            var point = ray.Origin + ray.Direction * along;
            if (entry.Covers(point))
            {
                nearest = new GridHit(name, entry.Grid, point, along);
            }
        }

        return nearest;
    }

    private sealed record Entry(IGrid Grid, Func<Vector3, bool> Covers);
}
