using System.Numerics;

namespace Super;

/// <summary>
/// Modular pieces laid on a grid, such as a dungeon's walls and floors or an island's coast: each cell holds a tile
/// from a <see cref="TileSet{TCell}"/>, turned to fit. Painting a group picks each cell's tile from its neighbours, so
/// walls join up and coasts face the sea. A map can have several floors on a <see cref="LayeredGrid{TCell}"/>, loads
/// from and saves to a <see cref="TileLevel"/>, and is drawn with <see cref="ViewTiles.TileMap"/>. See docs/grids.md.
/// </summary>
public sealed class TileMap<TCell>
    where TCell : struct, IGridCell<TCell>
{
    private readonly IGrid<TCell>[] _grids;
    private readonly GridMap<TCell, PlacedTile>[] _floors;

    /// <summary>A map of one floor on a grid.</summary>
    public TileMap(TileSet<TCell> tiles, IGrid<TCell> grid)
        : this(tiles, [grid ?? throw new ArgumentNullException(nameof(grid))])
    {
    }

    /// <summary>A map with a floor on each layer of a layered grid.</summary>
    public TileMap(TileSet<TCell> tiles, LayeredGrid<TCell> floors)
        : this(tiles, [.. Enumerable.Range(0, (floors ?? throw new ArgumentNullException(nameof(floors))).Count).Select(floor => floors[floor])])
    {
    }

    private TileMap(TileSet<TCell> tiles, IGrid<TCell>[] grids)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        Tiles = tiles;
        _grids = grids;
        _floors = [.. grids.Select(_ => new GridMap<TCell, PlacedTile>())];
    }

    /// <summary>The tiles the map places.</summary>
    public TileSet<TCell> Tiles { get; }

    /// <summary>How many floors there are.</summary>
    public int Floors => _floors.Length;

    /// <summary>The grid a floor lies on, 0 lowest.</summary>
    public IGrid<TCell> Grid(int floor = 0) => _grids[Floor(floor)];

    /// <summary>The tile in each filled cell of a floor. Changing it directly places tiles as they are, like <see cref="Set"/>.</summary>
    public GridMap<TCell, PlacedTile> Cells(int floor = 0) => _floors[Floor(floor)];

    /// <summary>Puts a tile in a cell as it is, turned by <paramref name="turns"/>, replacing what was there. Its neighbours don't change.</summary>
    public void Set(TCell cell, string tile, int turns = 0, int floor = 0)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (!Tiles.TryGet(tile, out _))
        {
            throw new ArgumentException($"There is no tile called {tile} in the map's tile set.", nameof(tile));
        }

        int sides = TCell.Sides.Length;
        _floors[Floor(floor)].Set(cell, new PlacedTile(tile, ((turns % sides) + sides) % sides));
    }

    /// <summary>
    /// Fills a cell with a group, such as walls, choosing its tile and turn from its neighbours in the same group, and
    /// fits those neighbours again too.
    /// </summary>
    public void Paint(TCell cell, string group, int floor = 0) => Paint([cell], group, floor);

    /// <summary>Fills cells with a group and fits them and their neighbours, as if each were painted, but faster.</summary>
    public void Paint(IEnumerable<TCell> cells, string group, int floor = 0)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(group);
        if (!Tiles.HasGroup(group))
        {
            throw new ArgumentException($"No tile in the map's tile set is in the group {group}.", nameof(group));
        }

        var map = _floors[Floor(floor)];
        var painted = cells.ToList();
        foreach (var cell in painted)
        {
            map.Set(cell, Tiles.Fit(group, 0, cell));
        }

        FitAround(map, painted);
    }

    /// <summary>Empties a cell, and fits its painted neighbours again. Says whether it held a tile.</summary>
    public bool Erase(TCell cell, int floor = 0)
    {
        var map = _floors[Floor(floor)];
        if (!map.Remove(cell))
        {
            return false;
        }

        FitAround(map, [cell]);
        return true;
    }

    /// <summary>
    /// Where the tile in a cell is drawn: its offset, then its turn, then the cell's middle on the grid. Use it to put
    /// something else where a tile is, such as a collider. Throws for an empty cell.
    /// </summary>
    public Matrix4x4 Transform(TCell cell, int floor = 0)
    {
        var placed = _floors[Floor(floor)][cell];
        return Transform(cell, placed, Tiles[placed.Tile], floor);
    }

    /// <summary>
    /// Empties the map and fills it from a level: every named tile as it is, then every painted group fitted at once,
    /// so the order of the cells doesn't matter. Throws, saying which cell, for one that's malformed.
    /// </summary>
    public void Load(TileLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        foreach (var floor in _floors)
        {
            floor.Clear();
        }

        var paints = new List<(int Floor, string Group, TCell Cell)>();
        for (int i = 0; i < level.Cells.Length; i++)
        {
            var (cell, entry) = Read(level, i);
            if (entry.Tile is { } tile)
            {
                Set(cell, tile, entry.Turns, entry.Floor);
            }
            else
            {
                paints.Add((entry.Floor, entry.Paint!, cell));
            }
        }

        foreach (var batch in paints.GroupBy(paint => (paint.Floor, paint.Group)))
        {
            Paint(batch.Select(paint => paint.Cell), batch.Key.Group, batch.Key.Floor);
        }
    }

    /// <summary>The map as a level with this id, every cell naming its tile and turn, floor by floor and row by row.</summary>
    public TileLevel ToLevel(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var cells = _floors
            .SelectMany((map, floor) => map.Select(pair => (Floor: floor, At: pair.Key.Pair, Placed: pair.Value)))
            .OrderBy(cell => cell.Floor)
            .ThenBy(cell => cell.At.B)
            .ThenBy(cell => cell.At.A)
            .Select(cell => new TileLevelCell([cell.At.A, cell.At.B], cell.Placed.Tile, cell.Placed.Turns, Floor: cell.Floor));
        return new TileLevel(id, [.. cells]);
    }

    /// <summary>Where a tile is drawn in a cell: its offset, its turn about the grid's up, then the cell's middle.</summary>
    internal Matrix4x4 Transform(TCell cell, PlacedTile placed, Tile tile, int floor)
    {
        var grid = _grids[floor];
        var turn = grid.Placement.Matrix with { Translation = Vector3.Zero };
        float angle = MathF.Tau * placed.Turns / TCell.Sides.Length;
        return Matrix4x4.CreateTranslation(tile.Offset)
            * Matrix4x4.CreateRotationY(angle)
            * turn
            * Matrix4x4.CreateTranslation(grid.Center(cell));
    }

    private (TCell Cell, TileLevelCell Entry) Read(TileLevel level, int i)
    {
        var entry = level.Cells[i];
        string? problem = entry switch
        {
            null => "is null",
            { At: not [_, _] } => "needs \"at\" as two whole numbers, such as [3, -2]",
            { Tile: null, Paint: null } or { Tile: not null, Paint: not null } => "needs either a tile or a group to paint, but not both",
            { Floor: var f } when (uint)f >= (uint)_floors.Length => $"is on floor {f}, but the map has {_floors.Length}",
            { Paint: { } group } when !Tiles.HasGroup(group) => $"paints {group}, but no tile is in that group",
            { Tile: { } tile } when !Tiles.TryGet(tile, out _) => $"names the tile {tile}, but the tile set has none called that",
            _ => null,
        };
        if (problem is not null)
        {
            throw new ArgumentException($"Cell {i} of the level {level.Id} {problem}.", nameof(level));
        }

        return (TCell.FromPair(entry!.At[0], entry.At[1]), entry);
    }

    // Fits every painted cell among these and their neighbours to the neighbours they have now.
    private void FitAround(GridMap<TCell, PlacedTile> map, List<TCell> cells)
    {
        var sides = TCell.Sides;
        var touched = new HashSet<TCell>(cells);
        foreach (var cell in cells)
        {
            foreach (var side in sides)
            {
                touched.Add(cell + side);
            }
        }

        foreach (var cell in touched)
        {
            if (GroupAt(map, cell) is not { } group)
            {
                continue;
            }

            int joined = 0;
            for (int side = 0; side < sides.Length; side++)
            {
                if (GroupAt(map, cell + sides[side]) == group)
                {
                    joined |= 1 << side;
                }
            }

            var fit = Tiles.Fit(group, joined, cell);
            if (map[cell] != fit)
            {
                map.Set(cell, fit);
            }
        }
    }

    private string? GroupAt(GridMap<TCell, PlacedTile> map, TCell cell) =>
        map.TryGet(cell, out var placed) && Tiles.TryGet(placed.Tile, out var tile) ? tile.Group : null;

    private int Floor(int floor)
    {
        if ((uint)floor >= (uint)_floors.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(floor), $"The map has {_floors.Length} floors, 0 to {_floors.Length - 1}, so there is no floor {floor}.");
        }

        return floor;
    }
}
