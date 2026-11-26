using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Super;

/// <summary>
/// The tiles a <see cref="TileMap{TCell}"/> places, found by id, and which tile and turn fits each group's
/// neighbours. Made from a list of tiles, or from a content catalog, in which case it follows the catalog through a
/// live reload. See docs/grids.md.
/// </summary>
public sealed class TileSet<TCell>
    where TCell : struct, IGridCell<TCell>
{
    private Dictionary<string, Tile> _byId = [];
    private Dictionary<string, PlacedTile[][]> _fits = [];

    /// <summary>A set of these tiles. Throws for a tile there twice or one whose <see cref="Tile.Connects"/> is malformed.</summary>
    public TileSet(IEnumerable<Tile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        Build(tiles);
    }

    /// <summary>A set of the tiles in a catalog, made again whenever the catalog reloads.</summary>
    public TileSet(Catalog<Tile> tiles)
        : this((IEnumerable<Tile>)tiles)
    {
        tiles.OnChange(() =>
        {
            Build(tiles);
            Version++;
        });
    }

    /// <summary>How many tiles there are.</summary>
    public int Count => _byId.Count;

    /// <summary>Goes up each time the tiles change, so a drawn map knows to draw again.</summary>
    public long Version { get; private set; }

    /// <summary>The tile with this id. Throws for one that isn't in the set.</summary>
    public Tile this[string id] => TryGet(id, out var tile)
        ? tile
        : throw new KeyNotFoundException($"There is no tile called {id} in the tile set.");

    /// <summary>The tile with this id, if there is one.</summary>
    public bool TryGet(string id, [MaybeNullWhen(false)] out Tile tile) => _byId.TryGetValue(id, out tile);

    /// <summary>Whether any tile is in this group.</summary>
    public bool HasGroup(string group) => _fits.ContainsKey(group);

    /// <summary>
    /// The tile of a group, and its turn, for a cell whose neighbours in the same group are on the sides
    /// <paramref name="joined"/> holds, one bit for each side in <c>Sides</c> order. The fit with the fewest sides
    /// wrong wins, then the fewest turns. When several tiles fit as well, the cell picks one of them, the same one
    /// each time.
    /// </summary>
    internal PlacedTile Fit(string group, int joined, TCell cell)
    {
        var choices = _fits[group][joined];
        return choices[Spread(cell) % (uint)choices.Length];
    }

    private void Build(IEnumerable<Tile> tiles)
    {
        var byId = new Dictionary<string, Tile>();
        var groups = new Dictionary<string, List<(string Id, int Joins)>>();
        foreach (var tile in tiles)
        {
            if (!byId.TryAdd(tile.Id, tile))
            {
                throw new ArgumentException($"The tile {tile.Id} is in the tile set twice.", nameof(tiles));
            }

            int joins = Joins(tile);
            if (tile.Group is { } group)
            {
                (groups.TryGetValue(group, out var members) ? members : groups[group] = []).Add((tile.Id, joins));
            }
        }

        _fits = groups.ToDictionary(group => group.Key, group => Fits(group.Value));
        _byId = byId;
    }

    private static int Joins(Tile tile)
    {
        int sides = TCell.Sides.Length;
        if (tile.Connects is null)
        {
            return (1 << sides) - 1;
        }

        if (tile.Group is null)
        {
            throw new ArgumentException(
                $"The tile {tile.Id} says which sides it connects, but it has no group, so painting never picks it. Give it a group.");
        }

        if (tile.Connects.Length != sides || tile.Connects.Any(side => side is not ('0' or '1')))
        {
            throw new ArgumentException(
                $"The tile {tile.Id} connects \"{tile.Connects}\", but a cell has {sides} sides, so it needs {sides} of 1 or 0, one for each side in Sides order, such as \"{new string('1', 2)}{new string('0', sides - 2)}\".");
        }

        int joins = 0;
        for (int side = 0; side < sides; side++)
        {
            if (tile.Connects[side] == '1')
            {
                joins |= 1 << side;
            }
        }

        return joins;
    }

    // For every pattern of joined sides, the tiles that fit it best.
    private static PlacedTile[][] Fits(List<(string Id, int Joins)> members)
    {
        var fits = new PlacedTile[1 << TCell.Sides.Length][];
        for (int joined = 0; joined < fits.Length; joined++)
        {
            int fewestWrong = int.MaxValue;
            var best = new List<PlacedTile>();
            foreach (var (id, joins) in members)
            {
                var (turns, wrong) = BestTurn(joins, joined);
                if (wrong < fewestWrong)
                {
                    fewestWrong = wrong;
                    best.Clear();
                }

                if (wrong == fewestWrong)
                {
                    best.Add(new PlacedTile(id, turns));
                }
            }

            fits[joined] = [.. best];
        }

        return fits;
    }

    private static (int Turns, int Wrong) BestTurn(int joins, int joined)
    {
        var best = (Turns: 0, Wrong: int.MaxValue);
        for (int turns = 0; turns < TCell.Sides.Length; turns++)
        {
            int wrong = BitOperations.PopCount((uint)(TileSides<TCell>.Turned(joins, turns) ^ joined));
            if (wrong < best.Wrong)
            {
                best = (turns, wrong);
            }
        }

        return best;
    }

    // Mixes a cell's coordinates into a number that looks random but is the same every run.
    private static uint Spread(TCell cell)
    {
        var (a, b) = cell.Pair;
        uint mixed = unchecked(((uint)a * 0x9E3779B1u) ^ ((uint)b * 0x85EBCA77u));
        mixed ^= mixed >> 15;
        mixed = unchecked(mixed * 0x2C1B3C6Du);
        return mixed ^ (mixed >> 12);
    }
}

/// <summary>Which side each side of a cell becomes when it's turned, for turning a tile's joined sides.</summary>
internal static class TileSides<TCell>
    where TCell : struct, IGridCell<TCell>
{
    // Turning by turns moves side i to side Moves[turns][i].
    private static readonly int[][] Moves = [.. Enumerable.Range(0, TCell.Sides.Length).Select(MovesFor)];

    /// <summary>The sides a tile joins once it's turned, one bit for each side in <c>Sides</c> order.</summary>
    public static int Turned(int joins, int turns)
    {
        var moves = Moves[((turns % Moves.Length) + Moves.Length) % Moves.Length];
        int turned = 0;
        for (int side = 0; side < moves.Length; side++)
        {
            if ((joins & (1 << side)) != 0)
            {
                turned |= 1 << moves[side];
            }
        }

        return turned;
    }

    private static int[] MovesFor(int turns)
    {
        var sides = TCell.Sides;
        var moves = new int[sides.Length];
        for (int side = 0; side < sides.Length; side++)
        {
            moves[side] = sides.IndexOf(TCell.Rotate(sides[side], default, turns));
        }

        return moves;
    }
}
