using System.Runtime.ExceptionServices;

namespace Super;

/// <summary>
/// The changes to a world's shape made while a query runs: adding a component an entity lacks, removing one it has,
/// and destroying it. They wait in call order and are made when the outermost query ends, so the rows a query is
/// walking never move under it. Added values are kept in one typed list per component type, so nothing boxes.
/// <c>OnAdded</c> handlers wait here too, so they run once the change that landed their component is complete.
/// </summary>
internal sealed class PendingChanges
{
    private readonly List<Change> _changes = [];
    private readonly Values?[] _values = new Values?[ComponentMask.MaxTypes];
    private readonly HashSet<Entity> _destroying = [];
    private int _depth;
    private int _holding;
    private int _shaping; // How many adds and removes wait.
    private bool _applying;

    private enum Kind
    {
        Add,
        Remove,
        Destroy,
        Added,
    }

    /// <summary>Whether a query is running, so changes to the world's shape have to wait.</summary>
    public bool IsQuerying => _depth > 0;

    public void Begin() => _depth++;

    /// <summary>Ends a query, making every waiting change when it was the outermost one.</summary>
    public void End(World world)
    {
        _depth--;
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

    private void Release(World world)
    {
        _holding--;
        ApplyIfDone(world);
    }

    public void Add<T>(Entity entity, int type, in T component)
        where T : struct
    {
        var values = (Values<T>)(_values[type] ??= new Values<T>());
        _changes.Add(new Change(Kind.Add, entity, type, values.Count));
        values.Add(in component);
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
        }

        return null;
    }

    private void ApplyIfDone(World world)
    {
        // A query or handler run while the changes are being made leaves its own to the pass already under way.
        if (_depth == 0 && _holding == 0 && _changes.Count > 0 && !_applying)
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
                    Make(world, change);
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
            foreach (var values in _values)
            {
                values?.Clear();
            }
        }

        failure?.Throw();
    }

    private void Make(World world, Change change)
    {
        switch (change.Kind)
        {
            case Kind.Add when !_destroying.Contains(change.Entity):
                _values[change.Type]!.AddTo(world, change.Entity, change.Index);
                break;
            case Kind.Remove:
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
        public void Dispose() => changes.Release(world);
    }

    private readonly record struct Change(Kind Kind, Entity Entity, int Type, int Index);

    private abstract class Values
    {
        public abstract void AddTo(World world, Entity entity, int index);

        public abstract void Clear();
    }

    private sealed class Values<T> : Values
        where T : struct
    {
        private readonly List<T> _list = [];

        public int Count => _list.Count;

        public void Add(in T value) => _list.Add(value);

        public override void AddTo(World world, Entity entity, int index) => world.Add(entity, _list[index]);

        public override void Clear() => _list.Clear();
    }
}
