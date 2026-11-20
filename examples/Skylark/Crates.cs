using System.Numerics;
using Super;
using Super.Physics;

namespace SkylarkExample;

/// <summary>Puts a crate that fell off back where it started, standing still.</summary>
public sealed class Crates(World world, Physics physics, Levels levels) : ISystem
{
    public void Update(GameTime time)
    {
        float floor = levels.Current.Start.Y - Levels.FallDepth;
        foreach (var row in world.Query<Crate, Transform>())
        {
            if (row.Second.Position.Y < floor)
            {
                world.Teleport(row.Entity, Transform.At(row.First.Home));
                physics.Bodies.SetVelocity(row.Entity, Vector3.Zero);
                physics.Bodies.SetAngularVelocity(row.Entity, Vector3.Zero);
            }
        }
    }
}
