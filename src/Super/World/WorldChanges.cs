namespace Super;

/// <summary>Tells the world about a change it can't see for itself.</summary>
public static class WorldChanges
{
    /// <summary>
    /// Says a <see cref="Static"/> entity was changed in place, such as a <see cref="Transform"/> written through
    /// <see cref="World.Get{T}"/>, so the world draws it again. The world keeps what it drew for static entities and
    /// draws them again only when one is made, destroyed, given or relieved of a component, or named here. For an
    /// entity that isn't static this does nothing, since those are drawn afresh every frame.
    /// </summary>
    public static void Changed(this World world, Entity entity)
    {
        if (world.Has<Static>(entity))
        {
            world.Archetypes.Touched(default(ComponentMask).With(world.Archetypes.TypeId<Static>()));
        }
    }
}
