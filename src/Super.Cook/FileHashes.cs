using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Super.Cook;

/// <summary>
/// The hashes of the files cooks read, kept in the cache's <c>hashes.json</c> by each file's path, size and last-write
/// time, so a file that hasn't changed on disk isn't hashed again. Workers hash in parallel, so the index is guarded.
/// </summary>
internal sealed class FileHashes(string folder)
{
    /// <summary>What an input that isn't there hashes to, so a record stays valid until the file appears.</summary>
    public const string Missing = "missing";

    private const string Unresolvable = "unresolvable";

    private readonly string _file = Path.Combine(folder, "hashes.json");
    private readonly object _lock = new();
    private Dictionary<string, string>? _hashes;
    private bool _changed;

    /// <summary>
    /// The hash of a file <paramref name="file"/> read, named as the cooker named it, <see cref="Missing"/> when it isn't
    /// there, a marker when the name leaves the source root, or null when it can't be read.
    /// </summary>
    public string? Input(SourceFile file, string name)
    {
        string? resolved = SourcePaths.ResolveName(file.Name, name);
        return resolved is null ? Unresolvable : Of(file.Root, resolved);
    }

    /// <summary>The hash of a file, <see cref="Missing"/> for a file that isn't there, or null when it can't be read.</summary>
    public string? Of(ISourceRoot root, string name)
    {
        try
        {
            string? path = root.FullPath(name);
            if (path is null)
            {
                return Convert.ToHexStringLower(SHA256.HashData(root.Read(name)));
            }

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return Missing;
            }

            // Hashed outside the lock, so workers hash their files at the same time.
            string key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            return Known(key) ?? Learn(key, Convert.ToHexStringLower(SHA256.HashData(root.Read(name))));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes down the file hashes learned this run, leaving out files that are gone or have changed.</summary>
    public void Flush()
    {
        if (_hashes is null || !_changed)
        {
            return;
        }

        try
        {
            var current = CurrentKeys(_hashes.Keys);
            var node = new JsonObject();
            foreach (var (key, hash) in _hashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (current.Contains(key))
                {
                    node[key] = hash;
                }
            }

            CacheObjects.WriteAtomically(_file, Encoding.UTF8.GetBytes(node.ToJsonString()));
            _changed = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation. Failing to write it is never a failure of the build.
        }
    }

    /// <summary>
    /// Which of the hash index's keys still name a file as it is on disk: its path, size and last-write time. Each folder
    /// the keys name is listed once, so an index of a few thousand files costs a few dozen listings. A file that's gone, or
    /// has changed since its key was made, drops out, so the index doesn't grow with every edit.
    /// </summary>
    internal static HashSet<string> CurrentKeys(IEnumerable<string> keys)
    {
        var current = new HashSet<string>(CacheFolder.PathComparer);
        var folders = new HashSet<string>(CacheFolder.PathComparer);
        foreach (string key in keys)
        {
            int ticks = key.LastIndexOf('|');
            int length = ticks > 0 ? key.LastIndexOf('|', ticks - 1) : -1;
            if (length <= 0 || Path.GetDirectoryName(key[..length]) is not { Length: > 0 } folder || !folders.Add(folder))
            {
                continue;
            }

            try
            {
                var listing = new FileSystemEnumerable<string>(folder,
                    (ref FileSystemEntry entry) => string.Concat(entry.ToFullPath(), "|",
                        entry.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), "|",
                        entry.LastWriteTimeUtc.UtcDateTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
                };
                current.UnionWith(listing);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The folder is gone, so its files are too.
            }
        }

        return current;
    }

    private string? Known(string key)
    {
        lock (_lock)
        {
            return Index().TryGetValue(key, out string? known) ? known : null;
        }
    }

    private string Learn(string key, string hash)
    {
        lock (_lock)
        {
            Index()[key] = hash;
            _changed = true;
        }

        return hash;
    }

    private Dictionary<string, string> Index()
    {
        if (_hashes is not null)
        {
            return _hashes;
        }

        _hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(_file)) is JsonObject node)
            {
                foreach (var (key, value) in node)
                {
                    if (value is JsonValue json && json.TryGetValue(out string? hash))
                    {
                        _hashes[key] = hash;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // No hashes yet, or they were damaged. Files get hashed again.
            _hashes.Clear();
        }

        return _hashes;
    }
}
