using System.Runtime.ExceptionServices;

namespace Super;

/// <summary>
/// The changes to a world's shape made while a query runs: adding a component an entity lacks, removing one it has,
/// spawning, and destroying. They wait in call order and are made when the outermost query ends, so the rows a query
/// is walking never move under it. Their values wait in <see cref="Values"/>. <c>OnAdded</c> handlers wait here too,
/// so they run once the change that landed their component is complete.
/// </summary>
internal sealed class PendingChanges
{
    private readonly List<Change> _changes = [];
    private readonly HashSet<Entity> _destroying = [];
    private int _holding;
    private int _shaping; // How many adds, removes and spawns wait.
    private bool _applying;

    private enum Kind
    {
        Add,
        Remove,
        Destroy,
        Added,
        Done, // An Added entry already run, before a removal of its component.
        Spawn, // A whole spawn, made in one move when the query ends.
    }

    /// <summary>The values the waiting adds and spawns give.</summary>
    public PendingValues Values { get; } = new();

    /// <summary>How many queries are running. Above 0, changes to the world's shape have to wait.</summary>
    public int Depth { get; private set; }

    public void Begin() => Depth++;

    /// <summary>Ends a query, making every waiting change when it was the outermost one.</summary>
    public void End(World world)
    {
        Depth--;
        ApplyIfDone(world);
    }

    /// <summary>
    /// Starts one change that may land several components, such as a spawn and the components it requires. Handlers
    /// it sets off wait until the returned hold is disposed, so they see the whole change.
    /// </summary>
    public Held Hold(World world)
    {
        _holding++;
        return new Held(this, world);
    }

    public void Add<T>(Entity entity, int type, in T component)
        where T : struct
    {
        _changes.Add(new Change(Kind.Add, entity, type, Values.Keep(type, in component)));
        _shaping++;
    }

    /// <summary>
    /// Queues a spawn of an entity made in the empty archetype, which lands in the plan's archetype in one move when the
    /// query ends. Its values follow through <see cref="PendingValues.Give{T}"/>, straight after.
    /// </summary>
    public void Spawn(Entity entity, SpawnPlan plan)
    {
        _changes.Add(new Change(Kind.Spawn, entity, -1, Values.Spawn(plan)));
        _shaping++;
    }

    public void Remove(Entity entity, int type)
    {
        _changes.Add(new Change(Kind.Remove, entity, type, -1));
        _shaping++;
    }

    // Components still waiting to be given to it never land, so their handlers never run.
    public void Destroy(Entity entity)
    {
        _changes.Add(new Change(Kind.Destroy, entity, -1, -1));
        _destroying.Add(entity);
    }

    /// <summary>Runs the <c>OnAdded</c> handlers for a component that just landed, once the change is complete.</summary>
    public void Added(Entity entity, int type) => _changes.Add(new Change(Kind.Added, entity, type, -1));

    /// <summary>
    /// Whether the entity will have a component of this type once the waiting changes are made: true when the last
    /// one waiting for it adds it, false when it removes it, and null when none is waiting.
    /// </summary>
    public bool? WillHave(Entity entity, int type)
    {
        // Most writes in a query change values only, so with no add or remove waiting this costs nothing.
        if (_shaping == 0)
        {
            return null;
        }

        for (int i = _changes.Count - 1; i >= 0; i--)
        {
            var change = _changes[i];
            if ((change.Kind is Kind.Add or Kind.Remove) && change.Entity == entity && change.Type == type)
            {
                return change.Kind == Kind.Add;
            }

            // A spawn comes before every other change to its entity, so nothing earlier can say otherwise.
            if (change.Kind == Kind.Spawn && change.Entity == entity)
            {
                return Values.PlanOf(change.Index).Target.Mask.Has(type) ? true : null;
            }
        }

        return null;
    }

    private void ApplyIfDone(World world)
    {
        // A query or handler run while the changes are being made leaves its own to the pass already under way.
        if (Depth == 0 && _holding == 0 && _changes.Count > 0 && !_applying)
        {
            Apply(world);
        }
    }

    // Walked by index, since making one change can queue another, which is made in the same pass. A handler that
    // throws doesn't stop the changes after it: they're all made, then the first error is thrown.
    private void Apply(World world)
    {
        ExceptionDispatchInfo? failure = null;
        _applying = true;
        try
        {
            for (int i = 0; i < _changes.Count; i++)
            {
                var change = _changes[i];
                if (!world.IsAlive(change.Entity))
                {
                    continue;
                }

                try
                {
                    Make(world, change, i);
                }
                catch (Exception problem)
                {
                    failure ??= ExceptionDispatchInfo.Capture(problem);
                }
            }
        }
        finally
        {
            _applying = false;
            _shaping = 0;
            _changes.Clear();
            _destroying.Clear();
            Values.Clear();
        }

        failure?.Throw();
    }

    // A component added and then removed in the same query was still seen to land: its OnAdded handlers, waiting at the
    // end of the list, run before the removal.
    private void RunAddedBefore(World world, Change removal, int start)
    {
        for (int i = start; i < _changes.Count; i++)
        {
            var change = _changes[i];
            if (change.Kind == Kind.Added && change.Entity == removal.Entity && change.Type == removal.Type)
            {
                _changes[i] = change with { Kind = Kind.Done };
                Make(world, change, i);
            }
        }
    }

    // At is where the change waits in the list.
    private void Make(World world, Change change, int at)
    {
        switch (change.Kind)
        {
            case Kind.Add when !_destroying.Contains(change.Entity):
                Values.AddTo(world, change.Entity, change.Type, change.Index);
                break;
            case Kind.Spawn when !_destroying.Contains(change.Entity):
                Values.Land(world, change.Entity, change.Index);
                break;
            case Kind.Remove:
                RunAddedBefore(world, change, at + 1);
                world.Remove(change.Entity, change.Type);
                break;
            case Kind.Destroy:
                world.Destroy(change.Entity);
                break;
            case Kind.Added when world.Has(change.Entity, change.Type):
                world.Archetypes.Reactions.RunAdded(world, change.Entity, change.Type);
                break;
        }
    }

    /// <summary>A change under way; disposing it makes what waited for it.</summary>
    public readonly struct Held(PendingChanges changes, World world) : IDisposable
    {
        public void Dispose()
        {
            changes._holding--;
            changes.ApplyIfDone(world);
        }
    }

    private readonly record struct Change(Kind Kind, Entity Entity, int Type, int Index);
}
