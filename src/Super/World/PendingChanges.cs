namespace Super;

/// <summary>
/// The changes to a world's shape made while a query runs: adding a component an entity lacks, removing one it has,
/// and destroying it. They wait in call order and are made when the outermost query ends, so the rows a query is
/// walking never move under it. Added values are kept in one typed list per component type, so nothing boxes.
/// </summary>
internal sealed class PendingChanges
{
    private readonly List<Change> _changes = [];
    private readonly Values?[] _values = new Values?[ComponentMask.MaxTypes];
    private int _depth;
    private bool _applying;

    private enum Kind
    {
        Add,
        Remove,
        Destroy,
    }

    /// <summary>Whether a query is running, so changes to the world's shape have to wait.</summary>
    public bool IsQuerying => _depth > 0;

    public void Begin() => _depth++;

    /// <summary>Ends a query, making every waiting change when it was the outermost one.</summary>
    public void End(World world)
    {
        // A query run while the changes are being made leaves its own to the pass already under way.
        if (--_depth == 0 && _changes.Count > 0 && !_applying)
        {
            Apply(world);
        }
    }

    public void Add<T>(Entity entity, int type, in T component)
        where T : struct
    {
        var values = (Values<T>)(_values[type] ??= new Values<T>());
        _changes.Add(new Change(Kind.Add, entity, type, values.Count));
        values.Add(in component);
    }

    public void Remove(Entity entity, int type) => _changes.Add(new Change(Kind.Remove, entity, type, -1));

    public void Destroy(Entity entity) => _changes.Add(new Change(Kind.Destroy, entity, -1, -1));

    /// <summary>Whether a component of this type is waiting to be added to the entity.</summary>
    public bool IsAdding(Entity entity, int type)
    {
        foreach (var change in _changes)
        {
            if (change.Kind == Kind.Add && change.Entity == entity && change.Type == type)
            {
                return true;
            }
        }

        return false;
    }

    // Walked by index, since making one change can queue another, which is made in the same pass.
    private void Apply(World world)
    {
        _applying = true;
        try
        {
            for (int i = 0; i < _changes.Count; i++)
            {
                var change = _changes[i];
                if (world.IsAlive(change.Entity))
                {
                    Make(world, change);
                }
            }
        }
        finally
        {
            _applying = false;
            _changes.Clear();
            foreach (var values in _values)
            {
                values?.Clear();
            }
        }
    }

    private void Make(World world, Change change)
    {
        switch (change.Kind)
        {
            case Kind.Add:
                _values[change.Type]!.AddTo(world, change.Entity, change.Index);
                break;
            case Kind.Remove:
                world.Remove(change.Entity, change.Type);
                break;
            default:
                world.Destroy(change.Entity);
                break;
        }
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
