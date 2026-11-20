namespace Super.Physics;

/// <summary>Stands for any entity in a <see cref="TriggerPairs{TTrigger, TOther}"/>, whatever components it has.</summary>
public readonly record struct Anyone;

/// <summary>
/// Trigger events picked by component, so a game reads only its own: the gems the player walked into, and not every
/// area anything entered. Walking them allocates nothing.
/// </summary>
public static class TriggerFilters
{
    /// <summary>
    /// Who came into an area that has a <typeparamref name="TTrigger"/> in the last step, when they have a
    /// <typeparamref name="TOther"/>.
    /// </summary>
    public static TriggerPairs<TTrigger, TOther> Entered<TTrigger, TOther>(this PhysicsTriggers triggers)
        where TTrigger : struct
        where TOther : struct =>
        new(triggers.Lists.World, triggers.Lists.Entered);

    /// <summary>Everyone who came into an area that has a <typeparamref name="TTrigger"/> in the last step.</summary>
    public static TriggerPairs<TTrigger, Anyone> Entered<TTrigger>(this PhysicsTriggers triggers)
        where TTrigger : struct =>
        new(triggers.Lists.World, triggers.Lists.Entered);

    /// <summary>
    /// Who left an area that has a <typeparamref name="TTrigger"/> in the last step, when they have a
    /// <typeparamref name="TOther"/>. An entity destroyed while inside has no components left, so it isn't listed.
    /// </summary>
    public static TriggerPairs<TTrigger, TOther> Exited<TTrigger, TOther>(this PhysicsTriggers triggers)
        where TTrigger : struct
        where TOther : struct =>
        new(triggers.Lists.World, triggers.Lists.Exited);

    /// <summary>
    /// Everyone who left an area that has a <typeparamref name="TTrigger"/> in the last step, alive or not: check
    /// <see cref="World.IsAlive"/> before using them.
    /// </summary>
    public static TriggerPairs<TTrigger, Anyone> Exited<TTrigger>(this PhysicsTriggers triggers)
        where TTrigger : struct =>
        new(triggers.Lists.World, triggers.Lists.Exited);

    /// <summary>
    /// Who is in an area that has a <typeparamref name="TTrigger"/>, as of the last step, when they have a
    /// <typeparamref name="TOther"/>.
    /// </summary>
    public static TriggerPairs<TTrigger, TOther> Inside<TTrigger, TOther>(this PhysicsTriggers triggers)
        where TTrigger : struct
        where TOther : struct =>
        new(triggers.Lists.World, triggers.Lists.Within);
}

/// <summary>The pairs the last step found, in trigger then entity order, and the world they are checked against.</summary>
internal sealed class TriggerLists(World world)
{
    public World World { get; } = world;

    public List<TriggerEvent> Entered { get; } = [];

    public List<TriggerEvent> Exited { get; } = [];

    /// <summary>Everyone in every trigger, as of the last step.</summary>
    public List<TriggerEvent> Within { get; } = [];
}

/// <summary>
/// The trigger events whose area has a <typeparamref name="TTrigger"/> and whose visitor has a
/// <typeparamref name="TOther"/>, in trigger then entity order. Walk it with <c>foreach</c>. It checks the components
/// as it goes, so an area destroyed in the loop drops out of the rest of it.
/// </summary>
public readonly struct TriggerPairs<TTrigger, TOther>
    where TTrigger : struct
    where TOther : struct
{
    private readonly World _world;
    private readonly List<TriggerEvent> _events;

    internal TriggerPairs(World world, List<TriggerEvent> events)
    {
        _world = world;
        _events = events;
    }

    /// <summary>Whether any event matches.</summary>
    public bool Any()
    {
        foreach (var _ in this)
        {
            return true;
        }

        return false;
    }

    public Enumerator GetEnumerator() => new(_world, _events);

    public struct Enumerator
    {
        private readonly World _world;
        private readonly List<TriggerEvent> _events;
        private int _index;

        internal Enumerator(World world, List<TriggerEvent> events)
        {
            _world = world;
            _events = events;
            _index = -1;
        }

        public readonly TriggerEvent Current => _events[_index];

        public bool MoveNext()
        {
            while (++_index < _events.Count)
            {
                var pair = _events[_index];
                if (Has<TTrigger>(pair.Trigger) && Has<TOther>(pair.Other))
                {
                    return true;
                }
            }

            return false;
        }

        private readonly bool Has<T>(Entity entity)
            where T : struct =>
            typeof(T) == typeof(Anyone) || (_world.IsAlive(entity) && _world.Has<T>(entity));
    }
}
