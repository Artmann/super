using System.Numerics;
using Super;

namespace OutpostExample;

/// <summary>Every shot in the air. A shot flies at its target and hits it when it gets there.</summary>
public sealed class Shots(World world, Scenes scenes, Enemies enemies, Look look) : ISystem
{
    public const float Speed = 14f;
    public const int Damage = 1;

    private static readonly Vector3 Glow = new(1f, 0.88f, 0.5f);

    /// <summary>Puts a small yellow ball in the air at <paramref name="from"/>, flying at the target.</summary>
    public void Fire(Vector3 from, Entity target) =>
        scenes.Current!.Spawn(Transform.At(from, 0.4f), new Model(look.Sphere, Glow), new Shot(target));

    public void Update(GameTime time)
    {
        foreach (var row in world.Query<Shot, Transform>())
        {
            var target = row.First.Target;

            // Its target died or reached the base since it was fired, so there's nothing left to hit.
            if (!world.IsAlive(target))
            {
                world.Destroy(row.Entity);
                continue;
            }

            // An enemy's transform is the middle of its body, which is where a shot aims.
            var toTarget = world.Get<Transform>(target).Position - row.Second.Position;
            float distance = toTarget.Length();
            float step = Speed * time.DeltaSeconds;
            if (distance <= step)
            {
                enemies.Hit(target, Damage);
                world.Destroy(row.Entity);
                continue;
            }

            row.Second.Position += toTarget / distance * step;
        }
    }
}
