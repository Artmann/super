using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Super;

/// <summary>What <see cref="Content"/> needs from a catalog of any type.</summary>
internal interface ICatalog
{
    /// <summary>Reads and checks the files, keeping them until <see cref="Build"/> or <see cref="Drop"/>.</summary>
    void Check(ContentReader reader, List<string> problems);

    /// <summary>Turns files that passed into records. They aren't shown to the game yet.</summary>
    void Build(ContentReader reader, List<string> problems);

    /// <summary>Shows the built records to the game.</summary>
    void Commit();

    /// <summary>Forgets a read that won't be used.</summary>
    void Drop();

    /// <summary>Tells the game the records changed.</summary>
    void Announce();
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
    private IReadOnlyList<CheckedFile>? _checked;
    private List<T>? _builtRecords;
    private Dictionary<string, T>? _builtById;

    internal Catalog(string folder)
    {
        _folder = folder;
    }

    public int Count => _records.Count;

    /// <summary>The record with this id. A missing id is a bug in the game, so it throws.</summary>
    public T this[string id] => _byId.TryGetValue(id, out var record)
        ? record
        : throw new KeyNotFoundException($"There is nothing called {id} in content/{_folder}.");

    public bool Contains(string id) => _byId.ContainsKey(id);

    public bool TryGet(string id, [MaybeNullWhen(false)] out T record) => _byId.TryGetValue(id, out record);

    /// <summary>Runs after a live reload, for a game that copied something out of its records.</summary>
    public void OnChange(Action changed) => _listeners.Add(changed);

    /// <summary>Goes through the records in file path order, without allocating.</summary>
    public List<T>.Enumerator GetEnumerator() => _records.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void ICatalog.Check(ContentReader reader, List<string> problems) =>
        _checked = reader.Check(_folder, typeof(T), problems);

    void ICatalog.Build(ContentReader reader, List<string> problems)
    {
        var records = new List<T>(_checked!.Count);
        var byId = new Dictionary<string, T>(_checked.Count);
        foreach (var file in _checked)
        {
            try
            {
                var record = JsonSerializer.Deserialize<T>(file.Json, reader.Options)!;
                records.Add(record);
                byId.Add(file.Id, record);
            }
            catch (JsonException error)
            {
                problems.Add($"{file.Path}:{error.LineNumber + 1 ?? 1}: {ContentValidator.Plain(error.Message)}");
            }
        }

        _builtRecords = records;
        _builtById = byId;
    }

    void ICatalog.Commit()
    {
        _records = _builtRecords!;
        _byId = _builtById!;
        ((ICatalog)this).Drop();
    }

    void ICatalog.Drop()
    {
        _checked = null;
        _builtRecords = null;
        _builtById = null;
    }

    void ICatalog.Announce()
    {
        foreach (var listener in _listeners)
        {
            listener();
        }
    }
}
