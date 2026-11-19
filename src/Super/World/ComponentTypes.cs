namespace Super;

/// <summary>
/// A world's component type IDs, given in the order the world first meets each type, and an empty column of each type
/// for new archetypes to copy the kind of. Looking an ID up is an array read by the type's process-wide
/// <see cref="ComponentIndex{T}"/>.
/// </summary>
internal sealed class ComponentTypes
{
    private const int MaxComponentTypes = Archetype.MaxComponentTypes;

    // Each type's world ID plus one, by its process-wide index, so 0 means this world hasn't met the type.
    private int[] _byIndex = new int[64];
    private int _count;

    /// <summary>An empty column of each type, by ID, which new archetypes copy the kind of.</summary>
    public ComponentColumn[] Kinds { get; } = new ComponentColumn[MaxComponentTypes];

    /// <summary>The <see cref="Static"/> type's ID, or -1 before any entity has had one.</summary>
    public int StaticType { get; private set; } = -1;

    /// <summary>The <see cref="Hidden"/> type's ID, or -1 before any entity has had one.</summary>
    public int HiddenType { get; private set; } = -1;

    /// <summary>The <see cref="Parent"/> type's ID, or -1 before any entity has had one, so the child index can watch it.</summary>
    public int ParentType { get; private set; } = -1;

    /// <summary>The type's ID, registering it the first time it's asked for.</summary>
    public int TypeId<T>()
    {
        int index = ComponentIndex<T>.Value;
        var byIndex = _byIndex;
        if (index < byIndex.Length && byIndex[index] != 0)
        {
            return byIndex[index] - 1;
        }

        return Register<T>(index);
    }

    /// <summary>The type's ID, or -1 if no entity has ever had it. Unlike TypeId, this registers nothing.</summary>
    public int TypeIdOrMissing<T>()
    {
        int index = ComponentIndex<T>.Value;
        return index < _byIndex.Length ? _byIndex[index] - 1 : -1;
    }

    private int Register<T>(int index)
    {
        if (_count == MaxComponentTypes)
        {
            throw new InvalidOperationException($"A world holds at most {MaxComponentTypes} component types.");
        }

        int id = _count++;
        if (index >= _byIndex.Length)
        {
            Array.Resize(ref _byIndex, Math.Max(index + 1, _byIndex.Length * 2));
        }

        _byIndex[index] = id + 1;
        Kinds[id] = new Column<T>(0);
        if (typeof(T) == typeof(Static))
        {
            StaticType = id;
        }
        else if (typeof(T) == typeof(Hidden))
        {
            HiddenType = id;
        }
        else if (typeof(T) == typeof(Parent))
        {
            ParentType = id;
        }

        return id;
    }
}
