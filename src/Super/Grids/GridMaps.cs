using System.Text.Json;
using System.Text.Json.Serialization;

namespace Super;

/// <summary>
/// Saves a <see cref="GridMap{TCell, T}"/> as JSON and loads it back, for save games and level files. The file lists
/// each filled cell as <c>{ "at": [x, z], "value": ... }</c>, row by row, so it reads well and changes little in a diff.
/// Values are written by System.Text.Json with the options given, camelCase by default. See docs/grids.md.
/// </summary>
public static class GridMaps
{
    private static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The map as JSON.</summary>
    public static string ToJson<TCell, T>(GridMap<TCell, T> map, JsonSerializerOptions? options = null)
        where TCell : struct, IGridCell<TCell>
    {
        ArgumentNullException.ThrowIfNull(map);
        var cells = map
            .Select(pair => (At: pair.Key.Pair, pair.Value))
            .OrderBy(cell => cell.At.B)
            .ThenBy(cell => cell.At.A)
            .Select(cell => (SavedCell<T>?)new SavedCell<T>([cell.At.A, cell.At.B], cell.Value))
            .ToList();
        return JsonSerializer.Serialize(new SavedMap<T>(cells), options ?? Default);
    }

    /// <summary>A map read from JSON that <see cref="ToJson"/> wrote. Throws, saying where, for a cell that's malformed or there twice.</summary>
    public static GridMap<TCell, T> FromJson<TCell, T>(string json, JsonSerializerOptions? options = null)
        where TCell : struct, IGridCell<TCell>
    {
        ArgumentNullException.ThrowIfNull(json);
        var saved = JsonSerializer.Deserialize<SavedMap<T>>(json, options ?? Default)
            ?? throw new JsonException("A grid map's JSON is null, but it should be an object with a cells list.");
        var map = new GridMap<TCell, T>();
        var cells = saved.Cells ?? [];
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i]?.At is not [int a, int b])
            {
                throw new JsonException($"Cell {i} of the grid map needs \"at\" as two whole numbers, such as [3, -2].");
            }

            var cell = TCell.FromPair(a, b);
            if (map.Contains(cell))
            {
                throw new JsonException($"Cell {i} of the grid map is at {cell}, but an earlier cell is there too.");
            }

            map.Set(cell, cells[i]!.Value);
        }

        return map;
    }

    private sealed record SavedMap<T>(List<SavedCell<T>?>? Cells);

    private sealed record SavedCell<T>(int[]? At, T Value);
}
