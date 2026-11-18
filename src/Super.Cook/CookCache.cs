using System.Security.Cryptography;
using System.Text;
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
    private readonly string _folder = Path.GetFullPath(folder);

    private readonly FileHashes _hashes = new(Path.GetFullPath(folder));

    /// <summary>The files this cook used, which trimming keeps. The step cache notes its entries here too.</summary>
    public CacheUsage Usage { get; } = usage ?? new CacheUsage(DateTime.UtcNow);

    /// <summary>The cache's folder, in full.</summary>
    public string Folder => _folder;

    public static string Key(string cookerId, int version, string target, JsonObject settings, string sourceHash)
    {
        string text = string.Join('\0', cookerId, version.ToString(System.Globalization.CultureInfo.InvariantCulture), target,
            Canonical(settings)!.ToJsonString(), sourceHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The hash of a source, or null when it can't be read. Unchanged files on disk aren't hashed again.</summary>
    public string? SourceHash(SourceFile file)
    {
        string? hash = _hashes.Of(file.Root, file.Name);
        return hash is FileHashes.Missing ? null : hash;
    }

    /// <summary>Looks for a valid record. Anything wrong with the record or its objects is a miss.</summary>
    /// <param name="miss">Why there was no hit, such as a changed input, for <c>super-cook --verbose</c>. Null on a hit.</param>
    public CacheHit? TryRestore(string key, SourceFile file, StoreWriter store, out string? miss)
    {
        try
        {
            // The files a hit uses, with their last-write times, noted only once it's a hit.
            var used = new List<FileInfo>();
            if (ReadRecord(key, file, used, out miss) is not { } hit)
            {
                return null;
            }

            miss = PutInStore(hit.Outputs.Select(output => output.Hash).Concat(hit.Objects.Select(shared => shared.Hash)), store);
            if (miss is not null)
            {
                return null;
            }

            foreach (var info in used)
            {
                Usage.Used(info.FullName, info.LastWriteTimeUtc);
            }

            return hit;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            miss = $"record unreadable: {exception.Message}";
            return null;
        }
    }

    /// <summary>
    /// The record for <paramref name="key"/> while every input it names still has the hash it had, and every object it
    /// names is still in the cache. The files it reads go in <paramref name="used"/>.
    /// </summary>
    private CacheHit? ReadRecord(string key, SourceFile file, List<FileInfo> used, out string? miss)
    {
        string recordFile = RecordFile(key);
        var recordInfo = new FileInfo(recordFile);
        if (!recordInfo.Exists)
        {
            miss = "no record: new source, or its bytes, settings or cooker version changed";
            return null;
        }

        used.Add(recordInfo);
        if (JsonNode.Parse(File.ReadAllBytes(recordFile)) is not JsonObject record)
        {
            miss = "record damaged";
            return null;
        }

        miss = ChangedInput(record, file);
        if (miss is not null)
        {
            return null;
        }

        if (ReadOutputs(record, used, out miss) is not { } outputs || ReadShared(record, used, out miss) is not { } objects)
        {
            return null;
        }

        List<(string, int?)> warnings = [.. record["warnings"]!.AsArray()
            .Select(item => (item!["message"]!.GetValue<string>(), item["line"]?.GetValue<int>()))];
        return new CacheHit(outputs, objects, warnings);
    }

    /// <summary>Why the record no longer counts because an input it names has changed, or null when none has.</summary>
    private string? ChangedInput(JsonObject record, SourceFile file)
    {
        foreach (var item in record["inputs"]!.AsArray())
        {
            string name = item!["name"]!.GetValue<string>();
            string? actual = _hashes.Input(file, name);
            if (actual is null || actual != item["hash"]!.GetValue<string>())
            {
                return $"input changed: {name}";
            }
        }

        return null;
    }

    /// <summary>The record's assets, or null with the <paramref name="miss"/> when one's object is gone or its kind is unknown.</summary>
    private List<CachedOutput>? ReadOutputs(JsonObject record, List<FileInfo> used, out string? miss)
    {
        var outputs = new List<CachedOutput>();
        foreach (var item in record["outputs"]!.AsArray())
        {
            string hash = item!["hash"]!.GetValue<string>();
            if (FindObject(hash, used, out miss) is not { } info)
            {
                return null;
            }

            if (!Enum.TryParse(item["kind"]!.GetValue<string>(), out ContentKind kind) || !Enum.IsDefined(kind))
            {
                miss = "record damaged";
                return null;
            }

            outputs.Add(new CachedOutput(item["part"]?.GetValue<string>(), kind, hash, info.Length));
        }

        miss = null;
        return outputs;
    }

    /// <summary>The record's shared objects with their sizes, or null with the <paramref name="miss"/> when one is gone or misnamed.</summary>
    private List<(string Hash, long Size)>? ReadShared(JsonObject record, List<FileInfo> used, out string? miss)
    {
        var objects = new List<(string Hash, long Size)>();
        foreach (var item in record["objects"]?.AsArray() ?? [])
        {
            string hash = item!.GetValue<string>();
            if (!ContentManifest.IsHash(hash))
            {
                miss = "record damaged";
                return null;
            }

            if (FindObject(hash, used, out miss) is not { } info)
            {
                return null;
            }

            objects.Add((hash, info.Length));
        }

        miss = null;
        return objects;
    }

    /// <summary>The cache's file for an object a record names, added to <paramref name="used"/>, or null with the miss when it's gone.</summary>
    private FileInfo? FindObject(string hash, List<FileInfo> used, out string? miss)
    {
        var info = new FileInfo(CacheObjects.PathOf(_folder, hash));
        if (!info.Exists)
        {
            miss = $"cached object gone: {hash}";
            return null;
        }

        used.Add(info);
        miss = null;
        return info;
    }

    /// <summary>Puts a hit's objects in the store. Gives the miss when one of them is damaged, or null when all went in.</summary>
    private string? PutInStore(IEnumerable<string> hashes, StoreWriter store)
    {
        foreach (string hash in hashes)
        {
            // The object is checked against its name before it reaches the store, so a damaged cache can't ship.
            // Once checked, the store links to the cache's copy when it can, so the bytes aren't kept twice.
            string objectFile = CacheObjects.PathOf(_folder, hash);
            bool restored = store.PutOnce(hash, () =>
            {
                byte[] bytes = File.ReadAllBytes(objectFile);
                return Convert.ToHexStringLower(SHA256.HashData(bytes)) == hash ? bytes : null;
            }, objectFile);
            if (!restored)
            {
                return $"cached object damaged: {hash}";
            }
        }

        return null;
    }

    /// <summary>Keeps a cook's result, with the cook's warnings. Failing to write the cache is never a failure of the build.</summary>
    /// <param name="hashes">The hash of each of the <paramref name="output"/>'s assets, in the same order.</param>
    public void Save(string key, SourceFile file, IReadOnlyList<string> inputs, CookOutput output, IReadOnlyList<string> hashes)
    {
        try
        {
            if (InputNodes(file, inputs) is not { } inputNodes)
            {
                return;
            }

            var outputNodes = new JsonArray();
            for (int i = 0; i < output.Assets.Count; i++)
            {
                var (part, kind, bytes) = output.Assets[i];
                CacheObjects.Save(_folder, hashes[i], bytes, Usage);
                outputNodes.Add(new JsonObject { ["part"] = part, ["kind"] = kind.ToString(), ["hash"] = hashes[i] });
            }

            var objectNodes = new JsonArray();
            foreach (var (hash, bytes) in output.Objects)
            {
                CacheObjects.Save(_folder, hash, bytes, Usage);
                objectNodes.Add(hash);
            }

            var warningNodes = new JsonArray();
            foreach (var (message, line, _) in output.Problems)
            {
                warningNodes.Add(new JsonObject { ["message"] = message, ["line"] = line });
            }

            var record = new JsonObject { ["inputs"] = inputNodes, ["outputs"] = outputNodes, ["objects"] = objectNodes, ["warnings"] = warningNodes };
            CacheObjects.WriteAtomically(RecordFile(key), Encoding.UTF8.GetBytes(record.ToJsonString()));
            Usage.Wrote(RecordFile(key));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation. A cook that couldn't be remembered is still a cook.
        }
    }

    /// <summary>Each input with its hash as a record lists them, or null when one can't be read, so the cook isn't kept.</summary>
    private JsonArray? InputNodes(SourceFile file, IReadOnlyList<string> inputs)
    {
        // Sorted, since parallel steps read their inputs in whatever order they run, and the same cook should make the same record.
        var inputNodes = new JsonArray();
        foreach (string name in inputs.Order(StringComparer.Ordinal))
        {
            string? hash = _hashes.Input(file, name);
            if (hash is null)
            {
                return null;
            }

            inputNodes.Add(new JsonObject { ["name"] = name, ["hash"] = hash });
        }

        return inputNodes;
    }

    /// <summary>The cache's file for an object, or null when the cache doesn't keep it, such as when saving it failed.</summary>
    public string? SavedObject(string hash)
    {
        string objectFile = CacheObjects.PathOf(_folder, hash);
        return File.Exists(objectFile) ? objectFile : null;
    }

    /// <summary>Writes down the file hashes learned this run.</summary>
    public void Flush()
    {
        _hashes.Flush();
    }

    /// <summary>
    /// Trims the cache to <paramref name="limit"/> bytes, least recently used first, keeping everything this cook used.
    /// See <see cref="CacheFolder.Trim(string, long)"/>.
    /// </summary>
    public CacheTrim Trim(long limit)
    {
        return CacheFolder.Trim(_folder, limit, Usage, DateTime.UtcNow);
    }

    private string RecordFile(string key)
    {
        return Path.Combine(_folder, "records", key + ".json");
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
