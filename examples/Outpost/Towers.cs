using System.Numerics;
using Super;

namespace OutpostExample;

/// <summary>The towers the player has built, and their shooting.</summary>
public sealed class Towers(World world, Scenes scenes, Field field, Player player, Look look, Shots shots) : ISystem
{
    public const int Price = 50;
    public const float Range = 5f;
    public const float ReloadSeconds = 0.75f;

    /// <summary>How high above its tile a tower's shots start, at the top of its wall.</summary>
    private static readonly Vector3 Muzzle = new(0, 1.2f, 0);

    private static readonly Vector3 Wall = new(0.92f, 0.88f, 0.8f);
    private static readonly Vector3 Roof = new(0.38f, 0.52f, 0.75f);

    /// <summary>Builds a tower on a tile, if it's grass, it's free, and the player can pay. Says whether it did.</summary>
    public bool TryBuild(int x, int z)
    {
        bool onField = x >= 0 && x < Field.Width && z >= 0 && z < Field.Depth;
        if (!onField || field.IsRoad(x, z) || IsBuilt(x, z) || player.Gold < Price)
        {
            return false;
        }

        player.Gold -= Price;

        // The tower stands on its tile, and its wall and roof are parts that follow it.
        var scene = scenes.Current!;
        var tower = scene.Spawn(Transform.At(field.Center(x, z)), new Tower(x, z, Reload: 0f));
        scene.Spawn(
            new Transform(new Vector3(0, 0.6f, 0), Quaternion.Identity, new Vector3(0.8f, 1.2f, 0.8f)),
            new Model(look.Cylinder, Wall),
            new Parent(tower));
        scene.Spawn(
            new Transform(new Vector3(0, 1.6f, 0), Quaternion.Identity, new Vector3(1.1f, 0.8f, 1.1f)),
            new Model(look.Cone, Roof),
            new Parent(tower));
        return true;
    }

    /// <summary>Each tower that has reloaded shoots at the enemy in range that's nearest the base.</summary>
    public void Update(GameTime time)
    {
        foreach (var row in world.Query<Tower, Transform>())
        {
            ref var tower = ref row.First;
            tower.Reload = MathF.Max(0f, tower.Reload - time.DeltaSeconds);
            if (tower.Reload > 0f)
            {
                continue;
            }

            var spot = row.Second.Position;
            Entity? target = null;
            float farthest = -1f;
            foreach (var enemy in world.Query<Enemy, Transform>())
            {
                bool inRange = Vector3.Distance(spot, enemy.Second.Position) <= Range;
                if (inRange && enemy.First.Distance > farthest)
                {
                    target = enemy.Entity;
                    farthest = enemy.First.Distance;
                }
            }

            if (target is { } aim)
            {
                shots.Fire(spot + Muzzle, aim);
                tower.Reload = ReloadSeconds;
            }
        }
    }

    private bool IsBuilt(int x, int z)
    {
        foreach (var row in world.Query<Tower>())
        {
            if (row.First.X == x && row.First.Z == z)
            {
                return true;
            }
        }

        return false;
    }
}
