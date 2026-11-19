using System.Numerics;
using Super;

namespace Hamlet;

/// <summary>Small helpers for the village's entities.</summary>
internal static class Spawning
{
    /// <summary>The transform that places things as <paramref name="matrix"/> does. It must have no shear.</summary>
    public static Transform Of(Matrix4x4 matrix)
    {
        Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position);
        return new Transform(position, rotation, scale);
    }

    /// <summary>Hides <paramref name="entity"/>, model and light, or shows it again.</summary>
    public static void Show(World world, Entity entity, bool shown)
    {
        bool hidden = world.Has<Hidden>(entity);
        if (shown && hidden)
        {
            world.Remove<Hidden>(entity);
        }
        else if (!shown && !hidden)
        {
            world.Add(entity, new Hidden());
        }
    }
}
