namespace Super;

/// <summary>
/// A world's component type IDs and its archetypes, one for each set of component types an entity has had. Archetypes
/// are only ever added, in the order they're first needed, so walking them visits entities in a repeatable order.
/// </summary>
internal sealed class ArchetypeTable
{
    private readonly Dictionary<ComponentMask, Archetype> _byMask = [];
    private readonly List<Archetype> _all = [];

    public ArchetypeTable()
    {
        Empty = new Archetype(default, Types.Kinds);
        Register(Empty);
        Required = new RequiredComponents(this);
        Newest = new Newest(this);
        Plans = new SpawnPlans(this);
    }

    /// <summary>The world's component type IDs.</summary>
    public ComponentTypes Types { get; } = new();

    /// <summary>Where each kind of spawn lands, so a spawn moves its entity once.</summary>
    public SpawnPlans Plans { get; }

    /// <summary>The children of each entity, so destroying one finds its children at once.</summary>
    public ChildIndex Children { get; } = new();

    /// <summary>The archetype of an entity with no components, where every new entity starts.</summary>
    public Archetype Empty { get; }

    /// <summary>What each component type brings with it, which <see cref="World.Add{T}"/> and Spawn apply.</summary>
    public RequiredComponents Required { get; }

    /// <summary>The handlers run when components land on entities or leave them.</summary>
    public Reactions Reactions { get; } = new();

    /// <summary>Which entity got each one-per-frame component most recently, such as the camera the frame is taken from.</summary>
    public Newest Newest { get; }

    /// <summary>
    /// Goes up whenever a <see cref="Static"/> or <see cref="Hidden"/> entity is made, destroyed, given or relieved of a component, or named in
    /// <see cref="WorldChanges.Changed"/>, so what was drawn for static entities is kept until it changes.
    /// </summary>
    public long StaticVersion { get; private set; }

    /// <summary>
    /// Counts a change to an entity of this mask, when the mask is a static one, or a hidden one, since hiding or
    /// showing a parent hides or shows its static children.
    /// </summary>
    public void Touched(in ComponentMask mask)
    {
        int staticType = Types.StaticType;
        int hiddenType = Types.HiddenType;
        if ((staticType >= 0 && mask.Has(staticType)) || (hiddenType >= 0 && mask.Has(hiddenType)))
        {
            StaticVersion++;
        }
    }

    /// <summary>The type's ID, registering it the first time it's asked for.</summary>
    public int TypeId<T>() => Types.TypeId<T>();

    /// <summary>The type's ID, or -1 if no entity has ever had it. Unlike TypeId, this registers nothing.</summary>
    public int TypeIdOrMissing<T>() => Types.TypeIdOrMissing<T>();

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

    /// <summary>The archetype with <paramref name="source"/>'s types and <paramref name="type"/>, made the first time.</summary>
    public Archetype With(Archetype source, int type) => source.WithEdge(type) ?? source.RememberWith(type, Find(source.Mask.With(type)));

    /// <summary>The archetype with <paramref name="source"/>'s types less <paramref name="type"/>, made the first time.</summary>
    public Archetype Without(Archetype source, int type) =>
        source.WithoutEdge(type) ?? source.RememberWithout(type, Find(source.Mask.Without(type)));

    private Archetype Find(ComponentMask mask)
    {
        if (!_byMask.TryGetValue(mask, out var target))
        {
            target = new Archetype(mask, Types.Kinds);
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
