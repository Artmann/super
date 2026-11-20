using System.Numerics;
using Super;
using Super.Physics;

namespace SkylarkExample;

/// <summary>Walks and jumps the bird, puts it back home when it falls, and keeps the camera on it.</summary>
public sealed class Bird(World world, Physics physics, Actions<Control> actions, Chase chase, Journey journey, Levels levels) : ISystem
{
    public const float Speed = 6f;

    public void Update(GameTime time)
    {
        float floor = levels.Current.Start.Y - Levels.FallDepth;

        foreach (var row in world.Query<Hero, Transform>())
        {
            // The bird's Jumper does the falling, the jumping and the riding on lifts.
            var walk = chase.Walk(actions.Vector(Control.Move)) * Speed;
            physics.Characters.Walk(row.Entity, walk, actions.Down(Control.Jump), time.DeltaSeconds);

            if (walk.LengthSquared() > 0.01f)
            {
                row.Second.Rotation = Transform.Facing(Vector3.Zero, walk).Rotation;
            }

            if (row.Second.Position.Y < floor)
            {
                world.Teleport(row.Entity, row.Second with { Position = row.First.Home });
                world.Get<Jumper>(row.Entity).Rise = 0f;
                journey.Falls++;
                chase.Jump();
            }

            chase.Update(world.Get<Transform>(row.Entity).Position, time);
        }
    }
}
