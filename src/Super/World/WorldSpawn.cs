namespace Super;

/// <summary>
/// Makes an entity with all its components at once, and says which components bring others. The given
/// components go on first and the required ones after, so a required component never replaces one that was given.
/// </summary>
public static class WorldSpawn
{
    public static Entity Spawn<T1>(this World world, in T1 first)
        where T1 : struct
    {
        var entity = world.Create();
        BringRequired(world, entity, Place(world, entity, in first));
        return entity;
    }

    public static Entity Spawn<T1, T2>(this World world, in T1 first, in T2 second)
        where T1 : struct
        where T2 : struct
    {
        var entity = world.Create();
        int a = Place(world, entity, in first);
        int b = Place(world, entity, in second);
        BringRequired(world, entity, a);
        BringRequired(world, entity, b);
        return entity;
    }

    public static Entity Spawn<T1, T2, T3>(this World world, in T1 first, in T2 second, in T3 third)
        where T1 : struct
        where T2 : struct
        where T3 : struct
    {
        var entity = world.Create();
        int a = Place(world, entity, in first);
        int b = Place(world, entity, in second);
        int c = Place(world, entity, in third);
        BringRequired(world, entity, a);
        BringRequired(world, entity, b);
        BringRequired(world, entity, c);
        return entity;
    }

    public static Entity Spawn<T1, T2, T3, T4>(this World world, in T1 first, in T2 second, in T3 third, in T4 fourth)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
    {
        var entity = world.Create();
        int a = Place(world, entity, in first);
        int b = Place(world, entity, in second);
        int c = Place(world, entity, in third);
        int d = Place(world, entity, in fourth);
        BringRequired(world, entity, a);
        BringRequired(world, entity, b);
        BringRequired(world, entity, c);
        BringRequired(world, entity, d);
        return entity;
    }

    /// <summary>
    /// Gives every entity that gets a <typeparamref name="TComponent"/> a copy of <paramref name="value"/>, unless
    /// it has a <typeparamref name="TNeeded"/> already. Entities that have one now are left as they are.
    /// </summary>
    public static void Require<TComponent, TNeeded>(this World world, TNeeded value)
        where TComponent : struct
        where TNeeded : struct =>
        world.Archetypes.Required.Add<TComponent, TNeeded>(value);

    // The component's type ID, or -1 when the same type was already given earlier in the call.
    private static int Place<T>(World world, Entity entity, in T component)
        where T : struct =>
        world.Put(entity, in component, out int type) ? type : -1;

    private static void BringRequired(World world, Entity entity, int type)
    {
        if (type >= 0)
        {
            world.Archetypes.Required.ApplyTo(world, entity, type);
        }
    }
}
