using Super.Cook;

namespace Super.Testing;

/// <summary>
/// Content held in memory, so a test can state its data next to its checks. What a test adds are
/// sources, cooked with the built-in cookers the first time anything is read after a change, just as a
/// game's content is cooked before it runs.
/// </summary>
public sealed class MemoryContent : IContentFiles
{
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly object _gate = new();
    private readonly MemoryRoot _root = new();
    private readonly Dictionary<string, ManifestEntry> _models = new(StringComparer.Ordinal);
    private MemoryStore? _cooked;

    /// <summary>Adds a text source, or replaces it, by its name, such as <c>items/axe.json</c>.</summary>
    public string this[string name]
    {
        set
        {
            lock (_gate)
            {
                _root[name] = value;
                _cooked = null;
            }
        }
    }

    /// <summary>Adds a source that isn't text, such as an image or a sound.</summary>
    public MemoryContent Add(string name, byte[] bytes)
    {
        lock (_gate)
        {
            _root.Add(name, bytes);
            _cooked = null;
        }

        return this;
    }

    /// <summary>
    /// Names a cooked model with no data, so content can name it. Models need files on disk to cook, so
    /// tests add them this way. It can't be drawn.
    /// </summary>
    public void AddModel(string name)
    {
        lock (_gate)
        {
            _models[name] = new ManifestEntry(ContentKind.Model, EmptyHash, 0);
        }
    }

    /// <summary>Removes a source, or a model added with <see cref="AddModel"/>.</summary>
    public void Remove(string name)
    {
        lock (_gate)
        {
            _root.Remove(name);
            _models.Remove(name);
            _cooked = null;
        }
    }

    public ContentKind? KindOf(string name)
    {
        lock (_gate)
        {
            return Find(name)?.Kind;
        }
    }

    public IReadOnlyList<string>? List(string folder)
    {
        lock (_gate)
        {
            return ContentNames.DataUnder(Cooked().Manifest.Assets, folder);
        }
    }

    public IReadOnlyList<string> Parts(string name)
    {
        lock (_gate)
        {
            return ContentNames.PartsIn(Cooked().Manifest.Assets.Keys.Concat(_models.Keys), name);
        }
    }

    public byte[] Read(string name)
    {
        lock (_gate)
        {
            if (Find(name) is not { } entry)
            {
                throw new FileNotFoundException(ContentNames.Missing(this, name), name);
            }

            return entry.Kind == ContentKind.Model && _models.ContainsKey(name) ? [] : Cooked().Get(entry.Hash);
        }
    }

    public Stream OpenObject(string hash)
    {
        lock (_gate)
        {
            var cooked = Cooked();
            if (!cooked.Manifest.Objects.Contains(hash))
            {
                throw new FileNotFoundException($"The cooked content keeps no object {hash}. Cook it again.", hash);
            }

            return new MemoryStream(cooked.Get(hash), writable: false);
        }
    }

    private ManifestEntry? Find(string name)
    {
        if (_models.TryGetValue(name, out var model))
        {
            return model;
        }

        return Cooked().Manifest.Assets.TryGetValue(name, out var entry) ? entry : null;
    }

    private MemoryStore Cooked()
    {
        if (_cooked is { } cooked)
        {
            return cooked;
        }

        var store = new MemoryStore();
        var result = new CookPipeline(BuiltInCookers.All, new CookOptions()).Cook([_root], store);
        if (!result.Succeeded)
        {
            throw new ContentException(result.Problems.Where(problem => problem.IsError).Select(problem => problem.ToString()).ToArray());
        }

        _cooked = store;
        return store;
    }
}
