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
        using var spawn = new Spawning(world);
        spawn.BringRequired(spawn.Place(in first));
        return spawn.Entity;
    }

    public static Entity Spawn<T1, T2>(this World world, in T1 first, in T2 second)
        where T1 : struct
        where T2 : struct
    {
        using var spawn = new Spawning(world);
        int a = spawn.Place(in first);
        int b = spawn.Place(in second);
        spawn.BringRequired(a);
        spawn.BringRequired(b);
        return spawn.Entity;
    }

    public static Entity Spawn<T1, T2, T3>(this World world, in T1 first, in T2 second, in T3 third)
        where T1 : struct
        where T2 : struct
        where T3 : struct
    {
        using var spawn = new Spawning(world);
        int a = spawn.Place(in first);
        int b = spawn.Place(in second);
        int c = spawn.Place(in third);
        spawn.BringRequired(a);
        spawn.BringRequired(b);
        spawn.BringRequired(c);
        return spawn.Entity;
    }

    public static Entity Spawn<T1, T2, T3, T4>(this World world, in T1 first, in T2 second, in T3 third, in T4 fourth)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
    {
        using var spawn = new Spawning(world);
        int a = spawn.Place(in first);
        int b = spawn.Place(in second);
        int c = spawn.Place(in third);
        int d = spawn.Place(in fourth);
        spawn.BringRequired(a);
        spawn.BringRequired(b);
        spawn.BringRequired(c);
        spawn.BringRequired(d);
        return spawn.Entity;
    }

    /// <summary>
    /// Gives every entity that gets a <typeparamref name="TComponent"/> a copy of <paramref name="value"/>, unless
    /// it has a <typeparamref name="TNeeded"/> already. Entities that have one now are left as they are.
    /// </summary>
    public static void Require<TComponent, TNeeded>(this World world, TNeeded value)
        where TComponent : struct
        where TNeeded : struct =>
        world.Archetypes.Required.Add<TComponent, TNeeded>(value);

    /// <summary>
    /// One spawn under way: the new entity, with its <c>OnAdded</c> handlers held back until the spawn is disposed,
    /// so they see every component.
    /// </summary>
    private readonly struct Spawning : IDisposable
    {
        private readonly World _world;
        private readonly PendingChanges.Held _held;

        public Spawning(World world)
        {
            _world = world;
            Entity = world.Create();
            _held = world.Pending.Hold(world);
        }

        public Entity Entity { get; }

        // The component's type ID, or -1 when the same type was already given earlier in the call.
        public int Place<T>(in T component)
            where T : struct =>
            _world.Put(Entity, in component, out int type) ? type : -1;

        public void BringRequired(int type)
        {
            if (type >= 0)
            {
                _world.Archetypes.Required.ApplyTo(_world, Entity, type);
            }
        }

        public void Dispose() => _held.Dispose();
    }
}
