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

/// <summary>One enemy on the road.</summary>
public sealed class Enemy
{
    public required int Id { get; init; }

    public required EnemyKind Kind { get; init; }

    public required int Health { get; set; }

    /// <summary>How far along the road it has walked, in metres.</summary>
    public float Distance { get; set; }

    /// <summary>How far to the side of the road's middle it walks, so a line of enemies doesn't look like a train.</summary>
    public required float Offset { get; init; }

    public Vector3 Position { get; set; }

    /// <summary>Where it was last tick, so drawing can blend between the two.</summary>
    public Vector3 PreviousPosition { get; set; }
}

/// <summary>Every enemy on the road: spawning them, walking them to the base, and hitting them.</summary>
public sealed class Enemies(Field field, Player player, Look look, GameRandom random)
{
    private readonly List<Enemy> _all = [];
    private readonly RandomStream _offsets = random.Stream("enemies");
    private int _nextId;

    public IReadOnlyList<Enemy> All => _all;

    /// <summary>How many enemies towers have killed since the last <see cref="Clear"/>.</summary>
    public int Killed { get; private set; }

    public static EnemyStats Stats(EnemyKind kind) => kind switch
    {
        EnemyKind.Walker => new(Health: 3, Speed: 2f, Reward: 10, Size: 0.4f, Color: new Vector3(0.88f, 0.38f, 0.32f)),
        EnemyKind.Runner => new(Health: 2, Speed: 4f, Reward: 6, Size: 0.3f, Color: new Vector3(0.92f, 0.78f, 0.42f)),
        EnemyKind.Brute => new(Health: 10, Speed: 1.2f, Reward: 25, Size: 0.65f, Color: new Vector3(0.58f, 0.46f, 0.7f)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "There's no such enemy."),
    };

    /// <summary>Puts a new enemy at the start of the road.</summary>
    public Enemy Spawn(EnemyKind kind)
    {
        var enemy = new Enemy
        {
            Id = _nextId++,
            Kind = kind,
            Health = Stats(kind).Health,
            Offset = _offsets.Between(-0.3f, 0.3f),
        };
        enemy.Position = PositionAt(enemy);
        enemy.PreviousPosition = enemy.Position;
        _all.Add(enemy);
        return enemy;
    }

    /// <summary>Walks every enemy along the road. One that reaches the base costs a life and is gone.</summary>
    public void Update(GameTime time)
    {
        // Backwards, so removing one doesn't skip the next.
        for (int i = _all.Count - 1; i >= 0; i--)
        {
            var enemy = _all[i];
            enemy.PreviousPosition = enemy.Position;
            enemy.Distance += Stats(enemy.Kind).Speed * time.DeltaSeconds;
            if (enemy.Distance >= field.Road.Length)
            {
                player.Lives--;
                _all.RemoveAt(i);
                continue;
            }

            enemy.Position = PositionAt(enemy);
        }
    }

    /// <summary>Takes health off an enemy. At zero it dies, and the player gets its reward.</summary>
    public void Hit(Enemy enemy, int damage)
    {
        if (!_all.Contains(enemy))
        {
            return;
        }

        enemy.Health -= damage;
        if (enemy.Health <= 0)
        {
            _all.Remove(enemy);
            player.Gold += Stats(enemy.Kind).Reward;
            Killed++;
        }
    }

    /// <summary>Draws each enemy as a capsule, blended between its last two ticks.</summary>
    public void Draw(View view)
    {
        foreach (var enemy in _all)
        {
            var stats = Stats(enemy.Kind);
            // The capsule is 2 metres tall, so lifting it by its size stands it on the road.
            var lift = new Vector3(0, stats.Size, 0);
            var scale = Matrix4x4.CreateScale(stats.Size);
            var previous = scale * Matrix4x4.CreateTranslation(enemy.PreviousPosition + lift);
            var current = scale * Matrix4x4.CreateTranslation(enemy.Position + lift);
            view.Model(100_000 + enemy.Id, look.Capsule, view.Smooth(previous, current), stats.Color);
        }
    }

    /// <summary>Takes every enemy off the road, for a new game.</summary>
    public void Clear()
    {
        _all.Clear();
        Killed = 0;
    }

    private Vector3 PositionAt(Enemy enemy)
    {
        var pose = field.Road.FrameAtDistance(enemy.Distance);
        return pose.Position + pose.Right * enemy.Offset;
    }
}
