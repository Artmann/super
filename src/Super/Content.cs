using System.Numerics;
using Rendition;
using StbImageSharp;
using Super.Ui;

namespace Super;

/// <summary>A picture made by code, as RGBA bytes row by row. It must be square.</summary>
public sealed record GeneratedTexture(int Size, byte[] Pixels);

/// <summary>A surface made by code rather than loaded from a file, such as generated ground.</summary>
public sealed record GeneratedMesh(Vertex[] Vertices, uint[] Indices, GeneratedTexture Texture, float Roughness);

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
    private readonly Dictionary<string, ModelRef> _models = [];
    private readonly Dictionary<string, GameImage> _images = [];
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
    /// Names an image, by its path inside the content folder. Naming it again gives the
    /// same one. A file that's missing or isn't an image stops here with a
    /// <see cref="ContentException"/>. It's a catalog's mistake, not a drawing one.
    /// </summary>
    public GameImage Image(string path)
    {
        if (_images.TryGetValue(path, out var known))
        {
            return known;
        }

        byte[] file = ReadImage(path);
        using var stream = new MemoryStream(file);
        if (ImageInfo.FromStream(stream) is not { } info)
        {
            throw new ContentException([$"{path} is not an image this can read"]);
        }

        // The item's place is its index, as for models, because index 0 is the built-in cube.
        var image = new GameImage(path, info.Width, info.Height, file, _items.Count + 1);
        _items.Add(image);
        _images.Add(path, image);
        return image;
    }

    /// <summary>Hands over a surface made by code.</summary>
    public ModelRef Mesh(GeneratedMesh mesh) => Add(mesh);

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
    internal void ItemLoaded()
    {
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

    private byte[] ReadImage(string path)
    {
        try
        {
            return _files.Read(path);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            throw new ContentException([$"Could not read the image {path}: {problem.Message}"]);
        }
    }

    private ModelRef Add(object item)
    {
        _items.Add(item);

        // Index 0 is the built-in cube.
        return new ModelRef(_items.Count);
    }
}
