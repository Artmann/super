namespace OutpostExample;

/// <summary>One tower, standing on a tile.</summary>
public sealed class Tower
{
    public required int X { get; init; }

    public required int Z { get; init; }
}

/// <summary>The towers the player has built.</summary>
public sealed class Towers(Field field, Player player)
{
    public const int Price = 50;

    private readonly List<Tower> _all = [];

    public IReadOnlyList<Tower> All => _all;

    /// <summary>Builds a tower on a tile, if it's grass, it's free, and the player can pay. Says whether it did.</summary>
    public bool TryBuild(int x, int z)
    {
        bool onField = x >= 0 && x < Field.Width && z >= 0 && z < Field.Depth;
        if (!onField || field.IsRoad(x, z) || _all.Any(tower => tower.X == x && tower.Z == z) || player.Gold < Price)
        {
            return false;
        }

        player.Gold -= Price;
        _all.Add(new Tower { X = x, Z = z });
        return true;
    }

    /// <summary>Takes every tower down, for a new game.</summary>
    public void Clear() => _all.Clear();
}
