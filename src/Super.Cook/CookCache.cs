using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Super.Cook;

/// <summary>One cooked asset in a cache record. <see cref="Part"/> is null for the source's own asset.</summary>
internal sealed record CachedOutput(string? Part, ContentKind Kind, string Hash, long Size);

/// <summary>What a cache hit gives back: the outputs, now in the store, and the warnings the cook made.</summary>
internal sealed record CacheHit(IReadOnlyList<CachedOutput> Outputs, IReadOnlyList<(string Message, int? Line)> Warnings);

/// <summary>
/// Remembers cooked results between runs. A record is keyed by the cooker, its version, the target, the settings and the
/// source's hash, and lists every other file the cooker read with the hash it had. The record only counts while all of those
/// hashes still match. Two builds can share one cache: everything is written to a temp file and moved into place, and a record
/// that can't be read is just a miss.
/// </summary>
internal sealed class CookCache(string folder)
{
    /// <summary>What an input that isn't there hashes to, so a record stays valid until the file appears.</summary>
    private const string Missing = "missing";

    private const string Unresolvable = "unresolvable";

    private readonly string _folder = Path.GetFullPath(folder);
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
    public CacheHit? TryRestore(string key, SourceFile file, ICookedStore store)
    {
        try
        {
            var record = JsonNode.Parse(File.ReadAllBytes(RecordFile(key))) as JsonObject;
            if (record is null)
            {
                return null;
            }

            foreach (var item in record["inputs"]!.AsArray())
            {
                string name = item!["name"]!.GetValue<string>();
                string? resolved = CookInput.ResolveName(file.Name, name);
                string? actual = resolved is null ? Unresolvable : HashFile(file.Root, resolved);
                if (actual is null || actual != item["hash"]!.GetValue<string>())
                {
                    return null;
                }
            }

            var outputs = new List<CachedOutput>();
            foreach (var item in record["outputs"]!.AsArray())
            {
                string hash = item!["hash"]!.GetValue<string>();
                var info = new FileInfo(ObjectFile(hash));
                if (!info.Exists || !Enum.TryParse(item["kind"]!.GetValue<string>(), out ContentKind kind) || !Enum.IsDefined(kind))
                {
                    return null;
                }

                outputs.Add(new CachedOutput(item["part"]?.GetValue<string>(), kind, hash, info.Length));
            }

            var warnings = new List<(string, int?)>();
            foreach (var item in record["warnings"]!.AsArray())
            {
                warnings.Add((item!["message"]!.GetValue<string>(), item["line"]?.GetValue<int>()));
            }

            foreach (var output in outputs)
            {
                if (store.Has(output.Hash))
                {
                    continue;
                }

                byte[] bytes = File.ReadAllBytes(ObjectFile(output.Hash));
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != output.Hash)
                {
                    return null;
                }

                store.Put(output.Hash, bytes);
            }

            return new CacheHit(outputs, warnings);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Keeps a cook's result. Failing to write the cache is never a failure of the build.</summary>
    public void Save(string key, SourceFile file, IReadOnlyList<string> inputs,
        IReadOnlyList<(string? Part, ContentKind Kind, byte[] Bytes)> assets, IReadOnlyList<(string Message, int? Line)> warnings)
    {
        try
        {
            var inputNodes = new JsonArray();
            foreach (string name in inputs)
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
            foreach (var (part, kind, bytes) in assets)
            {
                string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                string objectFile = ObjectFile(hash);
                if (!File.Exists(objectFile))
                {
                    WriteAtomically(objectFile, bytes);
                }

                outputNodes.Add(new JsonObject { ["part"] = part, ["kind"] = kind.ToString(), ["hash"] = hash });
            }

            var warningNodes = new JsonArray();
            foreach (var (message, line) in warnings)
            {
                warningNodes.Add(new JsonObject { ["message"] = message, ["line"] = line });
            }

            var record = new JsonObject { ["inputs"] = inputNodes, ["outputs"] = outputNodes, ["warnings"] = warningNodes };
            WriteAtomically(RecordFile(key), Encoding.UTF8.GetBytes(record.ToJsonString()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation. A cook that couldn't be remembered is still a cook.
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
            var node = new JsonObject();
            foreach (var (key, hash) in _hashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                node[key] = hash;
            }

            WriteAtomically(Path.Combine(_folder, "hashes.json"), Encoding.UTF8.GetBytes(node.ToJsonString()));
            _hashesChanged = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // See Save.
        }
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
            var hashes = Hashes();
            if (hashes.TryGetValue(key, out string? known))
            {
                return known;
            }

            string hash = Convert.ToHexStringLower(SHA256.HashData(root.Read(name)));
            hashes[key] = hash;
            _hashesChanged = true;
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
        return Path.Combine(_folder, "objects", hash[..2], hash);
    }

    private static void WriteAtomically(string path, byte[] bytes)
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
