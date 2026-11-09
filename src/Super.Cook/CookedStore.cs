using System.Collections.Concurrent;

namespace Super.Cook;

/// <summary>
/// Where cooked objects and the manifest are kept. The pipeline cooks in parallel, so <see cref="Has"/>, <see cref="Put"/>
/// and <see cref="Get"/> are called from several threads at once, though never for the same hash at the same time.
/// <see cref="Commit"/> is called alone.
/// </summary>
public interface ICookedStore
{
    bool Has(string hash);

    void Put(string hash, byte[] bytes);

    byte[] Get(string hash);

    /// <summary>
    /// Writes the manifest last, then removes objects that are no longer needed. <see cref="FolderStore"/>
    /// keeps the objects the replaced manifest named for one more commit, for a game still loading from it.
    /// </summary>
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

    /// <summary>
    /// Writes the manifest, then removes the objects named by neither it nor the manifest it replaced.
    /// A running game may have read the old manifest and still be loading from it, so an object leaves
    /// one commit after its last name does. An object that can't be deleted now is left for the next commit.
    /// </summary>
    public void Commit(ContentManifest manifest)
    {
        string manifestPath = Path.Combine(_folder, ContentManifest.FileName);
        var previous = Previous(manifestPath);
        WriteAtomically(manifestPath, manifest.Serialize());

        string objects = Path.Combine(_folder, "objects");
        if (previous is null || !Directory.Exists(objects))
        {
            return;
        }

        var keep = manifest.Assets.Values.Concat(previous.Assets.Values)
            .Select(entry => ObjectFile(entry.Hash))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories).ToList())
        {
            if (!keep.Contains(file))
            {
                TryDelete(() => File.Delete(file));
            }
        }

        foreach (string sub in Directory.EnumerateDirectories(objects).ToList())
        {
            if (!Directory.EnumerateFileSystemEntries(sub).Any())
            {
                TryDelete(() => Directory.Delete(sub));
            }
        }
    }

    /// <summary>
    /// The manifest about to be replaced: empty when there is none or it isn't a manifest, and null when
    /// the file couldn't be read, so nothing is pruned this time.
    /// </summary>
    private static ContentManifest? Previous(string manifestPath)
    {
        try
        {
            return File.Exists(manifestPath) ? ContentManifest.Parse(File.ReadAllBytes(manifestPath), manifestPath) : ContentManifest.Empty;
        }
        catch (ContentException)
        {
            // No game could have loaded from a manifest that doesn't parse, so nothing it names needs keeping.
            return ContentManifest.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Open in another program, or filled again meanwhile. The manifest is already written, so the
            // commit stands; the next commit tries again.
        }
    }

    private string ObjectFile(string hash)
    {
        return Path.Combine(_folder, ContentManifest.ObjectPath(hash).Replace('/', Path.DirectorySeparatorChar));
    }

    /// <exception cref="FileInUseException">Another program, such as the game reading the manifest, has the file open.</exception>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, bytes);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            File.Delete(temp);

            // Windows refuses to replace a file another program has open with access denied, not a
            // sharing violation, so here, and only here, that counts as the file being in use.
            if (exception is UnauthorizedAccessException)
            {
                throw new FileInUseException($"{path} is open in another program, such as the game reading it, so it couldn't be replaced.", exception);
            }

            throw;
        }
    }
}

/// <summary>
/// A file couldn't be replaced because another program has it open. It carries the HResult of a
/// sharing violation, so <see cref="CookPipeline.InUse"/> knows it and the cook can be tried again.
/// </summary>
public sealed class FileInUseException : IOException
{
    private const int SharingViolation = unchecked((int)0x80070020);

    public FileInUseException(string message, Exception inner)
        : base(message, inner)
    {
        HResult = SharingViolation;
    }
}

/// <summary>A store that lives in memory, for tests and tools that don't want files.</summary>
public sealed class MemoryStore : ICookedStore
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

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
            _objects.TryRemove(hash, out _);
        }
    }
}
