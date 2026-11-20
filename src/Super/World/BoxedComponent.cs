using System.Collections.Concurrent;

namespace Super;

/// <summary>
/// Lets a spawn take a component held as an object, for <c>Spawn</c> with a list of any length: finds the
/// component's real type once, with reflection, and from then on writes it with no more than an unboxing.
/// </summary>
internal abstract class BoxedComponent
{
    private static readonly ConcurrentDictionary<Type, BoxedComponent> Kinds = new();

    /// <summary>The way to spawn with <paramref name="component"/>, which must be a struct.</summary>
    public static BoxedComponent Of(object? component)
    {
        if (component is null)
        {
            throw new ArgumentException("Spawn was given null where a component should be.");
        }

        var type = component.GetType();
        if (Kinds.TryGetValue(type, out var kind))
        {
            return kind;
        }

        if (!type.IsValueType)
        {
            throw new ArgumentException($"A component must be a struct, and {type.Name} is a class.");
        }

        return Kinds.GetOrAdd(type, static made => (BoxedComponent)Activator.CreateInstance(typeof(BoxedComponent<>).MakeGenericType(made))!);
    }

    /// <summary>The component's type ID in <paramref name="world"/>.</summary>
    public abstract int TypeId(World world);

    /// <summary>Gives the spawn <paramref name="component"/>, as its typed <c>Give</c> would.</summary>
    public abstract void Give(WorldSpawn.Spawning spawning, SpawnPlan plan, int row, object component);
}

/// <summary>A <see cref="BoxedComponent"/> for one component type. Made through reflection.</summary>
internal sealed class BoxedComponent<T> : BoxedComponent
    where T : struct
{
    public override int TypeId(World world) => world.TypeId<T>();

    public override void Give(WorldSpawn.Spawning spawning, SpawnPlan plan, int row, object component) =>
        spawning.Give(plan, row, spawning.TypeId<T>(), (T)component);
}
