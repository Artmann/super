namespace Super.Ecs;

/// <summary>A thing in the world. It's only an ID; its data lives in components.</summary>
public readonly record struct Entity(int Id, int Generation);

/// <summary>
/// Holds every entity and its components. Each World stands alone, so several games can
/// run side by side. Queries visit entities in an order that only depends on what was
/// done to the world, which keeps seeded runs repeatable.
/// </summary>
public sealed partial class World
{
    internal const int MaxComponentTypes = 64;

    private readonly Dictionary<Type, int> _componentTypes = [];
    private readonly Dictionary<ulong, Archetype> _archetypesByMask = [];
    private readonly List<Archetype> _archetypes = [];
    private readonly Stack<int> _freeIds = [];
    private readonly Archetype _empty;
    private Slot[] _slots = new Slot[64];
    private int _nextId;
    private int _iterating;

    public World()
    {
        _empty = new Archetype(0, null, null, -1);
        _archetypesByMask.Add(0, _empty);
        _archetypes.Add(_empty);
    }

    /// <summary>How many entities are alive.</summary>
    public int Count { get; private set; }

    public Entity Create()
    {
        ThrowIfIterating();
        int id = _freeIds.Count > 0 ? _freeIds.Pop() : _nextId++;
        if (id == _slots.Length)
        {
            Array.Resize(ref _slots, id * 2);
        }

        ref var slot = ref _slots[id];
        var entity = new Entity(id, slot.Generation);
        slot.Archetype = _empty;
        slot.Row = _empty.AddRow(entity);
        Count++;
        return entity;
    }

    /// <summary>Removes an entity. Its ID can be reused, but old handles to it stop being alive.</summary>
    public void Destroy(Entity entity)
    {
        ThrowIfIterating();
        ref var slot = ref SlotOf(entity);
        RemoveFromArchetype(ref slot);
        slot.Archetype = null;
        slot.Generation++;
        _freeIds.Push(entity.Id);
        Count--;
    }

    public bool IsAlive(Entity entity) =>
        entity.Id >= 0 && entity.Id < _nextId && _slots[entity.Id].Archetype is not null && _slots[entity.Id].Generation == entity.Generation;

    /// <summary>Gives the entity a component, or replaces the one it has.</summary>
    public void Add<T>(Entity entity, T component)
        where T : struct
    {
        ref var slot = ref SlotOf(entity);
        int type = TypeId<T>();
        ulong bit = 1UL << type;
        if ((slot.Archetype!.Mask & bit) == 0)
        {
            ThrowIfIterating();
            MoveTo(ref slot, ArchetypeWith<T>(slot.Archetype, type));
        }

        slot.Archetype!.Column<T>(type).Values[slot.Row] = component;
    }

    /// <summary>Takes a component away. Does nothing if the entity doesn't have it.</summary>
    public void Remove<T>(Entity entity)
        where T : struct
    {
        ref var slot = ref SlotOf(entity);
        ulong bit = 1UL << TypeId<T>();
        if ((slot.Archetype!.Mask & bit) == 0)
        {
            return;
        }

        ThrowIfIterating();
        ulong mask = slot.Archetype.Mask & ~bit;
        if (!_archetypesByMask.TryGetValue(mask, out var target))
        {
            target = new Archetype(mask, slot.Archetype, null, -1);
            Register(target);
        }

        MoveTo(ref slot, target);
    }

    public bool Has<T>(Entity entity)
        where T : struct =>
        (SlotOf(entity).Archetype!.Mask & (1UL << TypeId<T>())) != 0;

    /// <summary>The entity's component, by reference so it can be changed.</summary>
    public ref T Get<T>(Entity entity)
        where T : struct
    {
        ref var slot = ref SlotOf(entity);
        int type = TypeId<T>();
        if ((slot.Archetype!.Mask & (1UL << type)) == 0)
        {
            throw new InvalidOperationException($"Entity {entity.Id} has no {typeof(T).Name}.");
        }

        return ref slot.Archetype.Column<T>(type).Values[slot.Row];
    }

    internal int TypeId<T>()
    {
        if (!_componentTypes.TryGetValue(typeof(T), out int id))
        {
            if (_componentTypes.Count == MaxComponentTypes)
            {
                throw new InvalidOperationException($"A world holds at most {MaxComponentTypes} component types.");
            }

            id = _componentTypes.Count;
            _componentTypes.Add(typeof(T), id);
        }

        return id;
    }

    internal void BeginIteration() => _iterating++;

    internal void EndIteration() => _iterating--;

    internal Archetype? NextArchetype(ref int index, ulong mask)
    {
        while (++index < _archetypes.Count)
        {
            var archetype = _archetypes[index];
            if (archetype.Count > 0 && (archetype.Mask & mask) == mask)
            {
                return archetype;
            }
        }

        return null;
    }

    private Archetype ArchetypeWith<T>(Archetype source, int type)
    {
        ulong mask = source.Mask | (1UL << type);
        if (!_archetypesByMask.TryGetValue(mask, out var target))
        {
            target = new Archetype(mask, source, new Column<T>(16), type);
            Register(target);
        }

        return target;
    }

    private void Register(Archetype archetype)
    {
        _archetypesByMask.Add(archetype.Mask, archetype);
        _archetypes.Add(archetype);
    }

    private void MoveTo(ref Slot slot, Archetype target)
    {
        var source = slot.Archetype!;
        int oldRow = slot.Row;
        int row = target.AddRow(source.Entities[oldRow]);
        source.CopyRowTo(target, oldRow, row);
        RemoveFromArchetype(ref slot);
        slot.Archetype = target;
        slot.Row = row;
    }

    private void RemoveFromArchetype(ref Slot slot)
    {
        if (slot.Archetype!.RemoveRow(slot.Row) is { } moved)
        {
            _slots[moved.Id].Row = slot.Row;
        }
    }

    private ref Slot SlotOf(Entity entity)
    {
        if (!IsAlive(entity))
        {
            throw new InvalidOperationException($"Entity {entity.Id} is not alive.");
        }

        return ref _slots[entity.Id];
    }

    private void ThrowIfIterating()
    {
        if (_iterating > 0)
        {
            throw new InvalidOperationException(
                "Entities can't be created, destroyed or change their components during a query. Collect the changes and make them afterwards.");
        }
    }

    private struct Slot
    {
        public Archetype? Archetype;
        public int Row;
        public int Generation;
    }
}
