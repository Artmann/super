namespace Super.Cook;

/// <summary>Where cooked objects and the manifest are kept.</summary>
public interface ICookedStore
{
    bool Has(string hash);

    void Put(string hash, byte[] bytes);

    byte[] Get(string hash);

    /// <summary>Writes the manifest last, then removes the objects it doesn't name.</summary>
    void Commit(ContentManifest manifest);
}

/// <summary>A content folder on disk: <c>content.manifest</c> and <c>objects/xx/hash</c>.</summary>
public sealed class FolderStore(string folder) : ICookedStore
{
    private readonly string _folder = Path.GetFullPath(folder);

    public bool Has(string hash)
    {
        return File.Exists(ObjectFile(hash));
    }

    public void Put(string hash, byte[] bytes)
    {
        WriteAtomically(ObjectFile(hash), bytes);
    }

    public byte[] Get(string hash)
    {
        return File.ReadAllBytes(ObjectFile(hash));
    }

    public void Commit(ContentManifest manifest)
    {
        WriteAtomically(Path.Combine(_folder, ContentManifest.FileName), manifest.Serialize());

        string objects = Path.Combine(_folder, "objects");
        if (!Directory.Exists(objects))
        {
            return;
        }

        var keep = manifest.Assets.Values.Select(entry => ObjectFile(entry.Hash)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories).ToList())
        {
            if (!keep.Contains(file))
            {
                File.Delete(file);
            }
        }

        foreach (string sub in Directory.EnumerateDirectories(objects).ToList())
        {
            if (!Directory.EnumerateFileSystemEntries(sub).Any())
            {
                Directory.Delete(sub);
            }
        }
    }

    private string ObjectFile(string hash)
    {
        return Path.Combine(_folder, ContentManifest.ObjectPath(hash).Replace('/', Path.DirectorySeparatorChar));
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>A store that lives in memory, for tests and tools that don't want files.</summary>
public sealed class MemoryStore : ICookedStore
{
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    /// <summary>The last committed manifest, or <see cref="ContentManifest.Empty"/> before the first commit.</summary>
    public ContentManifest Manifest { get; private set; } = ContentManifest.Empty;

    public bool Has(string hash)
    {
        return _objects.ContainsKey(hash);
    }

    public void Put(string hash, byte[] bytes)
    {
        _objects[hash] = bytes;
    }

    public byte[] Get(string hash)
    {
        return _objects.TryGetValue(hash, out var bytes) ? bytes : throw new FileNotFoundException($"The object {hash} isn't in the store.", hash);
    }

    public void Commit(ContentManifest manifest)
    {
        Manifest = manifest;
        var keep = manifest.Assets.Values.Select(entry => entry.Hash).ToHashSet(StringComparer.Ordinal);
        foreach (string hash in _objects.Keys.Where(hash => !keep.Contains(hash)).ToList())
        {
            _objects.Remove(hash);
        }
    }
}
