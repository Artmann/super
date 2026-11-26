using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Super;

/// <summary>
/// One modular piece for a <see cref="TileMap{TCell}"/>, such as a straight wall, a corner or a stretch of coast. It can
/// be made in code or loaded from content with <c>content.LoadAll&lt;Tile&gt;("tiles")</c>. See docs/grids.md.
/// </summary>
/// <param name="Id">The tile's name, which a map's cells and level files use.</param>
/// <param name="Model">The model drawn in the cell, with its middle on the cell's middle and its base on the grid.</param>
/// <param name="Group">
/// The group the tile belongs to, such as walls or land. Painting a group picks the tile in it whose
/// <paramref name="Connects"/> best fits the neighbours in the same group.
/// </param>
/// <param name="Connects">
/// Which sides of the model, before it's turned, join a neighbour of the same group: one 1 or 0 for each of the
/// cell's sides, in the order of <see cref="Cell.Sides"/> or <see cref="Hex.Sides"/>, such as <c>"1010"</c> for a
/// straight wall along X. Left out, the tile joins every side.
/// </param>
/// <param name="Offset">How far to move the model in its own space before it's turned, in metres.</param>
/// <param name="Color">A linear colour to tint the model with, or none.</param>
public sealed record Tile(
    string Id,
    ModelRef Model,
    string? Group = null,
    string? Connects = null,
    Vector3 Offset = default,
    Vector3? Color = null);

/// <summary>What a cell of a <see cref="TileMap{TCell}"/> holds: which tile, turned by how many quarter or sixth turns.</summary>
/// <param name="Tile">The <see cref="Super.Tile.Id"/> of the tile.</param>
/// <param name="Turns">How far it's turned, as <see cref="Cell.Rotate"/> and <see cref="Hex.Rotate"/> turn cells.</param>
public readonly record struct PlacedTile(string Tile, int Turns = 0);

/// <summary>
/// A tile map saved as a level, for a level file loaded with <c>content.LoadAll&lt;TileLevel&gt;("levels")</c> or a
/// save game. Fill a map from it with <see cref="TileMap{TCell}.Load"/> and make one with
/// <see cref="TileMap{TCell}.ToLevel"/>. See docs/grids.md.
/// </summary>
/// <param name="Id">The level's name.</param>
/// <param name="Cells">The filled cells, in any order.</param>
public sealed record TileLevel(string Id, TileLevelCell[] Cells)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    /// <summary>The level as JSON, in the shape level files have, leaving out what's the same as the defaults.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>A level read from JSON that <see cref="ToJson"/> wrote.</summary>
    public static TileLevel FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<TileLevel>(json, Options)
            ?? throw new JsonException("A tile level's JSON is null, but it should be an object with an id and cells.");
    }
}

/// <summary>
/// One cell of a <see cref="TileLevel"/>. It names a <paramref name="Tile"/> to place as it is, or a group to
/// <paramref name="Paint"/>, so the tile is picked to fit its neighbours as the level loads.
/// </summary>
/// <param name="At">The cell's two coordinates, X and Z for squares or Q and R for hexes, such as <c>[3, -2]</c>.</param>
/// <param name="Tile">The tile to place, or none when the cell paints a group.</param>
/// <param name="Turns">How far the tile is turned.</param>
/// <param name="Paint">The group to paint, or none when the cell names a tile.</param>
/// <param name="Floor">Which floor of a layered map the cell is on, 0 lowest.</param>
public sealed record TileLevelCell(int[] At, string? Tile = null, int Turns = 0, string? Paint = null, int Floor = 0);
