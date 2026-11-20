namespace Super;

/// <summary>
/// Makes an entity with all its components at once, and says which components bring others. The given
/// components go on first and the required ones after, so a required component never replaces one that was given.
/// </summary>
public static class WorldSpawn
{
    /// <summary>Stands for no scene, for an entity spawned straight on the world.</summary>
    internal const int NoScene = -1;

    /// <summary>
    /// Finishes a spawn whose values are all written in its row: counts the change, indexes its parent, and runs the
    /// <c>OnAdded</c> handlers of <paramref name="types"/>.
    /// </summary>
    internal static void Landed(World world, Entity entity, Archetype archetype, int row, int[] types)
    {
        var archetypes = world.Archetypes;
        archetypes.Touched(archetype.Mask);
        int parentType = archetypes.Types.ParentType;
        if (parentType >= 0 && archetype.Mask.Has(parentType))
        {
            archetypes.Children.Set(entity, archetype.Column<Parent>(parentType).Values[row].Value);
        }

        bool anyHandlers = false;
        foreach (int type in types)
        {
            anyHandlers |= archetypes.Reactions.HasAdded(type);
        }

        if (!anyHandlers)
        {
            return;
        }

        // The hold makes the handlers run as it ends, all queued, as when the components land one at a time.
        using var held = world.Pending.Hold(world);
        foreach (int type in types)
        {
            if (archetypes.Reactions.HasAdded(type))
            {
                world.Pending.Added(entity, type);
            }
        }
    }

    public static Entity Spawn<T1>(this World world, in T1 first)
        where T1 : struct =>
        new Spawning(world, NoScene).Fill(in first);

    public static Entity Spawn<T1, T2>(this World world, in T1 first, in T2 second)
        where T1 : struct
        where T2 : struct =>
        new Spawning(world, NoScene).Fill(in first, in second);

    public static Entity Spawn<T1, T2, T3>(this World world, in T1 first, in T2 second, in T3 third)
        where T1 : struct
        where T2 : struct
        where T3 : struct =>
        new Spawning(world, NoScene).Fill(in first, in second, in third);

    public static Entity Spawn<T1, T2, T3, T4>(this World world, in T1 first, in T2 second, in T3 third, in T4 fourth)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct =>
        new Spawning(world, NoScene).Fill(in first, in second, in third, in fourth);

    extension(World world)
    {
        public Entity Spawn<T1, T2, T3, T4, T5>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth)
            where T1 : struct
            where T2 : struct
            where T3 : struct
            where T4 : struct
            where T5 : struct =>
            new Spawning(world, NoScene).Fill(in first, in second, in third, in fourth, in fifth);
    }

    /// <summary>
    /// Makes an entity with any number of components, for when five aren't enough: write them all as arguments, or
    /// pass an array or a collection such as <c>[.. parts]</c>. It lands in one move like the others, but each
    /// component is boxed on the way, so keep the overloads up to five for what spawns often. Each must be a struct.
    /// </summary>
    public static Entity Spawn(this World world, params ReadOnlySpan<object> components) =>
        new Spawning(world, NoScene).Fill(components);

    /// <summary>
    /// Gives every entity that gets a <typeparamref name="TComponent"/> a copy of <paramref name="value"/>, unless
    /// it has a <typeparamref name="TNeeded"/> already. Entities that have one now are left as they are.
    /// </summary>
    public static void Require<TComponent, TNeeded>(this World world, TNeeded value)
        where TComponent : struct
        where TNeeded : struct =>
        world.Archetypes.Required.Add<TComponent, TNeeded>(value);

    /// <summary>
    /// One spawn: a new entity, owned by a scene or by none when the scene is <see cref="NoScene"/>. It goes straight
    /// into the archetype its components and their required ones make, from a <see cref="SpawnPlan"/>, with every
    /// value written in place. During a query the entity is alive at once, in the empty archetype that no query walks,
    /// and the whole spawn waits as one change, made in the same single move when the query ends.
    /// </summary>
    // A scene's tag goes on first, so OnAdded handlers see it, and the given components still win over required ones.
    internal readonly struct Spawning(World world, int scene)
    {
        // The row for a spawn that waits for a query.
        private const int Queued = -1;

        public Entity Fill<T1>(in T1 first)
            where T1 : struct
        {
            int a = world.TypeId<T1>();
            var plan = Plan(new SpawnKey(SceneType, a));
            var entity = Start(plan, out int row);
            Give(plan, row, a, in first);
            return Finish(entity, plan, row);
        }

        public Entity Fill<T1, T2>(in T1 first, in T2 second)
            where T1 : struct
            where T2 : struct
        {
            int a = world.TypeId<T1>();
            int b = world.TypeId<T2>();
            var plan = Plan(new SpawnKey(SceneType, a, b));
            var entity = Start(plan, out int row);
            Give(plan, row, a, in first);
            Give(plan, row, b, in second);
            return Finish(entity, plan, row);
        }

        public Entity Fill<T1, T2, T3>(in T1 first, in T2 second, in T3 third)
            where T1 : struct
            where T2 : struct
            where T3 : struct
        {
            int a = world.TypeId<T1>();
            int b = world.TypeId<T2>();
            int c = world.TypeId<T3>();
            var plan = Plan(new SpawnKey(SceneType, a, b, c));
            var entity = Start(plan, out int row);
            Give(plan, row, a, in first);
            Give(plan, row, b, in second);
            Give(plan, row, c, in third);
            return Finish(entity, plan, row);
        }

        public Entity Fill<T1, T2, T3, T4>(in T1 first, in T2 second, in T3 third, in T4 fourth)
            where T1 : struct
            where T2 : struct
            where T3 : struct
            where T4 : struct
        {
            int a = world.TypeId<T1>();
            int b = world.TypeId<T2>();
            int c = world.TypeId<T3>();
            int d = world.TypeId<T4>();
            var plan = Plan(new SpawnKey(SceneType, a, b, c, d));
            var entity = Start(plan, out int row);
            Give(plan, row, a, in first);
            Give(plan, row, b, in second);
            Give(plan, row, c, in third);
            Give(plan, row, d, in fourth);
            return Finish(entity, plan, row);
        }

        public Entity Fill<T1, T2, T3, T4, T5>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth)
            where T1 : struct
            where T2 : struct
            where T3 : struct
            where T4 : struct
            where T5 : struct
        {
            int a = world.TypeId<T1>();
            int b = world.TypeId<T2>();
            int c = world.TypeId<T3>();
            int d = world.TypeId<T4>();
            int e = world.TypeId<T5>();
            var plan = Plan(new SpawnKey(SceneType, a, b, c, d, e));
            var entity = Start(plan, out int row);
            Give(plan, row, a, in first);
            Give(plan, row, b, in second);
            Give(plan, row, c, in third);
            Give(plan, row, d, in fourth);
            Give(plan, row, e, in fifth);
            return Finish(entity, plan, row);
        }

        // Each component is looked up twice, for its type and then to write it, which keeps this free of arrays.
        public Entity Fill(ReadOnlySpan<object> components)
        {
            int count = components.Length + 1;
            Span<int> key = count <= 64 ? stackalloc int[count] : new int[count];
            key[0] = SceneType;
            for (int i = 0; i < components.Length; i++)
            {
                key[i + 1] = BoxedComponent.Of(components[i]).TypeId(world);
            }

            var plan = world.Archetypes.Plans.For(key);
            var entity = Start(plan, out int row);
            foreach (object component in components)
            {
                BoxedComponent.Of(component).Give(this, plan, row, component);
            }

            return Finish(entity, plan, row);
        }

        public int TypeId<T>()
            where T : struct => world.TypeId<T>();

        private int SceneType => scene == NoScene ? -1 : world.TypeId<SceneMember>();

        private SpawnPlan Plan(in SpawnKey key) => world.Archetypes.Plans.For(in key);

        private Entity Start(SpawnPlan plan, out int row)
        {
            Entity entity;
            if (world.IsQuerying)
            {
                // A query may be walking the plan's archetype, so the entity waits in the empty one.
                entity = world.Create();
                world.Pending.Spawn(entity, plan);
                row = Queued;
            }
            else
            {
                entity = world.Entities.Create(plan.Target, out row);
            }

            if (scene != NoScene)
            {
                Give(plan, row, world.TypeId<SceneMember>(), new SceneMember(scene));
            }

            return entity;
        }

        // Given in call order, so a type given twice keeps the later value, as two adds would.
        public void Give<T>(SpawnPlan plan, int row, int type, in T component)
            where T : struct
        {
            if (row == Queued)
            {
                world.Pending.Values.Give(type, in component);
            }
            else
            {
                plan.Target.Column<T>(type).Values[row] = component;
            }
        }

        private Entity Finish(Entity entity, SpawnPlan plan, int row)
        {
            if (row == Queued)
            {
                return entity;
            }

            foreach (var requirement in plan.Required)
            {
                requirement.WriteTo(plan.Target, row);
            }

            Landed(world, entity, plan.Target, row, plan.Landed);
            return entity;
        }
    }
}
