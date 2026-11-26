namespace Super;

/// <summary>Draws a <see cref="TileMap{TCell}"/> in a <see cref="View"/>. See docs/grids.md.</summary>
public static class ViewTiles
{
    /// <summary>
    /// Draws every tile of a map, on every floor. Each block of 16 by 16 cells is kept from the last frame until one of
    /// its cells, the tile set or the grid's placement changes, so a big map that isn't changing costs little and
    /// allocates nothing. The key names the map; give each map drawn in a frame its own.
    /// </summary>
    public static void TileMap<TCell>(this View view, int key, TileMap<TCell> map)
        where TCell : struct, IGridCell<TCell>
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(map);
        for (int floor = 0; floor < map.Floors; floor++)
        {
            var cells = map.Cells(floor);
            int look = HashCode.Combine(map.Grid(floor).Placement, map.Tiles.Version);
            foreach (var chunk in cells.ChunkKeys)
            {
                long version = cells.ChunkVersion(chunk) * 31 + look;
                view.Cached(HashCode.Combine(key, floor, chunk), version, (map, floor, chunk, key), Chunks<TCell>.Draw);
            }
        }
    }

    // One cached delegate for each kind of cell, so drawing a map makes no new ones.
    private static class Chunks<TCell>
        where TCell : struct, IGridCell<TCell>
    {
        public static readonly Action<(TileMap<TCell> Map, int Floor, GridChunk Chunk, int Key), View> Draw = static (data, view) =>
        {
            var (map, floor, chunk, key) = data;
            foreach (var (cell, placed) in map.Cells(floor).InChunk(chunk))
            {
                if (map.Tiles.TryGet(placed.Tile, out var tile))
                {
                    view.Model(HashCode.Combine(key, floor, cell), tile.Model, map.Transform(cell, placed, tile, floor), tile.Color);
                }
            }
        };
    }
}
