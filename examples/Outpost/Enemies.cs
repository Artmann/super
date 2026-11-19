using System.Numerics;
using Super;

namespace OutpostExample;

public enum EnemyKind
{
    Walker,
    Runner,
    Brute,
}

/// <summary>How tough, fast and valuable a kind of enemy is, and how it looks.</summary>
public readonly record struct EnemyStats(int Health, float Speed, int Reward, float Size, Vector3 Color);

/// <summary>Every enemy on the road: spawning them, walking them to the base, and hitting them.</summary>
public sealed class Enemies(World world, Scenes scenes, Field field, Player player, Look look, GameRandom random) : ISystem
{
    private readonly RandomStream _offsets = random.Stream("enemies");

    /// <summary>How many enemies are on the road.</summary>
    public int Count
    {
        get
        {
            int count = 0;
            foreach (var _ in world.Query<Enemy>())
            {
                count++;
            }

            return count;
        }
    }

    public static EnemyStats Stats(EnemyKind kind) => kind switch
    {
        EnemyKind.Walker => new(Health: 3, Speed: 2f, Reward: 10, Size: 0.4f, Color: new Vector3(0.88f, 0.38f, 0.32f)),
        EnemyKind.Runner => new(Health: 2, Speed: 4f, Reward: 6, Size: 0.3f, Color: new Vector3(0.92f, 0.78f, 0.42f)),
        EnemyKind.Brute => new(Health: 10, Speed: 1.2f, Reward: 25, Size: 0.65f, Color: new Vector3(0.58f, 0.46f, 0.7f)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "There's no such enemy."),
    };

    /// <summary>Puts a new enemy at the start of the road, as part of this game, so a new game takes it away.</summary>
    public Entity Spawn(EnemyKind kind)
    {
        var stats = Stats(kind);
        var enemy = new Enemy(kind, stats.Health, Distance: 0f, Offset: _offsets.Between(-0.3f, 0.3f));
        // The capsule is 2 metres tall and its size scales it, so it's drawn as a capsule standing on the road.
        return scenes.Current!.Spawn(
            Transform.At(PositionAt(enemy), stats.Size),
            new Model(look.Capsule, stats.Color),
            enemy);
    }

    /// <summary>Walks every enemy along the road. One that reaches the base costs a life and is gone.</summary>
    public void Update(GameTime time)
    {
        foreach (var row in world.Query<Enemy, Transform>())
        {
            ref var enemy = ref row.First;
            enemy.Distance += Stats(enemy.Kind).Speed * time.DeltaSeconds;
            if (enemy.Distance >= field.Road.Length)
            {
                player.Lives--;
                world.Destroy(row.Entity);
                continue;
            }

            row.Second.Position = PositionAt(enemy);
        }
    }

    /// <summary>Takes health off an enemy. At zero it dies, and the player gets its reward.</summary>
    public void Hit(Entity target, int damage)
    {
        // It may have reached the base, or died to another shot this tick and be about to go.
        if (!world.IsAlive(target) || world.Get<Enemy>(target).Health <= 0)
        {
            return;
        }

        ref var enemy = ref world.Get<Enemy>(target);
        enemy.Health -= damage;
        if (enemy.Health <= 0)
        {
            world.Destroy(target);
            player.Gold += Stats(enemy.Kind).Reward;
            player.Killed++;
        }
    }

    /// <summary>The middle of the enemy's body: its spot on the road, lifted by its size.</summary>
    private Vector3 PositionAt(in Enemy enemy)
    {
        var pose = field.Road.FrameAtDistance(enemy.Distance);
        return pose.Position + pose.Right * enemy.Offset + new Vector3(0, Stats(enemy.Kind).Size, 0);
    }
}
