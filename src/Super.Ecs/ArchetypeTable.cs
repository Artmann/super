namespace Super.Ecs;

/// <summary>
/// A world's component type IDs and its archetypes, one for each set of component types an entity has had. Archetypes
/// are only ever added, in the order they're first needed, so walking them visits entities in a repeatable order.
/// </summary>
internal sealed class ArchetypeTable
{
    private const int MaxComponentTypes = Archetype.MaxComponentTypes;

    private readonly Dictionary<Type, int> _componentTypes = [];
    private readonly Dictionary<ulong, Archetype> _byMask = [];
    private readonly List<Archetype> _all = [];

    public ArchetypeTable()
    {
        Empty = new Archetype(0, null, null, -1);
        Register(Empty);
    }

    /// <summary>The archetype of an entity with no components, where every new entity starts.</summary>
    public Archetype Empty { get; }

    /// <summary>The type's ID, registering it the first time it's asked for.</summary>
    public int TypeId<T>()
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

    /// <summary>The type's ID, or -1 if no entity has ever had it. Unlike TypeId, this registers nothing.</summary>
    public int TypeIdOrMissing<T>() => _componentTypes.TryGetValue(typeof(T), out int id) ? id : -1;

    /// <summary>The next archetype after <paramref name="index"/> that has rows and every type in <paramref name="mask"/>, or null.</summary>
    public Archetype? NextMatching(ref int index, ulong mask)
    {
        while (++index < _all.Count)
        {
            var archetype = _all[index];
            if (archetype.Count > 0 && (archetype.Mask & mask) == mask)
            {
                return archetype;
            }
        }

        return null;
    }

    /// <summary>The archetype with <paramref name="source"/>'s types and <typeparamref name="T"/>, made the first time.</summary>
    public Archetype With<T>(Archetype source, int type)
    {
        ulong mask = source.Mask | (1UL << type);
        if (!_byMask.TryGetValue(mask, out var target))
        {
            target = new Archetype(mask, source, new Column<T>(16), type);
            Register(target);
        }

        return target;
    }

    /// <summary>The archetype with <paramref name="source"/>'s types less the one in <paramref name="bit"/>, made the first time.</summary>
    public Archetype Without(Archetype source, ulong bit)
    {
        ulong mask = source.Mask & ~bit;
        if (!_byMask.TryGetValue(mask, out var target))
        {
            target = new Archetype(mask, source, null, -1);
            Register(target);
        }

        return target;
    }

    private void Register(Archetype archetype)
    {
        _byMask.Add(archetype.Mask, archetype);
        _all.Add(archetype);
    }
}
