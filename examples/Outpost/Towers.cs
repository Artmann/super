using System.Numerics;
using Super;

namespace OutpostExample;

/// <summary>One tower, standing on a tile.</summary>
public sealed class Tower
{
    public required int X { get; init; }

    public required int Z { get; init; }

    /// <summary>Seconds until it can shoot again.</summary>
    public float Reload { get; set; }
}

/// <summary>The towers the player has built, and their shooting.</summary>
public sealed class Towers(Field field, Player player, Look look, Enemies enemies, Shots shots)
{
    public const int Price = 50;
    public const float Range = 5f;
    public const float ReloadSeconds = 0.75f;

    /// <summary>How high above its tile a tower's shots start, at the top of its wall.</summary>
    private static readonly Vector3 Muzzle = new(0, 1.2f, 0);

    private static readonly Vector3 Wall = new(0.92f, 0.88f, 0.8f);
    private static readonly Vector3 Roof = new(0.38f, 0.52f, 0.75f);

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

    /// <summary>Each tower that has reloaded shoots at the enemy in range that's nearest the base.</summary>
    public void Update(GameTime time)
    {
        foreach (var tower in _all)
        {
            tower.Reload = MathF.Max(0f, tower.Reload - time.DeltaSeconds);
            if (tower.Reload > 0f)
            {
                continue;
            }

            var spot = field.Center(tower.X, tower.Z);
            Enemy? target = null;
            foreach (var enemy in enemies.All)
            {
                bool inRange = Vector3.Distance(spot, enemy.Position) <= Range;
                if (inRange && (target is null || enemy.Distance > target.Distance))
                {
                    target = enemy;
                }
            }

            if (target is not null)
            {
                shots.Fire(spot + Muzzle, target);
                tower.Reload = ReloadSeconds;
            }
        }
    }

    /// <summary>Draws each tower as a cylinder with a cone for a roof.</summary>
    public void Draw(View view)
    {
        for (int i = 0; i < _all.Count; i++)
        {
            var spot = field.Center(_all[i].X, _all[i].Z);
            var wall = Matrix4x4.CreateScale(0.8f, 1.2f, 0.8f) * Matrix4x4.CreateTranslation(spot + new Vector3(0, 0.6f, 0));
            var roof = Matrix4x4.CreateScale(1.1f, 0.8f, 1.1f) * Matrix4x4.CreateTranslation(spot + new Vector3(0, 1.6f, 0));
            view.Model(20_000 + i, look.Cylinder, wall, Wall);
            view.Model(30_000 + i, look.Cone, roof, Roof);
        }
    }

    /// <summary>Takes every tower down, for a new game.</summary>
    public void Clear() => _all.Clear();
}
