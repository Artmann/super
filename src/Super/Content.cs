using System.Runtime.InteropServices;
using Rendition;

namespace Super;

/// <summary>Names a fragment shader. <see cref="Content.Shader"/> hands these out.</summary>
public readonly record struct ShaderRef(int Index);

/// <summary>Names a texture. <see cref="Content.Texture(string, TextureKind, bool)"/> hands these out.</summary>
public readonly record struct TextureRef(int Index);

/// <summary>Names a stack of same-sized textures. <see cref="Content.TextureArray"/> hands these out.</summary>
public readonly record struct TextureArrayRef(int Index);

/// <summary>A shader the game named, read from shaders/&lt;name&gt;.spv beside the program.</summary>
internal sealed record ShaderItem(string Name);

/// <summary>A texture the game named: a PNG in the content folder, or pixels made in code.</summary>
internal sealed record TextureItem(string? Path, GeneratedTexture? Generated, IContentFiles Files, TextureKind Kind, bool NormalMap);

/// <summary>A texture array the game named, from textures named before it.</summary>
internal sealed record TextureArrayItem(TextureItem[] Layers);

/// <summary>Stands where an unloaded item was, so its data can be collected while its index stays taken.</summary>
internal sealed record UnloadedItem
{
    public static UnloadedItem Instance { get; } = new();
}

/// <summary>
/// Names what a game wants to draw, and reads its JSON files. It touches no GPU, so games
/// run the same with no window. The window loads what's named here in the background, and
/// a loading screen watches <see cref="IsLoaded"/> and <see cref="Progress"/>. Headless
/// runs, screenshots, recordings and profiles report everything loaded at once, so the
/// speed of a disk can't change what a seed does.
/// </summary>
public sealed class Content
{
    private static readonly TimeSpan ReloadQuietTime = TimeSpan.FromMilliseconds(100);

    private readonly List<object> _items = [];
    private readonly List<bool> _itemLoaded = [];
    private readonly HashSet<int> _unloaded = [];
    private readonly List<int> _pendingUnloads = [];
    private readonly Dictionary<string, ModelRef> _models = [];
    private readonly Dictionary<string, ShaderRef> _shaders = [];
    private readonly Dictionary<(string Path, TextureKind Kind, bool NormalMap), TextureRef> _textures = [];
    private readonly Dictionary<(Type Type, string Folder), ICatalog> _catalogs = [];
    private readonly List<ContentReference> _waiting = [];
    private readonly ContentReader _reader;
    private readonly IContentFiles _files;
    private ContentWatcher? _watcher;
    private volatile bool _reloadWanted;
    private int _loaded;
    private int _batchStart;

    /// <summary>Reads JSON from the given files, or from the content folder if there are none.</summary>
    public Content(IContentFiles? files = null)
    {
        _files = files ?? new ContentFolder();
        _reader = new ContentReader(this, _files);
    }

    internal IReadOnlyList<object> Items => _items;

    /// <summary>True only in the interactive window, where loading happens over real time.</summary>
    internal bool Streaming { get; set; }

    /// <summary>Set by tests, to keep a loading screen up for as long as they need it.</summary>
    internal bool Held { get; set; }

    /// <summary>Whether files are being watched. This never happens without a window.</summary>
    internal bool IsLive => _watcher is not null;

    /// <summary>Set when a file changed. The next tick reads everything again.</summary>
    internal bool ReloadWanted
    {
        get => _reloadWanted;
        set => _reloadWanted = value;
    }

    /// <summary>Whether everything named so far is ready to draw.</summary>
    public bool IsLoaded => !Held && (!Streaming || _loaded >= _items.Count);

    /// <summary>
    /// How far loading has got, from 0 to 1. It counts what's been named since the last
    /// time everything was loaded, so a second loading screen starts again from 0.
    /// </summary>
    public float Progress
    {
        get
        {
            if (IsLoaded)
            {
                return 1f;
            }

            return Held ? 0f : (_loaded - _batchStart) / (float)(_items.Count - _batchStart);
        }
    }

    /// <summary>Names a cooked model, by its path inside assets/cooked. Naming it again gives the same one.</summary>
    public ModelRef Model(string path)
    {
        if (!_models.TryGetValue(path, out var model))
        {
            model = Add(path);
            _models.Add(path, model);
        }

        return model;
    }

    /// <summary>
    /// Hands over a surface made by code. Anything its surface names must have been named
    /// first, which also means it's loaded first.
    /// </summary>
    public ModelRef Mesh(GeneratedMesh mesh)
    {
        var surface = mesh.Surface;
        RequireTexture(surface.ColorTexture, nameof(surface.ColorTexture));
        RequireTexture(surface.NormalTexture, nameof(surface.NormalTexture));
        RequireTexture(surface.SurfaceTexture, nameof(surface.SurfaceTexture));
        if (surface.Shader is { } shader)
        {
            Require<ShaderItem>(shader.Index, nameof(surface.Shader), "shader");
        }

        RequireCount(surface.Parameters.Length, Surface.MaxParameters, nameof(surface.Parameters));
        RequireCount(surface.ShaderTextures.Length, Surface.MaxShaderTextures, nameof(surface.ShaderTextures));
        RequireCount(surface.ShaderArrays.Length, Surface.MaxShaderTextures, nameof(surface.ShaderArrays));
        for (int i = 0; i < surface.ShaderTextures.Length; i++)
        {
            RequireTexture(surface.ShaderTextures[i], $"{nameof(surface.ShaderTextures)}[{i}]");
        }

        for (int i = 0; i < surface.ShaderArrays.Length; i++)
        {
            Require<TextureArrayItem>(surface.ShaderArrays[i].Index, $"{nameof(surface.ShaderArrays)}[{i}]", "texture array");
        }

        return Add(mesh);
    }

    /// <summary>
    /// Names a fragment shader compiled from the game's Shaders folder, by file name
    /// without extension. Naming it again gives the same one.
    /// </summary>
    public ShaderRef Shader(string name)
    {
        if (!_shaders.TryGetValue(name, out var shader))
        {
            shader = new ShaderRef(Add(new ShaderItem(name)).Index);
            _shaders.Add(name, shader);
        }

        return shader;
    }

    /// <summary>
    /// Names a PNG by its path inside the content folder. Naming it the same way again
    /// gives the same one. A normal map keeps its mips unit length.
    /// </summary>
    public TextureRef Texture(string path, TextureKind kind = TextureKind.Color, bool normalMap = false)
    {
        if (!_textures.TryGetValue((path, kind, normalMap), out var texture))
        {
            texture = new TextureRef(Add(new TextureItem(path, null, _files, kind, normalMap)).Index);
            _textures.Add((path, kind, normalMap), texture);
        }

        return texture;
    }

    /// <summary>Names an image made in code. Each call makes a new one.</summary>
    public TextureRef Texture(GeneratedTexture texture, TextureKind kind = TextureKind.Color, bool normalMap = false) =>
        new(Add(new TextureItem(null, texture, _files, kind, normalMap)).Index);

    /// <summary>
    /// Stacks textures named before into one array that a shader indexes by layer. All
    /// must be the same size.
    /// </summary>
    public TextureArrayRef TextureArray(params ReadOnlySpan<TextureRef> layers)
    {
        if (layers.IsEmpty)
        {
            throw new ArgumentException("A texture array needs at least one layer.", nameof(layers));
        }

        var items = new TextureItem[layers.Length];
        for (int i = 0; i < layers.Length; i++)
        {
            items[i] = Require<TextureItem>(layers[i].Index, $"{nameof(layers)}[{i}]", "texture");
        }

        return new TextureArrayRef(Add(new TextureArrayItem(items)).Index);
    }

    /// <summary>
    /// Whether this one model is ready to draw. Always true without a window, and false
    /// once it's unloaded.
    /// </summary>
    public bool IsReady(ModelRef model)
    {
        if (model.Index == 0)
        {
            return true;
        }

        RequireNamed(model.Index, nameof(model));
        return !_unloaded.Contains(model.Index) && !Held && (!Streaming || _itemLoaded[model.Index - 1]);
    }

    /// <summary>
    /// Lets go of a mesh or model. The window frees it, and drawing it afterwards fails.
    /// Naming the same file again later makes a new one.
    /// </summary>
    public void Unload(ModelRef model)
    {
        if (model.Index == 0)
        {
            throw new ArgumentException("The built-in cube can't be unloaded.", nameof(model));
        }

        RequireNamed(model.Index, nameof(model));
        RequireLoaded(model.Index, nameof(model));
        object item = _items[model.Index - 1];
        if (item is not (string or GeneratedMesh))
        {
            throw new ArgumentException($"{nameof(model)} names {model.Index}, which is not a model.", nameof(model));
        }

        MarkUnloaded(model.Index);
        if (item is string path)
        {
            _models.Remove(path);
        }
    }

    /// <summary>
    /// Lets go of a texture. The window frees it, and a mesh named afterwards can't use
    /// it. A mesh that names it must be unloaded first, since the window would otherwise
    /// draw with a texture that is gone. Naming the same file again later makes a new one.
    /// </summary>
    public void Unload(TextureRef texture)
    {
        var item = Require<TextureItem>(texture.Index, nameof(texture), "texture");
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i] is GeneratedMesh mesh && Names(mesh.Surface, texture))
            {
                throw new InvalidOperationException($"Texture {texture.Index} is still named by model {i + 1}. Unload the model first.");
            }
        }

        MarkUnloaded(texture.Index);
        if (item.Path is { } path)
        {
            _textures.Remove((path, item.Kind, item.NormalMap));
        }
    }

    private static bool Names(Surface surface, TextureRef texture)
    {
        if (surface.ColorTexture == texture || surface.NormalTexture == texture || surface.SurfaceTexture == texture)
        {
            return true;
        }

        foreach (var named in surface.ShaderTextures)
        {
            if (named == texture)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether an item was let go. Cheap, so drawing can ask every frame.</summary>
    internal bool IsUnloaded(int index) => _unloaded.Count > 0 && _unloaded.Contains(index);

    /// <summary>Items let go since the window last looked. Indices into the loader, not <see cref="Items"/>.</summary>
    internal ReadOnlySpan<int> PendingUnloads => CollectionsMarshal.AsSpan(_pendingUnloads);

    internal void ClearPendingUnloads() => _pendingUnloads.Clear();

    /// <summary>
    /// Reads every JSON file under a folder of the content folder, one record per file.
    /// Anything wrong stops here with a <see cref="ContentException"/> listing every
    /// problem. Loading the same folder again hands back the same catalog.
    /// </summary>
    public Catalog<T> LoadAll<T>(string folder)
        where T : notnull
    {
        if (_catalogs.TryGetValue((typeof(T), folder), out var known))
        {
            return (Catalog<T>)known;
        }

        _reader.RequireId(typeof(T));
        var catalog = new Catalog<T>(folder);
        Load([catalog], [.. _catalogs.Values, catalog]);
        _catalogs.Add((typeof(T), folder), catalog);
        if (IsLive)
        {
            ContentSchema.Write(((ContentFolder)_files).Root!, folder, typeof(T), _reader.Options);
        }

        return catalog;
    }

    /// <summary>
    /// Fails if a reference still has no catalog to point at. Files can load in any order
    /// within one Load or one tick, so this runs when those end.
    /// </summary>
    internal void RequireReferences()
    {
        if (_waiting.Count == 0)
        {
            return;
        }

        var problems = _waiting.ConvertAll(reference =>
            $"{reference.Place}: {reference.Field} points at {reference.Id}, but no {reference.Target.Name} was loaded");
        _waiting.Clear();
        throw new ContentException(problems);
    }

    /// <summary>The schema for a type of record, as text.</summary>
    internal string SchemaText(Type type) => ContentSchema.Text(type, _reader.Options);

    /// <summary>Writes a schema for every catalog into a content folder.</summary>
    internal void WriteSchemas(string root)
    {
        foreach (var catalog in _catalogs.Values)
        {
            ContentSchema.Write(root, catalog.Folder, catalog.RecordType, _reader.Options);
        }
    }

    /// <summary>
    /// Starts development help: schemas, and reloading on save. It does nothing for a
    /// shipped game, whose content sits beside the program, or for files not on disk.
    /// </summary>
    internal void GoLive()
    {
        if (_files is ContentFolder { IsSource: true, Root: { } root } && Directory.Exists(root))
        {
            WriteSchemas(root);
            _watcher = new ContentWatcher(root, () => ReloadWanted = true, ReloadQuietTime);
        }
    }

    /// <summary>Stops watching files.</summary>
    internal void StopLive()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>
    /// Reads everything again, all or nothing. A mistake is written to the log and the old
    /// data stays, because a typo shouldn't end a play session. A file that can't be read
    /// yet is likely still being saved, so it's tried again on the next tick.
    /// </summary>
    internal bool Reload(TextWriter log)
    {
        ReloadWanted = false;
        ICatalog[] all = [.. _catalogs.Values];
        try
        {
            Load(all, all);
        }
        catch (ContentException mistake)
        {
            log.WriteLine($"Content not reloaded:{Environment.NewLine}{mistake.Message}");
            return false;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            foreach (var catalog in all)
            {
                catalog.Drop();
            }

            ReloadWanted = true;
            return false;
        }

        foreach (var catalog in all)
        {
            catalog.Announce();
        }

        log.WriteLine("Content reloaded.");
        return true;
    }

    /// <summary>Counts one more item as ready. The window calls this as it finishes each one.</summary>
    internal void ItemLoaded(int index)
    {
        _itemLoaded[index - 1] = true;
        _loaded++;
        if (_loaded == _items.Count)
        {
            _batchStart = _loaded;
        }
    }

    /// <summary>
    /// Reads catalogs all or nothing, so a mistake in one leaves every one of them as it
    /// was. References are checked against everything known, including what's being read.
    /// </summary>
    private void Load(ICatalog[] catalogs, ICatalog[] known)
    {
        var problems = new List<string>();
        var references = new List<ContentReference>();
        foreach (var catalog in catalogs)
        {
            catalog.Check(_reader, problems, references);
        }

        var waiting = new List<ContentReference>(_waiting);
        waiting.AddRange(references);
        var stillWaiting = Resolve(waiting, known, problems);

        if (problems.Count == 0)
        {
            foreach (var catalog in catalogs)
            {
                catalog.Build(_reader, problems);
            }
        }

        foreach (var catalog in catalogs)
        {
            if (problems.Count > 0)
            {
                catalog.Drop();
            }
            else
            {
                catalog.Commit();
            }
        }

        if (problems.Count > 0)
        {
            throw new ContentException(problems);
        }

        _waiting.Clear();
        _waiting.AddRange(stillWaiting);
    }

    /// <summary>Reports references that point at nothing, and hands back those with no catalog yet.</summary>
    private static List<ContentReference> Resolve(List<ContentReference> references, ICatalog[] known, List<string> problems)
    {
        var stillWaiting = new List<ContentReference>();
        foreach (var reference in references)
        {
            var targets = known.Where(catalog => catalog.RecordType == reference.Target).ToArray();
            if (targets.Length == 0)
            {
                stillWaiting.Add(reference);
            }
            else if (!targets.Any(catalog => catalog.Has(reference.Id)))
            {
                problems.Add($"{reference.Place}: {reference.Field} points at {reference.Id}, but there is no {reference.Target.Name} called that");
            }
        }

        return stillWaiting;
    }

    private ModelRef Add(object item)
    {
        _items.Add(item);
        _itemLoaded.Add(false);

        // Index 0 is the built-in cube.
        return new ModelRef(_items.Count);
    }

    /// <summary>Forgets an item's data and tells the window to free what it made of it.</summary>
    private void MarkUnloaded(int index)
    {
        _unloaded.Add(index);
        _pendingUnloads.Add(index);
        _items[index - 1] = UnloadedItem.Instance;
    }

    private void RequireLoaded(int index, string field)
    {
        if (_unloaded.Contains(index))
        {
            throw new ArgumentException($"{field} names {index}, which was unloaded.", field);
        }
    }

    private void RequireTexture(TextureRef? texture, string field)
    {
        if (texture is { } named)
        {
            Require<TextureItem>(named.Index, field, "texture");
        }
    }

    private void RequireNamed(int index, string field)
    {
        if (index < 1 || index > _items.Count)
        {
            throw new ArgumentException($"{field} names {index}, but nothing was named with that index.", field);
        }
    }

    private T Require<T>(int index, string field, string kind)
        where T : class
    {
        RequireNamed(index, field);
        RequireLoaded(index, field);
        if (_items[index - 1] is not T item)
        {
            throw new ArgumentException($"{field} names {index}, which is not a {kind}.", field);
        }

        return item;
    }

    private static void RequireCount(int count, int max, string field)
    {
        if (count > max)
        {
            throw new ArgumentException($"{field} has {count} entries, but a surface shader takes at most {max}.", field);
        }
    }
}
