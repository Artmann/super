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
    }

    /// <summary>Gives every entity that gets a <typeparamref name="TComponent"/> a <typeparamref name="TNeeded"/> it doesn't have.</summary>
    public void Add<TComponent, TNeeded>(TNeeded value)
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

        var list = _byType[component] ??= [];
        int existing = list.FindIndex(requirement => requirement.Needed == needed);
        var requirement = new Requirement<TNeeded>(needed, value);
        if (existing >= 0)
        {
            list[existing] = requirement;
        }
        else
        {
            list.Add(requirement);
        }
    }

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

    private abstract class Requirement(int needed)
    {
        public int Needed { get; } = needed;

        public abstract void ApplyTo(World world, Entity entity);
    }

    private sealed class Requirement<T>(int needed, T value) : Requirement(needed)
        where T : struct
    {
        public override void ApplyTo(World world, Entity entity)
        {
            if (!world.Has<T>(entity))
            {
                world.Add(entity, value);
            }
        }
    }
}
