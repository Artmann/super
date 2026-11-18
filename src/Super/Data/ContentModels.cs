using System.Runtime.InteropServices;
using Rendition;

namespace Super;

/// <summary>
/// What a game can ask about a model it named: its meshes by name, whether it's ready to draw, and letting it go.
/// Reached through the content's <c>Models</c>.
/// </summary>
public sealed class ContentModels
{
    private readonly ContentItems _items;
    private readonly Dictionary<int, string[]> _meshNames = [];

    internal ContentModels(ContentItems items) => _items = items;

    /// <summary>
    /// Picks the meshes of a cooked model that have these names, for a draw to show only those. A
    /// mesh is named by the node that holds it in the source file, the name an artist sees in the
    /// outliner, such as <c>Head_05b</c>. A mesh with several materials is cooked as several pieces
    /// under one name, and a name picks all of them. Names must match exactly. They're read from the
    /// front of the cooked model, so the geometry is never read. A name the model has no mesh called,
    /// an empty list, a model that isn't cooked, or a static model cooked without <c>keepMeshes</c>
    /// stops here with an <see cref="ArgumentException"/> or an <see cref="InvalidOperationException"/>.
    /// Not the same as <c>file#part</c> in a model's name, which is a split made at cook time into
    /// separate models.
    /// </summary>
    public MeshMask Meshes(ModelRef model, params ReadOnlySpan<string> names)
    {
        if (names.IsEmpty)
        {
            throw new ArgumentException("A mesh mask needs at least one mesh name.", nameof(names));
        }

        string[] all = MeshNamesOf(model, nameof(model));
        var kept = new List<int>();
        foreach (string name in names)
        {
            int before = kept.Count;
            for (int part = 0; part < all.Length; part++)
            {
                if (all[part] == name)
                {
                    kept.Add(part);
                }
            }

            if (kept.Count == before)
            {
                throw new ArgumentException(MissingMesh(ModelName(model.Index), name, all), nameof(names));
            }
        }

        return new MeshMask(model.Index, all.Length, CollectionsMarshal.AsSpan(kept));
    }

    /// <summary>
    /// The mesh names of a cooked model, once each, in the order they were cooked, so a game can
    /// see what <see cref="Meshes"/> takes.
    /// </summary>
    public IReadOnlyList<string> MeshNames(ModelRef model) => MeshNamesOf(model, nameof(model)).Distinct().ToArray();

    /// <summary>
    /// Whether this one model is ready to draw. Always true without a window, and false
    /// once it's unloaded.
    /// </summary>
    public bool IsReady(ModelRef model)
    {
        if (model.IsBuiltIn)
        {
            return true;
        }

        ItemChecks.RequireNamed(_items, model.Index, nameof(model));
        return !_items.IsUnloaded(model.Index) && _items.Loading.IsReady(model.Index);
    }

    /// <summary>
    /// Lets go of a mesh or model. The window frees it, and drawing it afterwards fails.
    /// Naming the same file again later makes a new one.
    /// </summary>
    public void Unload(ModelRef model)
    {
        if (model.IsBuiltIn)
        {
            throw new ArgumentException("The built-in shapes, such as the cube, can't be unloaded.", nameof(model));
        }

        ItemChecks.RequireNamed(_items, model.Index, nameof(model));
        ItemChecks.RequireLoaded(_items, model.Index, nameof(model));
        object item = _items.Items[model.Index - 1];
        if (item is not (string or GeneratedMesh))
        {
            throw new ArgumentException($"{nameof(model)} names {model.Index}, which is not a model.", nameof(model));
        }

        _items.MarkUnloaded(model.Index);
        _meshNames.Remove(model.Index);
        if (item is string name)
        {
            _items.ForgetName<ModelRef>(name);
        }
    }

    /// <summary>Keeps the mesh names read with a model's rig, so <see cref="Meshes"/> needn't open the file again.</summary>
    internal void RememberMeshNames(int index, string[] names) => _meshNames.TryAdd(index, names);

    // One name for each piece the model is drawn in, so a name repeats for a mesh with several materials.
    private string[] MeshNamesOf(ModelRef model, string field)
    {
        if (model.IsBuiltIn)
        {
            throw new ArgumentException("The built-in shapes, such as the cube, are not cooked, so they have no mesh names.", field);
        }

        if (model.Index < 1 || model.Index > _items.Items.Count)
        {
            throw new ArgumentException($"{field} names {model.Index}, which this content never named, so it has no mesh names.", field);
        }

        ItemChecks.RequireLoaded(_items, model.Index, field);
        if (_meshNames.TryGetValue(model.Index, out var known))
        {
            return known;
        }

        if (_items.Items[model.Index - 1] is not string name)
        {
            throw new ArgumentException($"{field} names {model.Index}, which was made in code, not cooked, so it has no mesh names.", field);
        }

        string[] names = ReadMeshNames(name);
        _meshNames.Add(model.Index, names);
        return names;
    }

    private string[] ReadMeshNames(string name)
    {
        string[] names;
        using (var stream = _items.Files.Open(name))
        {
            names = ModelData.ReadMeshNames(stream, name);
        }

        if (names.Length == 0)
        {
            throw new InvalidOperationException(
                $"{name} has no mesh names, because its file was cooked without them. A rigged model always has them. " +
                $"A static model needs \"keepMeshes\": true in {name}.cook.json, and then to be cooked again.");
        }

        return names;
    }

    private string ModelName(int index) => _items.Items[index - 1] as string ?? $"model {index}";

    // The first 20 names are listed, so a model with hundreds of meshes still gives a short message.
    private static string MissingMesh(string model, string name, string[] all)
    {
        const int Shown = 20;
        string[] distinct = all.Distinct().ToArray();
        string list = string.Join(", ", distinct.Take(Shown));
        string more = distinct.Length > Shown ? $", and {distinct.Length - Shown} more" : "";
        return $"{model} has no mesh called {name}. It has {distinct.Length} {(distinct.Length == 1 ? "mesh" : "meshes")}: {list}{more}.";
    }
}
