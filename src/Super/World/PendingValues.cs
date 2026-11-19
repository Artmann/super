namespace Super;

/// <summary>
/// The values waiting in <see cref="PendingChanges"/>: one typed list per component type, so nothing boxes, and the
/// spawns waiting for a query to end, each with its plan and the values it was given.
/// </summary>
internal sealed class PendingValues
{
    private readonly Values?[] _lists = new Values?[ComponentMask.MaxTypes];

    // Each waiting spawn's plan, and where its given values start in _given: the type and index of each, in call order.
    private readonly List<QueuedSpawn> _spawns = [];
    private readonly List<(int Type, int Index)> _given = [];

    /// <summary>Keeps a value of this type, and returns where it's kept.</summary>
    public int Keep<T>(int type, in T value)
        where T : struct
    {
        var values = (Values<T>)(_lists[type] ??= new Values<T>());
        int index = values.Count;
        values.Add(in value);
        return index;
    }

    /// <summary>Gives the kept value to the entity through <see cref="World.Add{T}"/>.</summary>
    public void AddTo(World world, Entity entity, int type, int index) => _lists[type]!.AddTo(world, entity, index);

    /// <summary>Keeps a waiting spawn, and returns its number. Its values follow through <see cref="Give{T}"/>, straight after.</summary>
    public int Spawn(SpawnPlan plan)
    {
        _spawns.Add(new QueuedSpawn(plan, _given.Count));
        return _spawns.Count - 1;
    }

    /// <summary>Keeps one value of the spawn kept last.</summary>
    public void Give<T>(int type, in T value)
        where T : struct =>
        _given.Add((type, Keep(type, in value)));

    public SpawnPlan PlanOf(int spawn) => _spawns[spawn].Plan;

    /// <summary>
    /// Moves the spawned entity from the empty archetype into its plan's, and writes its values: the given ones in
    /// call order, then the required ones.
    /// </summary>
    public void Land(World world, Entity entity, int spawn)
    {
        var (plan, start) = _spawns[spawn];
        int end = spawn + 1 < _spawns.Count ? _spawns[spawn + 1].Start : _given.Count;
        var target = plan.Target;
        ref var slot = ref world.Entities.Of(entity);
        world.Entities.MoveTo(ref slot, target);
        int row = slot.Row;
        for (int i = start; i < end; i++)
        {
            var (type, index) = _given[i];
            _lists[type]!.WriteTo(target, row, type, index);
        }

        foreach (var requirement in plan.Required)
        {
            requirement.WriteTo(target, row);
        }

        WorldSpawn.Landed(world, entity, target, row, plan.Landed);
    }

    public void Clear()
    {
        _spawns.Clear();
        _given.Clear();
        foreach (var values in _lists)
        {
            values?.Clear();
        }
    }

    private readonly record struct QueuedSpawn(SpawnPlan Plan, int Start);

    private abstract class Values
    {
        public abstract void AddTo(World world, Entity entity, int index);

        public abstract void WriteTo(Archetype archetype, int row, int type, int index);

        public abstract void Clear();
    }

    private sealed class Values<T> : Values
        where T : struct
    {
        private readonly List<T> _list = [];

        public int Count => _list.Count;

        public void Add(in T value) => _list.Add(value);

        public override void AddTo(World world, Entity entity, int index) => world.Add(entity, _list[index]);

        public override void WriteTo(Archetype archetype, int row, int type, int index) =>
            archetype.Column<T>(type).Values[row] = _list[index];

        public override void Clear() => _list.Clear();
    }
}
