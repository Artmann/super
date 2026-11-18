using System.Runtime.InteropServices;

namespace Super;

/// <summary>
/// Everything a game named, by the index its ref carries, with the names it was asked for by and what was let go.
/// Index 0 is the built-in cube, and the other built-in shapes are below zero, so the first item is 1.
/// </summary>
internal sealed class ContentItems
{
    private readonly List<object> _items = [];
    private readonly HashSet<int> _unloaded = [];
    private readonly List<int> _pendingUnloads = [];

    // What each name was asked for as, so naming it again gives the same one: a model, a rig, a texture and so on.
    private readonly Dictionary<(Type Kind, string Name), object> _named = [];

    public ContentItems(IContentFiles files) => Files = files;

    /// <summary>Where the cooked bytes come from. The window reads models and textures through it.</summary>
    public IContentFiles Files { get; }

    /// <summary>How far the window has got with loading the items.</summary>
    public ItemProgress Loading { get; } = new();

    /// <summary>The bake identities of models, and which meshes were made without lightmap UVs.</summary>
    public ModelIdentities Identities { get; } = new();

    public IReadOnlyList<object> Items => _items;

    /// <summary>Adds an item and gives the ref of its index.</summary>
    public ModelRef Add(object item)
    {
        _items.Add(item);
        Loading.Added();
        return new ModelRef(_items.Count);
    }

    /// <summary>Whether an item was let go. Cheap, so drawing can ask every frame.</summary>
    public bool IsUnloaded(int index) => _unloaded.Count > 0 && _unloaded.Contains(index);

    /// <summary>Items let go since the window last looked. Indices into the loader, not <see cref="Items"/>.</summary>
    public ReadOnlySpan<int> PendingUnloads => CollectionsMarshal.AsSpan(_pendingUnloads);

    public void ClearPendingUnloads() => _pendingUnloads.Clear();

    /// <summary>Forgets an item's data and tells the window to free what it made of it.</summary>
    public void MarkUnloaded(int index)
    {
        Identities.Forget(index);
        _unloaded.Add(index);
        _pendingUnloads.Add(index);
        _items[index - 1] = UnloadedItem.Instance;
    }

    /// <summary>What <paramref name="name"/> was asked for as a <typeparamref name="T"/> before, if it was.</summary>
    public bool TryNamed<T>(string name, out T value)
    {
        if (_named.TryGetValue((typeof(T), name), out object? known))
        {
            value = (T)known;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Keeps what <paramref name="name"/> was asked for as, for the next time it's asked for.</summary>
    public void Remember<T>(string name, T value)
        where T : notnull => _named.Add((typeof(T), name), value);

    /// <summary>Forgets what <paramref name="name"/> was asked for as, so asking again makes a new one.</summary>
    public void ForgetName<T>(string name) => _named.Remove((typeof(T), name));

    /// <summary>Forgets every <typeparamref name="T"/> that <paramref name="match"/> picks.</summary>
    public void ForgetWhere<T>(Func<T, bool> match)
    {
        foreach (var key in _named.Where(pair => pair.Key.Kind == typeof(T) && match((T)pair.Value)).Select(pair => pair.Key).ToList())
        {
            _named.Remove(key);
        }
    }

    /// <summary>
    /// What a model is, as a stable hash for the bake ids of its static draws. Worked out the first time a static
    /// draw asks, since hashing a big mesh made in code takes a while, and kept until the model is unloaded.
    /// </summary>
    public ulong IdentityOf(ModelRef model) => Identities.Of(model, _items, Files);
}
