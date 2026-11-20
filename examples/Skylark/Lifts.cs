using Super;

namespace SkylarkExample;

/// <summary>Glides every lift along its path.</summary>
public sealed class Lifts(World world) : ISystem
{
    public void Update(GameTime time)
    {
        foreach (var row in world.Query<Lift, Transform>())
        {
            row.First.Path.Update(time);
            row.Second.Position = row.First.Path.Value;
        }
    }
}
