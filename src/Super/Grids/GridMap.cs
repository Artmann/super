using System.Collections;

namespace Super;

/// <summary>One square block of <see cref="GridMap{TCell, T}.ChunkSize"/> by <see cref="GridMap{TCell, T}.ChunkSize"/> cells in a <see cref="GridMap{TCell, T}"/>.</summary>
/// <param name="A">Which block along the cells' first coordinate, X or Q.</param>
/// <param name="B">Which block along the cells' second coordinate, Z or R.</param>
public readonly record struct GridChunk(int A, int B);

/// <summary>
/// Something kept in each cell of a grid, such as a building's entity, a tile or a cost, for as many cells as a game
/// fills, in any direction and with no edge. Cells are kept in blocks of 16 by 16, so a map can be walked a block at a
/// time and a block that hasn't changed since last time can be skipped. It saves to JSON and loads back with
/// <see cref="GridMaps"/>. See docs/grids.md.
/// </summary>
public sealed class GridMap<TCell, T> : IEnumerable<KeyValuePair<TCell, T>>
    where TCell : struct, IGridCell<TCell>
{
    /// <summary>How many cells wide and deep each block is.</summary>
    public const int ChunkSize = 1 << ChunkShift;

    private const int ChunkShift = 4;

    private readonly Dictionary<GridChunk, Block> _blocks = [];

    /// <summary>How many cells hold something.</summary>
    public int Count { get; private set; }

    /// <summary>Goes up each time a cell changes, so code that draws the map knows to look again.</summary>
    public long Version { get; private set; }

    /// <summary>The blocks that hold at least one cell.</summary>
    public IEnumerable<GridChunk> Chunks => _blocks.Keys;

    /// <summary>The blocks that hold at least one cell, walked without allocating.</summary>
    internal Dictionary<GridChunk, Block>.KeyCollection ChunkKeys => _blocks.Keys;

    /// <summary>What a cell holds. Reading a cell that holds nothing throws; use <see cref="TryGet"/> for that.</summary>
    public T this[TCell cell]
    {
        get => TryGet(cell, out var value) ? value : throw new KeyNotFoundException($"{cell} holds nothing.");
        set => Set(cell, value);
    }

    /// <summary>Puts something in a cell, replacing what was there.</summary>
    public void Set(TCell cell, T value)
    {
        var (chunk, slot) = Locate(cell);
        if (!_blocks.TryGetValue(chunk, out var block))
        {
            block = new Block();
            _blocks.Add(chunk, block);
        }

        if (!block.Filled[slot])
        {
            block.Filled[slot] = true;
            block.Count++;
            Count++;
        }

        block.Values[slot] = value;
        block.Version = ++Version;
    }

    /// <summary>Empties a cell. Says whether it held anything.</summary>
    public bool Remove(TCell cell)
    {
        var (chunk, slot) = Locate(cell);
        if (!_blocks.TryGetValue(chunk, out var block) || !block.Filled[slot])
        {
            return false;
        }

        block.Filled[slot] = false;
        block.Values[slot] = default!;
        Count--;
        Version++;
        if (--block.Count == 0)
        {
            _blocks.Remove(chunk);
        }
        else
        {
            block.Version = Version;
        }

        return true;
    }

    /// <summary>What a cell holds, if anything.</summary>
    public bool TryGet(TCell cell, out T value)
    {
        var (chunk, slot) = Locate(cell);
        if (_blocks.TryGetValue(chunk, out var block) && block.Filled[slot])
        {
            value = block.Values[slot];
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Whether a cell holds anything.</summary>
    public bool Contains(TCell cell) => TryGet(cell, out _);

    /// <summary>Empties every cell.</summary>
    public void Clear()
    {
        _blocks.Clear();
        Count = 0;
        Version++;
    }

    /// <summary>
    /// The <see cref="Version"/> a block last changed at, or −1 for a block that holds nothing, so a cache of a block's
    /// drawing knows when to rebuild.
    /// </summary>
    public long ChunkVersion(GridChunk chunk) => _blocks.TryGetValue(chunk, out var block) ? block.Version : -1;

    /// <summary>The block a cell is kept in.</summary>
    public static GridChunk ChunkOf(TCell cell) => Locate(cell).Chunk;

    /// <summary>The filled cells in one block, and what each holds.</summary>
    public IEnumerable<KeyValuePair<TCell, T>> InChunk(GridChunk chunk)
    {
        if (!_blocks.TryGetValue(chunk, out var block))
        {
            yield break;
        }

        for (int slot = 0; slot < ChunkSize * ChunkSize; slot++)
        {
            if (block.Filled[slot])
            {
                var cell = TCell.FromPair(chunk.A * ChunkSize + slot % ChunkSize, chunk.B * ChunkSize + slot / ChunkSize);
                yield return new KeyValuePair<TCell, T>(cell, block.Values[slot]);
            }
        }
    }

    /// <summary>Every filled cell and what it holds, a block at a time.</summary>
    public IEnumerator<KeyValuePair<TCell, T>> GetEnumerator() =>
        _blocks.Keys.ToArray().SelectMany(InChunk).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Shifting right and masking round towards minus infinity, so cell −1 is in block −1, not block 0.
    private static (GridChunk Chunk, int Slot) Locate(TCell cell)
    {
        var (a, b) = cell.Pair;
        return (new GridChunk(a >> ChunkShift, b >> ChunkShift), (a & (ChunkSize - 1)) + (b & (ChunkSize - 1)) * ChunkSize);
    }

    internal sealed class Block
    {
        public readonly T[] Values = new T[ChunkSize * ChunkSize];
        public readonly bool[] Filled = new bool[ChunkSize * ChunkSize];
        public int Count;
        public long Version;
    }
}
