using System.Numerics;

namespace Reef;

/// <summary>
/// A grid of cells over the reef, each listing the fish in it, so a fish finds its neighbours by looking only in its
/// own cell and the cells round it. Its arrays are allocated once.
/// </summary>
internal sealed class NeighbourGrid
{
    /// <summary>How wide a cell is, in metres.</summary>
    public const float Cell = 5f;

    private readonly float _radius;
    private readonly int _columns;
    private readonly int _layers;
    private readonly int[] _nextInCell;
    private readonly int[] _firstInCell;

    /// <summary>A grid over a reef <paramref name="radius"/> round its middle and <paramref name="height"/> high, for that many fish.</summary>
    public NeighbourGrid(float radius, float height, int fish)
    {
        _radius = radius;
        _nextInCell = new int[fish];
        _columns = (int)(radius * 2f / Cell) + 3;
        _layers = (int)(height / Cell) + 3;
        _firstInCell = new int[_columns * _columns * _layers];
    }

    /// <summary>Empties every cell.</summary>
    public void Clear() => Array.Fill(_firstInCell, -1);

    /// <summary>Puts fish <paramref name="fish"/> in the cell at <paramref name="position"/>.</summary>
    public void Add(int fish, Vector3 position)
    {
        int cell = CellOf(position);
        _nextInCell[fish] = _firstInCell[cell];
        _firstInCell[cell] = fish;
    }

    /// <summary>The first fish in <paramref name="cell"/>, or -1 when it's empty.</summary>
    public int First(int cell) => _firstInCell[cell];

    /// <summary>The fish after <paramref name="fish"/> in its cell, or -1 when it's the last.</summary>
    public int Next(int fish) => _nextInCell[fish];

    /// <summary>
    /// Fills <paramref name="cells"/> with the cell at <paramref name="position"/> and those round it, layer by layer,
    /// row by row, and gives how many there are: up to 27.
    /// </summary>
    public int CellsAround(Vector3 position, Span<int> cells)
    {
        int count = 0;
        int cellX = CellIndex(position.X + _radius);
        int cellY = CellIndex(position.Y);
        int cellZ = CellIndex(position.Z + _radius);
        for (int y = Math.Max(cellY - 1, 0); y <= Math.Min(cellY + 1, _layers - 1); y++)
        {
            for (int z = Math.Max(cellZ - 1, 0); z <= Math.Min(cellZ + 1, _columns - 1); z++)
            {
                for (int x = Math.Max(cellX - 1, 0); x <= Math.Min(cellX + 1, _columns - 1); x++)
                {
                    cells[count++] = (y * _columns + z) * _columns + x;
                }
            }
        }

        return count;
    }

    private int CellOf(Vector3 position) =>
        (CellIndex(position.Y) * _columns + CellIndex(position.Z + _radius)) * _columns + CellIndex(position.X + _radius);

    // The first cell is a margin, so neighbours just outside the area are safe to reach.
    private static int CellIndex(float metres) => Math.Max((int)(metres / Cell) + 1, 0);
}
