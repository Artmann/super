using System.Numerics;
using Super;
using Super.Physics;

namespace SkylarkExample;

/// <summary>Walks and jumps the bird, puts it back home when it falls, and keeps the camera on it.</summary>
public sealed class Bird(World world, Physics physics, Actions<Control> actions, Chase chase, Journey journey, Levels levels) : ISystem
{
    public const float Speed = 6f;
    public const float JumpSpeed = 9.5f;
    public const float Gravity = 24f;

    /// <summary>A jump still works this long after walking off an edge, so a late press isn't punished.</summary>
    public const float Forgiveness = 0.12f;

    public void Update(GameTime time)
    {
        float seconds = time.DeltaSeconds;
        float floor = levels.Current.Start.Y - Levels.FallDepth;

        foreach (var row in world.Query<Hero, Transform>())
        {
            ref var hero = ref row.First;
            var walk = chase.Walk(actions.Vector(Control.Move)) * Speed;
            var velocity = Jump(ref hero, new Vector3(walk.X, hero.Velocity.Y - Gravity * seconds, walk.Z));
            var ride = Ride(hero);

            var move = physics.Characters.Move(row.Entity, velocity + ride, seconds);
            hero.Velocity = move.Velocity - ride;
            hero.Ground = move.Ground;
            Land(ref hero, move.Grounded, seconds);

            if (walk.LengthSquared() > 0.01f)
            {
                row.Second.Rotation = Transform.Facing(Vector3.Zero, walk).Rotation;
            }

            if (row.Second.Position.Y < floor)
            {
                world.Teleport(row.Entity, row.Second with { Position = hero.Home });
                hero.Velocity = Vector3.Zero;
                journey.Falls++;
                chase.Jump();
            }

            chase.Update(world.Get<Transform>(row.Entity).Position, time);
        }
    }

    /// <summary>Starts a jump when the button is pressed, and cuts one short when it's let go.</summary>
    private Vector3 Jump(ref Hero hero, Vector3 velocity)
    {
        if (actions.Pressed(Control.Jump) && hero.AirTime <= Forgiveness)
        {
            hero.AirTime = Forgiveness + 1f;
            return velocity with { Y = JumpSpeed };
        }

        if (actions.Released(Control.Jump) && velocity.Y > 0f)
        {
            // Letting go early makes a short hop.
            return velocity with { Y = velocity.Y * 0.5f };
        }

        return velocity;
    }

    /// <summary>How fast the lift under the bird is moving. A character isn't carried by what it stands on.</summary>
    private Vector3 Ride(Hero hero) =>
        hero.Ground is { } ground && world.IsAlive(ground) && world.Has<Lift>(ground) ? world.Get<Lift>(ground).Velocity : Vector3.Zero;

    /// <summary>Keeps count of how long the bird has been in the air, and stops it falling once it lands.</summary>
    private static void Land(ref Hero hero, bool grounded, float seconds)
    {
        if (grounded)
        {
            hero.AirTime = 0f;
            hero.Velocity = hero.Velocity with { Y = 0f };
        }
        else
        {
            hero.AirTime += seconds;
        }
    }
}
