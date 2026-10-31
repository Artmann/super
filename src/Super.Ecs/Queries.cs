namespace Super.Ecs;

// Queries allocate nothing, so they're safe to run every tick and every frame. Each row
// hands out references: changing row.First.Value changes the component in the world.
public sealed partial class World
{
    public Query<T1> Query<T1>()
        where T1 : struct =>
        new(this);

    public Query<T1, T2> Query<T1, T2>()
        where T1 : struct
        where T2 : struct =>
        new(this);

    public Query<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : struct
        where T2 : struct
        where T3 : struct =>
        new(this);

    public Query<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct =>
        new(this);
}

public readonly ref struct Row<T1>(Entity entity, ref T1 first)
{
    private readonly ref T1 _first = ref first;

    public Entity Entity { get; } = entity;

    public ref T1 First => ref _first;
}

public readonly ref struct Row<T1, T2>(Entity entity, ref T1 first, ref T2 second)
{
    private readonly ref T1 _first = ref first;
    private readonly ref T2 _second = ref second;

    public Entity Entity { get; } = entity;

    public ref T1 First => ref _first;

    public ref T2 Second => ref _second;
}

public readonly ref struct Row<T1, T2, T3>(Entity entity, ref T1 first, ref T2 second, ref T3 third)
{
    private readonly ref T1 _first = ref first;
    private readonly ref T2 _second = ref second;
    private readonly ref T3 _third = ref third;

    public Entity Entity { get; } = entity;

    public ref T1 First => ref _first;

    public ref T2 Second => ref _second;

    public ref T3 Third => ref _third;
}

public readonly ref struct Row<T1, T2, T3, T4>(Entity entity, ref T1 first, ref T2 second, ref T3 third, ref T4 fourth)
{
    private readonly ref T1 _first = ref first;
    private readonly ref T2 _second = ref second;
    private readonly ref T3 _third = ref third;
    private readonly ref T4 _fourth = ref fourth;

    public Entity Entity { get; } = entity;

    public ref T1 First => ref _first;

    public ref T2 Second => ref _second;

    public ref T3 Third => ref _third;

    public ref T4 Fourth => ref _fourth;
}

/// <summary>Walks the rows of each matching archetype in turn.</summary>
internal ref struct ArchetypeCursor(World world, ulong mask)
{
    private int _archetypeIndex = -1;
    private int _row = -1;

    public Archetype? Archetype { get; private set; }

    public readonly int Row => _row;

    /// <summary>Moves on one row. Sets <paramref name="enteredArchetype"/> when the next row is in a new archetype.</summary>
    public bool MoveNext(out bool enteredArchetype)
    {
        enteredArchetype = false;
        if (Archetype is not null && ++_row < Archetype.Count)
        {
            return true;
        }

        Archetype = world.NextArchetype(ref _archetypeIndex, mask);
        _row = 0;
        enteredArchetype = Archetype is not null;
        return enteredArchetype;
    }
}

public readonly ref struct Query<T1>(World world)
    where T1 : struct
{
    public Enumerator GetEnumerator() => new(world);

    public ref struct Enumerator
    {
        private readonly World _world;
        private readonly int _type1;
        private ArchetypeCursor _cursor;
        private T1[] _values1;

        internal Enumerator(World world)
        {
            _world = world;
            _type1 = world.TypeId<T1>();
            _cursor = new ArchetypeCursor(world, 1UL << _type1);
            _values1 = [];
            world.BeginIteration();
        }

        public readonly Row<T1> Current =>
            new(_cursor.Archetype!.Entities[_cursor.Row], ref _values1[_cursor.Row]);

        public bool MoveNext()
        {
            if (!_cursor.MoveNext(out bool entered))
            {
                return false;
            }

            if (entered)
            {
                _values1 = _cursor.Archetype!.Column<T1>(_type1).Values;
            }

            return true;
        }

        public readonly void Dispose() => _world.EndIteration();
    }
}

public readonly ref struct Query<T1, T2>(World world)
    where T1 : struct
    where T2 : struct
{
    public Enumerator GetEnumerator() => new(world);

    public ref struct Enumerator
    {
        private readonly World _world;
        private readonly int _type1;
        private readonly int _type2;
        private ArchetypeCursor _cursor;
        private T1[] _values1;
        private T2[] _values2;

        internal Enumerator(World world)
        {
            _world = world;
            _type1 = world.TypeId<T1>();
            _type2 = world.TypeId<T2>();
            _cursor = new ArchetypeCursor(world, (1UL << _type1) | (1UL << _type2));
            _values1 = [];
            _values2 = [];
            world.BeginIteration();
        }

        public readonly Row<T1, T2> Current
        {
            get
            {
                int row = _cursor.Row;
                return new(_cursor.Archetype!.Entities[row], ref _values1[row], ref _values2[row]);
            }
        }

        public bool MoveNext()
        {
            if (!_cursor.MoveNext(out bool entered))
            {
                return false;
            }

            if (entered)
            {
                var archetype = _cursor.Archetype!;
                _values1 = archetype.Column<T1>(_type1).Values;
                _values2 = archetype.Column<T2>(_type2).Values;
            }

            return true;
        }

        public readonly void Dispose() => _world.EndIteration();
    }
}

public readonly ref struct Query<T1, T2, T3>(World world)
    where T1 : struct
    where T2 : struct
    where T3 : struct
{
    public Enumerator GetEnumerator() => new(world);

    public ref struct Enumerator
    {
        private readonly World _world;
        private readonly int _type1;
        private readonly int _type2;
        private readonly int _type3;
        private ArchetypeCursor _cursor;
        private T1[] _values1;
        private T2[] _values2;
        private T3[] _values3;

        internal Enumerator(World world)
        {
            _world = world;
            _type1 = world.TypeId<T1>();
            _type2 = world.TypeId<T2>();
            _type3 = world.TypeId<T3>();
            _cursor = new ArchetypeCursor(world, (1UL << _type1) | (1UL << _type2) | (1UL << _type3));
            _values1 = [];
            _values2 = [];
            _values3 = [];
            world.BeginIteration();
        }

        public readonly Row<T1, T2, T3> Current
        {
            get
            {
                int row = _cursor.Row;
                return new(_cursor.Archetype!.Entities[row], ref _values1[row], ref _values2[row], ref _values3[row]);
            }
        }

        public bool MoveNext()
        {
            if (!_cursor.MoveNext(out bool entered))
            {
                return false;
            }

            if (entered)
            {
                var archetype = _cursor.Archetype!;
                _values1 = archetype.Column<T1>(_type1).Values;
                _values2 = archetype.Column<T2>(_type2).Values;
                _values3 = archetype.Column<T3>(_type3).Values;
            }

            return true;
        }

        public readonly void Dispose() => _world.EndIteration();
    }
}

public readonly ref struct Query<T1, T2, T3, T4>(World world)
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct
{
    public Enumerator GetEnumerator() => new(world);

    public ref struct Enumerator
    {
        private readonly World _world;
        private readonly int _type1;
        private readonly int _type2;
        private readonly int _type3;
        private readonly int _type4;
        private ArchetypeCursor _cursor;
        private T1[] _values1;
        private T2[] _values2;
        private T3[] _values3;
        private T4[] _values4;

        internal Enumerator(World world)
        {
            _world = world;
            _type1 = world.TypeId<T1>();
            _type2 = world.TypeId<T2>();
            _type3 = world.TypeId<T3>();
            _type4 = world.TypeId<T4>();
            _cursor = new ArchetypeCursor(world, (1UL << _type1) | (1UL << _type2) | (1UL << _type3) | (1UL << _type4));
            _values1 = [];
            _values2 = [];
            _values3 = [];
            _values4 = [];
            world.BeginIteration();
        }

        public readonly Row<T1, T2, T3, T4> Current
        {
            get
            {
                int row = _cursor.Row;
                return new(_cursor.Archetype!.Entities[row], ref _values1[row], ref _values2[row], ref _values3[row], ref _values4[row]);
            }
        }

        public bool MoveNext()
        {
            if (!_cursor.MoveNext(out bool entered))
            {
                return false;
            }

            if (entered)
            {
                var archetype = _cursor.Archetype!;
                _values1 = archetype.Column<T1>(_type1).Values;
                _values2 = archetype.Column<T2>(_type2).Values;
                _values3 = archetype.Column<T3>(_type3).Values;
                _values4 = archetype.Column<T4>(_type4).Values;
            }

            return true;
        }

        public readonly void Dispose() => _world.EndIteration();
    }
}
