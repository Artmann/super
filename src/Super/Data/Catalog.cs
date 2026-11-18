using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Super;

/// <summary>What <see cref="Content"/> needs from a catalog of any type.</summary>
internal interface ICatalog
{
    /// <summary>Reads and checks the files, keeping them until <see cref="Build"/> or <see cref="Drop"/>.</summary>
    void Check(ContentReader reader, List<string> problems, List<ContentReference> references);

    /// <summary>Turns files that passed into records. They aren't shown to the game yet.</summary>
    void Build(ContentReader reader, List<string> problems);

    /// <summary>Shows the built records to the game.</summary>
    void Commit();

    /// <summary>Forgets a read that won't be used.</summary>
    void Drop();

    /// <summary>Tells the game the records changed.</summary>
    void Announce();

    /// <summary>The type of record this holds.</summary>
    Type RecordType { get; }

    /// <summary>The folder this was loaded from.</summary>
    string Folder { get; }

    /// <summary>
    /// Whether this holds an id. During a read, that means the files just read, counting files that
    /// failed their checks: what's wrong with those is reported already, so a reference to one isn't.
    /// </summary>
    bool Has(string id);
}

/// <summary>
/// Everything loaded from one folder, found by id. It's the same object for the life of
/// the game, so a live reload reaches whoever is holding it.
/// </summary>
public sealed class Catalog<T> : IReadOnlyCollection<T>, ICatalog
    where T : notnull
{
    private readonly string _folder;
    private readonly List<Action> _listeners = [];
    private List<T> _records = [];
    private Dictionary<string, T> _byId = [];
    private PendingRead? _read;

    internal Catalog(string folder)
    {
        _folder = folder;
    }

    public int Count => _records.Count;

    /// <summary>The record with this id. A missing id is a bug in the game, so it throws.</summary>
    public T this[string id] => _byId.TryGetValue(id, out var record)
        ? record
        : throw new KeyNotFoundException($"There is nothing called {id} in content/{_folder}.");

    /// <summary>The record a reference points at.</summary>
    public T this[Ref<T> reference] => this[reference.Id];

    Type ICatalog.RecordType => typeof(T);

    string ICatalog.Folder => _folder;

    public bool Contains(string id) => _byId.ContainsKey(id);

    public bool TryGet(string id, [MaybeNullWhen(false)] out T record) => _byId.TryGetValue(id, out record);

    /// <summary>Runs after a live reload, for a game that copied something out of its records.</summary>
    public void OnChange(Action changed) => _listeners.Add(changed);

    /// <summary>Goes through the records in file path order, without allocating.</summary>
    public List<T>.Enumerator GetEnumerator() => _records.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void ICatalog.Check(ContentReader reader, List<string> problems, List<ContentReference> references)
    {
        var broken = new HashSet<string>();
        var files = reader.Check(_folder, typeof(T), problems, references, broken);
        _read = new PendingRead(files, broken);
    }

    bool ICatalog.Has(string id) => _read is null
        ? _byId.ContainsKey(id)
        : _read.Broken.Contains(id) || _read.Files.Any(file => file.Id == id);

    void ICatalog.Build(ContentReader reader, List<string> problems)
    {
        var read = _read!;
        var records = new List<T>(read.Files.Count);
        var byId = new Dictionary<string, T>(read.Files.Count);
        foreach (var file in read.Files)
        {
            try
            {
                var record = JsonSerializer.Deserialize<T>(file.Json, reader.Options)!;
                records.Add(record);
                byId.Add(file.Id, record);
            }
            catch (JsonException error)
            {
                problems.Add($"{file.Path}:{error.LineNumber + 1 ?? 1}: {JsonProblemLog.Plain(error.Message)}");
            }
            catch (ArgumentException error)
            {
                // A value that checks itself, such as a curve with keys out of order.
                problems.Add($"{file.Path}: {error.Message}");
            }
        }

        read.Records = records;
        read.ById = byId;
    }

    void ICatalog.Commit()
    {
        _records = _read!.Records!;
        _byId = _read.ById!;
        ((ICatalog)this).Drop();
    }

    void ICatalog.Drop() => _read = null;

    void ICatalog.Announce()
    {
        foreach (var listener in _listeners)
        {
            listener();
        }
    }

    /// <summary>
    /// One read of the folder, from <see cref="ICatalog.Check"/> until <see cref="ICatalog.Commit"/> or
    /// <see cref="ICatalog.Drop"/>: the files that passed, the ids of those that didn't, and the records built.
    /// </summary>
    private sealed class PendingRead(IReadOnlyList<CheckedFile> files, HashSet<string> broken)
    {
        public IReadOnlyList<CheckedFile> Files { get; } = files;

        public HashSet<string> Broken { get; } = broken;

        public List<T>? Records { get; set; }

        public Dictionary<string, T>? ById { get; set; }
    }
}
