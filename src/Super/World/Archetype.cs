namespace Super;

/// <summary>One component type's values in an archetype, packed side by side.</summary>
internal abstract class ComponentColumn
{
    public abstract void Grow(int capacity);

    /// <summary>Fills a hole by moving the last row into it, which keeps rows packed.</summary>
    public abstract void MoveRow(int from, int to);

    public abstract void CopyRowTo(ComponentColumn target, int from, int to);

    public abstract void ClearRow(int row);

    public abstract ComponentColumn CreateEmpty(int capacity);
}

internal sealed class Column<T>(int capacity) : ComponentColumn
{
    public T[] Values = new T[capacity];

    public override void Grow(int capacity) => Array.Resize(ref Values, capacity);

    public override void MoveRow(int from, int to) => Values[to] = Values[from];

    public override void CopyRowTo(ComponentColumn target, int from, int to) => ((Column<T>)target).Values[to] = Values[from];

    public override void ClearRow(int row) => Values[row] = default!;

    public override ComponentColumn CreateEmpty(int capacity) => new Column<T>(capacity);
}

/// <summary>
/// All the entities with exactly the same set of components. Keeping them in packed
/// arrays is what makes walking thousands of them fast.
/// </summary>
internal sealed class Archetype
{
    /// <summary>The most component types a world holds: one bit each in an archetype's mask.</summary>
    internal const int MaxComponentTypes = ComponentMask.MaxTypes;

    private const int FirstCapacity = 16;

    // Sized to the archetype's highest type, so an archetype of a few early types stays small.
    private readonly ComponentColumn?[] _columns;

    public Archetype(ComponentMask mask, Archetype? source, ComponentColumn? added, int addedType)
    {
        Mask = mask;
        int highest = -1;
        for (int type = mask.Next(-1); type >= 0; type = mask.Next(type))
        {
            highest = type;
        }

        _columns = new ComponentColumn?[highest + 1];
        for (int type = mask.Next(-1); type >= 0; type = mask.Next(type))
        {
            _columns[type] = type == addedType ? added : source!._columns[type]!.CreateEmpty(FirstCapacity);
        }
    }

    public ComponentMask Mask { get; }

    public int Count { get; private set; }

    public Entity[] Entities { get; private set; } = new Entity[FirstCapacity];

    public Column<T> Column<T>(int type) => (Column<T>)_columns[type]!;

    /// <summary>The column for a component this archetype may not have. A type of -1 means it was never registered.</summary>
    public Column<T>? ColumnOrNull<T>(int type) => type >= 0 && type < _columns.Length ? _columns[type] as Column<T> : null;

    public int AddRow(Entity entity)
    {
        if (Count == Entities.Length)
        {
            int capacity = Count * 2;
            var entities = Entities;
            Array.Resize(ref entities, capacity);
            Entities = entities;
            foreach (var column in _columns)
            {
                column?.Grow(capacity);
            }
        }

        Entities[Count] = entity;
        return Count++;
    }

    /// <summary>Copies the components both archetypes share.</summary>
    public void CopyRowTo(Archetype target, int from, int to)
    {
        var shared = Mask.And(target.Mask);
        for (int type = shared.Next(-1); type >= 0; type = shared.Next(type))
        {
            _columns[type]!.CopyRowTo(target._columns[type]!, from, to);
        }
    }

    /// <summary>Removes a row. Returns the entity that moved into its place, if any.</summary>
    public Entity? RemoveRow(int row)
    {
        int last = --Count;
        Entity? moved = null;
        if (row != last)
        {
            Entities[row] = Entities[last];
            moved = Entities[row];
        }

        foreach (var column in _columns)
        {
            if (column is null)
            {
                continue;
            }

            if (row != last)
            {
                column.MoveRow(last, row);
            }

            column.ClearRow(last);
        }

        return moved;
    }
}
