namespace Super;

/// <summary>
/// What each model is, as a stable hash for the bake ids of its static draws, and which meshes made in code were
/// made without lightmap UVs, so they can't be drawn static.
/// </summary>
internal sealed class ModelIdentities
{
    private readonly Dictionary<int, ulong> _identities = [];
    private readonly HashSet<int> _dynamicMeshes = [];

    /// <summary>Reads a surface shader's SPIR-V by name, for the bake identities of models that use it. Tests swap it.</summary>
    public Func<string, byte[]> ShaderBytes { get; set; } = ItemReads.LoadShader;

    /// <summary>The model's hash, worked out the first time it's asked for and kept until the model is let go.</summary>
    public ulong Of(ModelRef model, IReadOnlyList<object> items, IContentFiles files)
    {
        if (!_identities.TryGetValue(model.Index, out ulong identity))
        {
            identity = BakeIdentity.Model(model, items, files, ShaderBytes);
            _identities[model.Index] = identity;
        }

        return identity;
    }

    /// <summary>
    /// False only for a mesh made in code without <c>isStatic</c>, which has no lightmap UVs. Built-in shapes and
    /// cooked models always have them.
    /// </summary>
    public bool MadeStatic(ModelRef model) => !_dynamicMeshes.Contains(model.Index);

    /// <summary>Marks a mesh made in code without lightmap UVs.</summary>
    public void MarkDynamic(int index) => _dynamicMeshes.Add(index);

    /// <summary>Forgets an item that was let go.</summary>
    public void Forget(int index)
    {
        _identities.Remove(index);
        _dynamicMeshes.Remove(index);
    }

    /// <summary>Forgets every hash, since a cooked file named again may hash differently after a reload.</summary>
    public void ForgetHashes() => _identities.Clear();
}
