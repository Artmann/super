namespace Super;

/// <summary>
/// Where each entity lives: its archetype and row, and the generation that tells a live handle from a stale one. IDs of
/// destroyed entities are used again, most recently freed first.
/// </summary>
internal sealed class EntitySlots
{
    private readonly Stack<int> _freeIds = [];
    private Slot[] _slots = new Slot[64];
    private int _nextId;

    /// <summary>How many entities are alive.</summary>
    public int Count { get; private set; }

    /// <summary>A new entity with no components, in the <paramref name="empty"/> archetype.</summary>
    public Entity Create(Archetype empty)
    {
        int id = _freeIds.Count > 0 ? _freeIds.Pop() : _nextId++;
        if (id == _slots.Length)
        {
            Array.Resize(ref _slots, id * 2);
        }

        ref var slot = ref _slots[id];
        var entity = new Entity(id, slot.Generation);
        slot.Archetype = empty;
        slot.Row = empty.AddRow(entity);
        Count++;
        return entity;
    }

    /// <summary>Takes the entity out of its archetype and frees its ID, so its handles stop being alive.</summary>
    public void Free(Entity entity)
    {
        ref var slot = ref Of(entity);
        RemoveFromArchetype(ref slot);
        slot.Archetype = null;
        slot.Generation++;
        _freeIds.Push(entity.Id);
        Count--;
    }

    public bool IsAlive(Entity entity) =>
        entity.Id >= 0 && entity.Id < _nextId && _slots[entity.Id].Archetype is not null && _slots[entity.Id].Generation == entity.Generation;

    /// <summary>The live entity's slot, by reference.</summary>
    public ref Slot Of(Entity entity)
    {
        if (!IsAlive(entity))
        {
            throw new InvalidOperationException($"Entity {entity.Id} is not alive.");
        }

        return ref _slots[entity.Id];
    }

    /// <summary>Moves the entity in <paramref name="slot"/> to <paramref name="target"/>, copying the components both have.</summary>
    public void MoveTo(ref Slot slot, Archetype target)
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

    internal struct Slot
    {
        public Archetype? Archetype;
        public int Row;
        public int Generation;
    }
}
