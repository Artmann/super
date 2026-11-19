using Super;

namespace Hamlet;

/// <summary>Small helpers for the village's entities.</summary>
internal static class Spawning
{
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
