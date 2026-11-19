namespace Super;

/// <summary>
/// A world's component type IDs and its archetypes, one for each set of component types an entity has had. Archetypes
/// are only ever added, in the order they're first needed, so walking them visits entities in a repeatable order.
/// </summary>
internal sealed class ArchetypeTable
{
    private const int MaxComponentTypes = Archetype.MaxComponentTypes;

    private readonly Dictionary<Type, int> _componentTypes = [];
    private readonly Dictionary<ComponentMask, Archetype> _byMask = [];
    private readonly List<Archetype> _all = [];
    private int _staticType = -1;

    public ArchetypeTable()
    {
        Empty = new Archetype(default, null, null, -1);
        Register(Empty);
        Required = new RequiredComponents(this);
        Newest = new Newest(this);
    }

    /// <summary>The archetype of an entity with no components, where every new entity starts.</summary>
    public Archetype Empty { get; }

    /// <summary>What each component type brings with it, which <see cref="World.Add{T}"/> and Spawn apply.</summary>
    public RequiredComponents Required { get; }

    /// <summary>The handlers run when components land on entities or leave them.</summary>
    public Reactions Reactions { get; } = new();

    /// <summary>Which entity got each one-per-frame component most recently, such as the camera the frame is taken from.</summary>
    public Newest Newest { get; }

    /// <summary>
    /// Goes up whenever a <see cref="Static"/> entity is made, destroyed, given or relieved of a component, or named in
    /// <see cref="WorldChanges.Changed"/>, so what was drawn for static entities is kept until it changes.
    /// </summary>
    public long StaticVersion { get; private set; }

    /// <summary>Counts a change to an entity of this mask, when the mask is a static one.</summary>
    public void Touched(in ComponentMask mask)
    {
        if (_staticType >= 0 && mask.Has(_staticType))
        {
            StaticVersion++;
        }
    }

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
            if (typeof(T) == typeof(Static))
            {
                _staticType = id;
            }
        }

        return id;
    }

    /// <summary>The type's ID, or -1 if no entity has ever had it. Unlike TypeId, this registers nothing.</summary>
    public int TypeIdOrMissing<T>() => _componentTypes.TryGetValue(typeof(T), out int id) ? id : -1;

    /// <summary>The next archetype after <paramref name="index"/> that has rows and every type in <paramref name="mask"/>, or null.</summary>
    public Archetype? NextMatching(ref int index, in ComponentMask mask)
    {
        while (++index < _all.Count)
        {
            var archetype = _all[index];
            if (archetype.Count > 0 && archetype.Mask.ContainsAll(mask))
            {
                return archetype;
            }
        }

        return null;
    }

    /// <summary>The archetype with <paramref name="source"/>'s types and <typeparamref name="T"/>, made the first time.</summary>
    public Archetype With<T>(Archetype source, int type)
    {
        var mask = source.Mask.With(type);
        if (!_byMask.TryGetValue(mask, out var target))
        {
            target = new Archetype(mask, source, new Column<T>(16), type);
            Register(target);
        }

        return target;
    }

    /// <summary>The archetype with <paramref name="source"/>'s types less <paramref name="type"/>, made the first time.</summary>
    public Archetype Without(Archetype source, int type)
    {
        var mask = source.Mask.Without(type);
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
