namespace Rendition.Gpu.Vulkan;

/// <summary>
/// What each id of one kind stands for. Ids start at 1, since 0 means none, and an id is given out again once
/// what it stood for is removed, so the table stays as small as what is alive.
/// </summary>
internal sealed class IdTable<T>
{
    private readonly List<T> _items = [default!];
    private readonly List<bool> _used = [false];
    private readonly Stack<int> _free = new();

    public T this[int id]
    {
        get
        {
            Check(id);
            return _items[id];
        }
    }

    /// <summary>Everything in the table, for a device to destroy what is left when it is disposed.</summary>
    public IEnumerable<T> Items => _items.Where((_, id) => _used[id]);

    public int Add(T item)
    {
        if (_free.TryPop(out int id))
        {
            _items[id] = item;
            _used[id] = true;
            return id;
        }

        _items.Add(item);
        _used.Add(true);
        return _items.Count - 1;
    }

    public void Remove(int id)
    {
        Check(id);
        _items[id] = default!;
        _used[id] = false;
        _free.Push(id);
    }

    private void Check(int id)
    {
        if (id <= 0 || id >= _used.Count || !_used[id])
        {
            throw new KeyNotFoundException($"Id {id} is not in use.");
        }
    }
}
