namespace Super;

/// <summary>
/// Makes an entity with all its components at once, and says which components bring others. The given
/// components go on first and the required ones after, so a required component never replaces one that was given.
/// </summary>
public static class WorldSpawn
{
    /// <summary>Stands for no scene, for an entity spawned straight on the world.</summary>
    internal const int NoScene = -1;

    public static Entity Spawn<T1>(this World world, in T1 first)
        where T1 : struct
    {
        using var spawn = new Spawning(world, NoScene);
        return spawn.Fill(in first);
    }

    public static Entity Spawn<T1, T2>(this World world, in T1 first, in T2 second)
        where T1 : struct
        where T2 : struct
    {
        using var spawn = new Spawning(world, NoScene);
        return spawn.Fill(in first, in second);
    }

    public static Entity Spawn<T1, T2, T3>(this World world, in T1 first, in T2 second, in T3 third)
        where T1 : struct
        where T2 : struct
        where T3 : struct
    {
        using var spawn = new Spawning(world, NoScene);
        return spawn.Fill(in first, in second, in third);
    }

    public static Entity Spawn<T1, T2, T3, T4>(this World world, in T1 first, in T2 second, in T3 third, in T4 fourth)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
    {
        using var spawn = new Spawning(world, NoScene);
        return spawn.Fill(in first, in second, in third, in fourth);
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
    /// One spawn under way: the new entity, owned by a scene or by none when the scene is <see cref="NoScene"/>, with
    /// its <c>OnAdded</c> handlers held back until the spawn is disposed, so they see every component.
    /// </summary>
    internal readonly struct Spawning : IDisposable
    {
        private readonly World _world;
        private readonly PendingChanges.Held _held;

        // A scene's tag goes on first, so OnAdded handlers see it, and the given components still win over required ones.
        public Spawning(World world, int scene)
        {
            _world = world;
            Entity = world.Create();
            _held = world.Pending.Hold(world);
            if (scene != NoScene)
            {
                world.Put(Entity, new SceneMember(scene), out _);
            }
        }

        public Entity Entity { get; }

        public Entity Fill<T1>(in T1 first)
            where T1 : struct
        {
            BringRequired(Place(in first));
            return Entity;
        }

        public Entity Fill<T1, T2>(in T1 first, in T2 second)
            where T1 : struct
            where T2 : struct
        {
            int a = Place(in first);
            int b = Place(in second);
            BringRequired(a);
            BringRequired(b);
            return Entity;
        }

        public Entity Fill<T1, T2, T3>(in T1 first, in T2 second, in T3 third)
            where T1 : struct
            where T2 : struct
            where T3 : struct
        {
            int a = Place(in first);
            int b = Place(in second);
            int c = Place(in third);
            BringRequired(a);
            BringRequired(b);
            BringRequired(c);
            return Entity;
        }

        public Entity Fill<T1, T2, T3, T4>(in T1 first, in T2 second, in T3 third, in T4 fourth)
            where T1 : struct
            where T2 : struct
            where T3 : struct
            where T4 : struct
        {
            int a = Place(in first);
            int b = Place(in second);
            int c = Place(in third);
            int d = Place(in fourth);
            BringRequired(a);
            BringRequired(b);
            BringRequired(c);
            BringRequired(d);
            return Entity;
        }

        public void Dispose() => _held.Dispose();

        // The component's type ID, or -1 when the same type was already given earlier in the call.
        private int Place<T>(in T component)
            where T : struct =>
            _world.Put(Entity, in component, out int type) ? type : -1;

        private void BringRequired(int type)
        {
            if (type >= 0)
            {
                _world.Archetypes.Required.ApplyTo(_world, Entity, type);
            }
        }
    }
}
