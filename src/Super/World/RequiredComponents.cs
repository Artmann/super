namespace Super;

/// <summary>
/// The components an entity is given when it gets another, such as a <see cref="Transform"/> for a
/// <see cref="Model"/>. They're kept per component type ID as typed entries, so applying them boxes nothing.
/// </summary>
internal sealed class RequiredComponents
{
    private readonly ArchetypeTable _types;
    private readonly List<Requirement>?[] _byType = new List<Requirement>?[ComponentMask.MaxTypes];

    public RequiredComponents(ArchetypeTable types)
    {
        _types = types;

        // A model or a skeleton is drawn where its transform puts it, so one is never left without.
        Add<Model, Transform>(Transform.Identity);
        Add<Animated, Transform>(Transform.Identity);

        // Anything placed can move, and drawing blends it from where it was at the last tick. A transform, a swim or
        // a static that lands starts it afresh, so nothing glides from where it was before it was taken away, or
        // from a tick that never saw its phase.
        Add<Transform, LastTick>(default, replace: true);
        Add<Swim, LastTick>(default, replace: true);
        Add<Static, LastTick>(default, replace: true);

        // A camera blends between ticks the same way, and one that lands starts afresh.
        Add<Camera, LastCamera>(default, replace: true);

        // A rig needs a camera to write to.
        Add<OnRig, Camera>(WorldRigs.Unplaced);
    }

    /// <summary>
    /// Gives every entity that gets a <typeparamref name="TComponent"/> a <typeparamref name="TNeeded"/> it doesn't
    /// have, or with <paramref name="replace"/>, sets it even when it has one.
    /// </summary>
    public void Add<TComponent, TNeeded>(TNeeded value, bool replace = false)
        where TNeeded : struct
    {
        int component = _types.TypeId<TComponent>();
        int needed = _types.TypeId<TNeeded>();
        string componentName = typeof(TComponent).Name;
        string neededName = typeof(TNeeded).Name;
        if (component == needed)
        {
            throw new InvalidOperationException($"{componentName} can't require itself.");
        }

        if (Reaches(needed, component))
        {
            throw new InvalidOperationException(
                $"{componentName} can't require {neededName}, because {neededName} already requires {componentName}.");
        }

        Version++;
        var list = _byType[component] ??= [];
        int existing = list.FindIndex(requirement => requirement.Needed == needed);
        var requirement = new Requirement<TNeeded>(needed, value, replace);
        if (existing >= 0)
        {
            list[existing] = requirement;
        }
        else
        {
            list.Add(requirement);
        }
    }

    /// <summary>Goes up whenever a requirement is added or replaced, so spawn plans made before it are made again.</summary>
    public int Version { get; private set; }

    /// <summary>What <paramref name="type"/> brings, in the order it was asked for, or null for nothing.</summary>
    public List<Requirement>? Of(int type) => _byType[type];

    /// <summary>Adds what <paramref name="type"/> needs to the entity, leaving what it already has.</summary>
    public void ApplyTo(World world, Entity entity, int type)
    {
        if (_byType[type] is not { } list)
        {
            return;
        }

        foreach (var requirement in list)
        {
            requirement.ApplyTo(world, entity);
        }
    }

    // Whether a component of type 'from' leads, through requirements, to one of type 'to'.
    private bool Reaches(int from, int to)
    {
        if (from == to)
        {
            return true;
        }

        if (_byType[from] is not { } list)
        {
            return false;
        }

        foreach (var requirement in list)
        {
            if (Reaches(requirement.Needed, to))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One component that another brings: its type ID, its value, and whether it replaces one already there.</summary>
    internal abstract class Requirement(int needed, bool replace)
    {
        public int Needed { get; } = needed;

        public bool Replace { get; } = replace;

        public abstract void ApplyTo(World world, Entity entity);

        /// <summary>Sets the value in a row of an archetype that has the type, for a spawn made in one move.</summary>
        public abstract void WriteTo(Archetype archetype, int row);
    }

    private sealed class Requirement<T>(int needed, T value, bool replace) : Requirement(needed, replace)
        where T : struct
    {
        public override void ApplyTo(World world, Entity entity)
        {
            if (Replace || !world.Has<T>(entity))
            {
                world.Add(entity, value);
            }
        }

        public override void WriteTo(Archetype archetype, int row) => archetype.Column<T>(Needed).Values[row] = value;
    }
}
