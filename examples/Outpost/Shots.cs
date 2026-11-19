using System.Numerics;
using Super;

namespace OutpostExample;

/// <summary>One shot in the air, chasing its target.</summary>
public sealed class Shot
{
    public required int Id { get; init; }

    public required Enemy Target { get; init; }

    public Vector3 Position { get; set; }

    /// <summary>Where it was last tick, so drawing can blend between the two.</summary>
    public Vector3 PreviousPosition { get; set; }
}

/// <summary>Every shot in the air. A shot flies at its target and hits it when it gets there.</summary>
public sealed class Shots(Enemies enemies)
{
    public const float Speed = 14f;
    public const int Damage = 1;

    private readonly List<Shot> _all = [];
    private int _nextId;

    public IReadOnlyList<Shot> All => _all;

    public void Fire(Vector3 from, Enemy target)
    {
        _all.Add(new Shot { Id = _nextId++, Target = target, Position = from, PreviousPosition = from });
    }

    public void Update(GameTime time)
    {
        for (int i = _all.Count - 1; i >= 0; i--)
        {
            var shot = _all[i];

            // Its target died or reached the base since it was fired, so there's nothing left to hit.
            if (!enemies.All.Contains(shot.Target))
            {
                _all.RemoveAt(i);
                continue;
            }

            // Aim for the middle of the enemy's body, not its feet.
            var aim = shot.Target.Position + new Vector3(0, Enemies.Stats(shot.Target.Kind).Size, 0);
            var toTarget = aim - shot.Position;
            float distance = toTarget.Length();
            float step = Speed * time.DeltaSeconds;

            shot.PreviousPosition = shot.Position;
            if (distance <= step)
            {
                enemies.Hit(shot.Target, Damage);
                _all.RemoveAt(i);
                continue;
            }

            shot.Position += toTarget / distance * step;
        }
    }

    /// <summary>Takes every shot out of the air, for a new game.</summary>
    public void Clear() => _all.Clear();
}
