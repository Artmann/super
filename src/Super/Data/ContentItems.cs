namespace Super;

/// <summary>
/// Everything a game named, by the index its ref carries, with the names it was asked for by and what was let go.
/// Index 0 is the built-in cube, and the other built-in shapes are below zero, so the first item is 1. An unloaded
/// model, texture or interface image gives its slot to a later one; <see cref="Slots"/> keeps track of that.
/// </summary>
internal sealed class ContentItems
{
    private readonly List<object> _items = [];

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

    /// <summary>Which slots were let go, which the window still has to free, and which can be taken again.</summary>
    public ItemSlots Slots { get; } = new();

    /// <summary>Adds an item in a slot of its own for good, and gives the ref of its index.</summary>
    public ModelRef Add(object item) => Place(item, reusable: false);

    /// <summary>
    /// Adds a model, texture or interface image, in a slot an unloaded one gave back if there is one, and gives its
    /// ref, which carries the slot's generation. <see cref="ItemSlots.Next"/> tells an item that holds its own
    /// index which one it will get.
    /// </summary>
    public ModelRef AddReusable(object item) => Place(item, reusable: true);

    /// <summary>
    /// Forgets an item's data and tells the window to free what it made of it. One the window hasn't seen yet has
    /// nothing to free, so it counts as loaded and its slot is given back at once.
    /// </summary>
    public void MarkUnloaded(int index)
    {
        Identities.Forget(index);
        _items[index - 1] = UnloadedItem.Instance;
        if (Slots.Unload(index))
        {
            Loading.ItemLoaded(index);
        }
    }

    private ModelRef Place(object item, bool reusable)
    {
        if (reusable && Slots.TryRetake(out int slot, out int generation))
        {
            _items[slot - 1] = item;
            Loading.Added(slot);
            return new ModelRef(slot) { Generation = generation };
        }

        _items.Add(item);
        int index = Slots.Append(reusable);
        Loading.Added(index);
        return new ModelRef(index);
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
