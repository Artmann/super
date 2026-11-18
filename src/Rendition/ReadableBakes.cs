namespace Rendition;

/// <summary>
/// A store's bakes, less those found unreadable, which count as missing until the store reloads. The renderer asks
/// <see cref="Renderer.Bakes"/> through this.
/// </summary>
internal sealed class ReadableBakes : IBakeStore
{
    // Whether each store type has a Load of its own, rather than the interface's, which gives no lightmaps.
    private readonly Dictionary<Type, bool> _givesLightmaps = [];

    /// <summary>The store asked.</summary>
    public IBakeStore Inner { get; set; } = NoBakes.Instance;

    /// <summary>The store's reload count when it was last followed.</summary>
    public int ReloadsSeen { get; set; }

    /// <summary>The files that couldn't be read, which count as missing.</summary>
    public HashSet<string> Unreadable { get; } = [];

    public bool IsFresh(string file, BakeInputs inputs) => !Unreadable.Contains(file) && Inner.IsFresh(file, inputs);

    public bool IsFreshQuietly(string file, BakeInputs inputs) => !Unreadable.Contains(file) && Inner.IsFreshQuietly(file, inputs);

    public LightBakeFile? Load(string file) => Inner.Load(file);

    int IBakeStore.Reloads => ReloadsSeen;

    /// <summary>
    /// Whether the store has its own <see cref="IBakeStore.Load"/>. Only a store that leaves it out may answer null for
    /// a fresh file and still have the area count as baked.
    /// </summary>
    public bool GivesLightmaps(IBakeStore store)
    {
        var type = store.GetType();
        if (!_givesLightmaps.TryGetValue(type, out bool gives))
        {
            var map = type.GetInterfaceMap(typeof(IBakeStore));
            int load = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(IBakeStore.Load));
            gives = map.TargetMethods[load].DeclaringType != typeof(IBakeStore);
            _givesLightmaps[type] = gives;
        }

        return gives;
    }
}
