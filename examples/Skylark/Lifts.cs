using System.Numerics;
using Super;

namespace SkylarkExample;

/// <summary>Glides every lift along its path, and notes how fast each went.</summary>
public sealed class Lifts(World world) : ISystem
{
    public void Update(GameTime time)
    {
        foreach (var row in world.Query<Lift, Transform>())
        {
            ref var lift = ref row.First;
            lift.Path.Update(time);

            var next = lift.Path.Value;
            lift.Velocity = time.DeltaSeconds > 0f ? (next - row.Second.Position) / time.DeltaSeconds : Vector3.Zero;
            row.Second.Position = next;
        }
    }
}
