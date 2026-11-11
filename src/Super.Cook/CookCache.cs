using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Super.Cook;

/// <summary>One cooked asset in a cache record. <see cref="Part"/> is null for the source's own asset.</summary>
internal sealed record CachedOutput(string? Part, ContentKind Kind, string Hash, long Size);

/// <summary>What a cache hit gives back: the outputs, now in the store, and the warnings the cook made.</summary>
/// <param name="Objects">The shared objects the outputs point to, with their sizes, also in the store now.</param>
internal sealed record CacheHit(
    IReadOnlyList<CachedOutput> Outputs,
    IReadOnlyList<(string Hash, long Size)> Objects,
    IReadOnlyList<(string Message, int? Line)> Warnings);

/// <summary>
/// Remembers cooked results between runs. A record is keyed by the cooker, its version, the target, the settings and the
/// source's hash, and lists every other file the cooker read with the hash it had. The record only counts while all of those
/// hashes still match. Two builds can share one cache: everything is written to a temp file and moved into place, and a record
/// that can't be read is just a miss. Every file a cook uses is noted in <see cref="Usage"/>, so <see cref="Trim"/> keeps it.
/// </summary>
internal sealed class CookCache(string folder, CacheUsage? usage = null)
{
    /// <summary>What an input that isn't there hashes to, so a record stays valid until the file appears.</summary>
    private const string Missing = "missing";

    private const string Unresolvable = "unresolvable";

    private readonly string _folder = Path.GetFullPath(folder);

    /// <summary>The files this cook used, which trimming keeps. The step cache notes its entries here too.</summary>
    public CacheUsage Usage { get; } = usage ?? new CacheUsage(DateTime.UtcNow);

    /// <summary>The cache's folder, in full.</summary>
    public string Folder => _folder;
    // Workers cook in parallel, so the hash index and object writes are guarded.
    private readonly object _hashesLock = new();

    // Locks for object writes, picked by the object's path. A fixed set, shared by every cook in the process, so the
    // record and step caches gate the same object with the same lock and the set doesn't grow with each object.
    private static readonly object[] ObjectGates = [.. Enumerable.Range(0, 64).Select(_ => new object())];
    private Dictionary<string, string>? _hashes;
    private bool _hashesChanged;

    public static string Key(string cookerId, int version, string target, JsonObject settings, string sourceHash)
    {
        string text = string.Join('\0', cookerId, version.ToString(System.Globalization.CultureInfo.InvariantCulture), target,
            Canonical(settings)!.ToJsonString(), sourceHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The hash of a source, or null when it can't be read. Unchanged files on disk aren't hashed again.</summary>
    public string? SourceHash(SourceFile file)
    {
        string? hash = HashFile(file.Root, file.Name);
        return hash is Missing ? null : hash;
    }

    /// <summary>Looks for a valid record. Anything wrong with the record or its objects is a miss.</summary>
    /// <param name="miss">Why there was no hit, such as a changed input, for <c>super-cook --verbose</c>. Null on a hit.</param>
    public CacheHit? TryRestore(string key, SourceFile file, StoreWriter store, out string? miss)
    {
        string recordFile = RecordFile(key);
        try
        {
            var recordInfo = new FileInfo(recordFile);
            if (!recordInfo.Exists)
            {
                miss = "no record: new source, or its bytes, settings or cooker version changed";
                return null;
            }

            // The files a hit uses, with their last-write times, noted only once it's a hit.
            var files = new List<FileInfo> { recordInfo };
            if (JsonNode.Parse(File.ReadAllBytes(recordFile)) is not JsonObject record)
            {
                miss = "record damaged";
                return null;
            }

            foreach (var item in record["inputs"]!.AsArray())
            {
                string name = item!["name"]!.GetValue<string>();
                string? resolved = CookInput.ResolveName(file.Name, name);
                string? actual = resolved is null ? Unresolvable : HashFile(file.Root, resolved);
                if (actual is null || actual != item["hash"]!.GetValue<string>())
                {
                    miss = $"input changed: {name}";
                    return null;
                }
            }

            var outputs = new List<CachedOutput>();
            foreach (var item in record["outputs"]!.AsArray())
            {
                string hash = item!["hash"]!.GetValue<string>();
                var info = new FileInfo(ObjectFile(hash));
                if (!info.Exists)
                {
                    miss = $"cached object gone: {hash}";
                    return null;
                }

                if (!Enum.TryParse(item["kind"]!.GetValue<string>(), out ContentKind kind) || !Enum.IsDefined(kind))
                {
                    miss = "record damaged";
                    return null;
                }

                outputs.Add(new CachedOutput(item["part"]?.GetValue<string>(), kind, hash, info.Length));
                files.Add(info);
            }

            var objects = new List<(string Hash, long Size)>();
            foreach (var item in record["objects"]?.AsArray() ?? [])
            {
                string hash = item!.GetValue<string>();
                if (!ContentManifest.IsHash(hash))
                {
                    miss = "record damaged";
                    return null;
                }

                var info = new FileInfo(ObjectFile(hash));
                if (!info.Exists)
                {
                    miss = $"cached object gone: {hash}";
                    return null;
                }

                objects.Add((hash, info.Length));
                files.Add(info);
            }

            var warnings = new List<(string, int?)>();
            foreach (var item in record["warnings"]!.AsArray())
            {
                warnings.Add((item!["message"]!.GetValue<string>(), item["line"]?.GetValue<int>()));
            }

            foreach (string hash in outputs.Select(output => output.Hash).Concat(objects.Select(shared => shared.Hash)))
            {
                // The object is checked against its name before it reaches the store, so a damaged cache can't ship.
                // Once checked, the store links to the cache's copy when it can, so the bytes aren't kept twice.
                bool restored = store.PutOnce(hash, () =>
                {
                    byte[] bytes = File.ReadAllBytes(ObjectFile(hash));
                    return Convert.ToHexStringLower(SHA256.HashData(bytes)) == hash ? bytes : null;
                }, ObjectFile(hash));
                if (!restored)
                {
                    miss = $"cached object damaged: {hash}";
                    return null;
                }
            }

            foreach (var info in files)
            {
                Usage.Used(info.FullName, info.LastWriteTimeUtc);
            }

            miss = null;
            return new CacheHit(outputs, objects, warnings);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            miss = $"record unreadable: {exception.Message}";
            return null;
        }
    }

    /// <summary>Keeps a cook's result. Failing to write the cache is never a failure of the build.</summary>
    /// <param name="hashes">The hash of each asset's bytes, in the same order.</param>
    /// <param name="objects">The shared objects the assets point to.</param>
    public void Save(string key, SourceFile file, IReadOnlyList<string> inputs,
        IReadOnlyList<(string? Part, ContentKind Kind, byte[] Bytes)> assets, IReadOnlyList<string> hashes,
        IReadOnlyList<(string Hash, byte[] Bytes)> objects,
        IReadOnlyList<(string Message, int? Line)> warnings)
    {
        try
        {
            // Sorted, since parallel steps read their inputs in whatever order they run, and the same cook should make the same record.
            var inputNodes = new JsonArray();
            foreach (string name in inputs.Order(StringComparer.Ordinal))
            {
                string? resolved = CookInput.ResolveName(file.Name, name);
                string? hash = resolved is null ? Unresolvable : HashFile(file.Root, resolved);
                if (hash is null)
                {
                    return;
                }

                inputNodes.Add(new JsonObject { ["name"] = name, ["hash"] = hash });
            }

            var outputNodes = new JsonArray();
            for (int i = 0; i < assets.Count; i++)
            {
                var (part, kind, bytes) = assets[i];
                SaveObject(hashes[i], bytes);
                outputNodes.Add(new JsonObject { ["part"] = part, ["kind"] = kind.ToString(), ["hash"] = hashes[i] });
            }

            var objectNodes = new JsonArray();
            foreach (var (hash, bytes) in objects)
            {
                SaveObject(hash, bytes);
                objectNodes.Add(hash);
            }

            var warningNodes = new JsonArray();
            foreach (var (message, line) in warnings)
            {
                warningNodes.Add(new JsonObject { ["message"] = message, ["line"] = line });
            }

            var record = new JsonObject { ["inputs"] = inputNodes, ["outputs"] = outputNodes, ["objects"] = objectNodes, ["warnings"] = warningNodes };
            WriteAtomically(RecordFile(key), Encoding.UTF8.GetBytes(record.ToJsonString()));
            Usage.Wrote(RecordFile(key));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation. A cook that couldn't be remembered is still a cook.
        }
    }

    /// <summary>The cache's file for an object, or null when the cache doesn't keep it, such as when saving it failed.</summary>
    public string? SavedObject(string hash)
    {
        string objectFile = ObjectFile(hash);
        return File.Exists(objectFile) ? objectFile : null;
    }

    /// <summary>Keeps an object unless the cache has it already, as it often does for a texture models share.</summary>
    private void SaveObject(string hash, byte[] bytes)
    {
        SaveObject(_folder, hash, bytes, Usage);
    }

    /// <summary>
    /// Keeps an object in the cache in <paramref name="folder"/> unless it has it already, and notes it in
    /// <paramref name="usage"/>. The record cache and the step cache both save objects from parallel workers, so one
    /// object is written by one of them at a time: the others then find it there and only note it.
    /// </summary>
    /// <exception cref="IOException">It couldn't be written.</exception>
    internal static void SaveObject(string folder, string hash, byte[] bytes, CacheUsage usage)
    {
        var info = new FileInfo(ObjectFile(folder, hash));
        lock (ObjectGates[(int)((uint)CacheFolder.PathComparer.GetHashCode(info.FullName) % (uint)ObjectGates.Length)])
        {
            if (info.Exists)
            {
                usage.Used(info.FullName, info.LastWriteTimeUtc);
                return;
            }

            WriteAtomically(info.FullName, bytes);
            usage.Wrote(info.FullName);
        }
    }

    /// <summary>
    /// The bytes of the object <paramref name="hash"/> in the cache in <paramref name="folder"/>, checked against its hash,
    /// and notes it in <paramref name="usage"/>. Null when it isn't there, can't be read or is damaged.
    /// </summary>
    internal static byte[]? ReadObject(string folder, string hash, CacheUsage usage)
    {
        if (!ContentManifest.IsHash(hash))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(ObjectFile(folder, hash));
            if (!info.Exists)
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(info.FullName);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash)
            {
                return null;
            }

            usage.Used(info.FullName, info.LastWriteTimeUtc);
            return bytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes down the file hashes learned this run.</summary>
    public void Flush()
    {
        if (_hashes is null || !_hashesChanged)
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

            WriteAtomically(Path.Combine(_folder, "hashes.json"), Encoding.UTF8.GetBytes(node.ToJsonString()));
            _hashesChanged = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // See Save.
        }
    }

    /// <summary>
    /// Trims the cache to <paramref name="limit"/> bytes, least recently used first, keeping everything this cook used.
    /// See <see cref="CacheFolder.Trim"/>.
    /// </summary>
    public CacheTrim Trim(long limit)
    {
        return CacheFolder.Trim(_folder, limit, Usage, DateTime.UtcNow);
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

    /// <summary>The hash of a file, the marker for a file that isn't there, or null when it can't be read.</summary>
    private string? HashFile(ISourceRoot root, string name)
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

            string key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            lock (_hashesLock)
            {
                if (Hashes().TryGetValue(key, out string? known))
                {
                    return known;
                }
            }

            // Hashed outside the lock, so workers hash their files at the same time.
            string hash = Convert.ToHexStringLower(SHA256.HashData(root.Read(name)));
            lock (_hashesLock)
            {
                Hashes()[key] = hash;
                _hashesChanged = true;
            }

            return hash;
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

    private Dictionary<string, string> Hashes()
    {
        if (_hashes is not null)
        {
            return _hashes;
        }

        _hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(Path.Combine(_folder, "hashes.json"))) is JsonObject node)
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

    private string RecordFile(string key)
    {
        return Path.Combine(_folder, "records", key + ".json");
    }

    private string ObjectFile(string hash)
    {
        return ObjectFile(_folder, hash);
    }

    private static string ObjectFile(string folder, string hash)
    {
        return Path.Combine(folder, "objects", hash[..2], hash);
    }

    internal static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>The same settings with object keys in ordinal order, so the same settings always make the same text.</summary>
    private static JsonNode? Canonical(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var (key, value) in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    sorted[key] = Canonical(value);
                }

                return sorted;
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(Canonical(item));
                }

                return items;
            default:
                return node?.DeepClone();
        }
    }
}
