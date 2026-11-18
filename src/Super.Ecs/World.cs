namespace Super.Ecs;

/// <summary>A thing in the world. It's only an ID; its data lives in components.</summary>
public readonly record struct Entity(int Id, int Generation);

/// <summary>
/// Holds every entity and its components. Each World stands alone, so several games can
/// run side by side. Queries visit entities in an order that only depends on what was
/// done to the world, which keeps seeded runs repeatable.
/// </summary>
public sealed class World : ITickHook, IStateHook
{
    private readonly EntitySlots _entities = new();
    private int _iterating;

    /// <summary>How many entities are alive.</summary>
    public int Count => _entities.Count;

    /// <summary>The component types and archetypes, which queries walk.</summary>
    internal ArchetypeTable Archetypes { get; } = new();

    public Entity Create()
    {
        ThrowIfIterating();
        return _entities.Create(Archetypes.Empty);
    }

    /// <summary>Removes an entity. Its ID can be reused, but old handles to it stop being alive.</summary>
    public void Destroy(Entity entity)
    {
        ThrowIfIterating();
        _entities.Free(entity);
    }

    public bool IsAlive(Entity entity) => _entities.IsAlive(entity);

    /// <summary>Gives the entity a component, or replaces the one it has.</summary>
    public void Add<T>(Entity entity, T component)
        where T : struct
    {
        ref var slot = ref _entities.Of(entity);
        int type = Archetypes.TypeId<T>();
        ulong bit = 1UL << type;
        if ((slot.Archetype!.Mask & bit) == 0)
        {
            ThrowIfIterating();
            _entities.MoveTo(ref slot, Archetypes.With<T>(slot.Archetype, type));
        }

        slot.Archetype!.Column<T>(type).Values[slot.Row] = component;
    }

    /// <summary>Takes a component away. Does nothing if the entity doesn't have it.</summary>
    public void Remove<T>(Entity entity)
        where T : struct
    {
        ref var slot = ref _entities.Of(entity);
        ulong bit = 1UL << Archetypes.TypeId<T>();
        if ((slot.Archetype!.Mask & bit) == 0)
        {
            return;
        }

        ThrowIfIterating();
        _entities.MoveTo(ref slot, Archetypes.Without(slot.Archetype, bit));
    }

    public bool Has<T>(Entity entity)
        where T : struct =>
        (_entities.Of(entity).Archetype!.Mask & (1UL << Archetypes.TypeId<T>())) != 0;

    /// <summary>The entity's component, by reference so it can be changed.</summary>
    public ref T Get<T>(Entity entity)
        where T : struct
    {
        ref var slot = ref _entities.Of(entity);
        int type = Archetypes.TypeId<T>();
        if ((slot.Archetype!.Mask & (1UL << type)) == 0)
        {
            throw new InvalidOperationException($"Entity {entity.Id} has no {typeof(T).Name}.");
        }

        return ref slot.Archetype.Column<T>(type).Values[slot.Row];
    }

    // Queries allocate nothing, so they're safe to run every tick and every frame. Each row
    // hands out references: changing row.First.Value changes the component in the world.
    public Query<T1> Query<T1>()
        where T1 : struct =>
        new(this);

    public Query<T1, T2> Query<T1, T2>()
        where T1 : struct
        where T2 : struct =>
        new(this);

    public Query<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : struct
        where T2 : struct
        where T3 : struct =>
        new(this);

    public Query<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct =>
        new(this);

    void ITickHook.BeforeUpdate() => SmoothedTransforms.Remember(this);

    void IStateHook.Exited<TState>(TState state) => ScopedEntities.DestroyScopedTo(this, state);

    // Whatever was just placed for the new state should appear there, not glide from the old one.
    void IStateHook.Entered() => SmoothedTransforms.Remember(this);

    /// <summary>The component type's ID, registering it the first time. Queries ask for theirs as they start.</summary>
    internal int TypeId<T>() => Archetypes.TypeId<T>();

    internal void BeginIteration() => _iterating++;

    internal void EndIteration() => _iterating--;

    private void ThrowIfIterating()
    {
        if (_iterating > 0)
        {
            throw new InvalidOperationException(
                "Entities can't be created, destroyed or change their components during a query. Collect the changes and make them afterwards.");
        }
    }
}
