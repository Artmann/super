using System.Numerics;
using Super;
using Super.Ecs;

namespace Rooms;

/// <summary>Cubes that belong to the screen they're made in, so they go when the player leaves it.</summary>
internal static class ScopedCube
{
    /// <summary>A coloured cube at <paramref name="transform"/>, scoped to the current screen.</summary>
    public static Entity Add(World world, States<Screen> screen, Transform transform, Vector3 color)
    {
        var entity = world.Create();
        world.Add(entity, transform);
        world.Add(entity, new Model(ModelRef.Cube, color));
        world.Add(entity, new ScopedTo<Screen>(screen.Current));
        return entity;
    }
}
