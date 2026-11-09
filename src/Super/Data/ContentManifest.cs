using System.Text.Json;

namespace Super;

/// <summary>What kind of content a cooked asset holds.</summary>
public enum ContentKind
{
    Data,
    Texture,
    Sound,
    Model,
}

/// <summary>One cooked asset: what it is, the SHA-256 of its bytes in lowercase hex, and how many bytes.</summary>
public sealed record ManifestEntry(ContentKind Kind, string Hash, long Size);

/// <summary>
/// The index of cooked content: every asset name and the object holding its bytes. Names are compared ordinally.
/// <see cref="Serialize"/> is deterministic, so the same assets always give the same bytes.
/// </summary>
public sealed class ContentManifest
{
    public const string FileName = "content.manifest";

    public static ContentManifest Empty { get; } = new("", new Dictionary<string, ManifestEntry>());

    public ContentManifest(string target, IReadOnlyDictionary<string, ManifestEntry> assets, IEnumerable<string>? objects = null)
    {
        Target = target;
        Assets = new Dictionary<string, ManifestEntry>(assets, StringComparer.Ordinal);
        Objects = new SortedSet<string>(objects ?? [], StringComparer.Ordinal);
    }

    public string Target { get; }

    public IReadOnlyDictionary<string, ManifestEntry> Assets { get; }

    /// <summary>
    /// Objects that have no name of their own but that assets point to by hash, such as the textures
    /// models share. Listed so they're kept with the assets, in ordinal order.
    /// </summary>
    public IReadOnlySet<string> Objects { get; }

    /// <summary>Every object the manifest keeps: the assets' and <see cref="Objects"/>.</summary>
    public IEnumerable<string> AllObjects => Assets.Values.Select(entry => entry.Hash).Concat(Objects);

    /// <summary>Whether text is a hash as the manifest writes them: 64 lowercase hex digits. Anything else can't name an object file.</summary>
    public static bool IsHash(string text) => text.Length == 64 && text.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    /// <summary>Where an object lives under the content folder.</summary>
    public static string ObjectPath(string hash)
    {
        return $"objects/{hash[..2]}/{hash}";
    }

    /// <summary>Reads a manifest. <paramref name="where"/> names the file in the error when it can't be read.</summary>
    /// <exception cref="ContentException">The JSON is bad or isn't a version 1 manifest.</exception>
    public static ContentManifest Parse(byte[] json, string where)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Fail(where, "the manifest must be a JSON object.");
            }

            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int number) || number != 1)
            {
                throw Fail(where, "the manifest version must be 1.");
            }

            if (!root.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.String)
            {
                throw Fail(where, "the manifest needs a \"target\" string.");
            }

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Object)
            {
                throw Fail(where, "the manifest needs an \"assets\" object.");
            }

            var entries = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
            foreach (var asset in assets.EnumerateObject())
            {
                if (asset.Value.ValueKind != JsonValueKind.Object
                    || !asset.Value.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
                    || !asset.Value.TryGetProperty("hash", out var hash) || hash.ValueKind != JsonValueKind.String || hash.GetString()!.Length < 2
                    || !asset.Value.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes))
                {
                    throw Fail(where, $"the asset \"{asset.Name}\" needs a kind, a hash and a size.");
                }

                if (!TryParseKind(kind.GetString()!, out var parsed))
                {
                    throw Fail(where, $"the asset \"{asset.Name}\" has the unknown kind \"{kind.GetString()}\".");
                }

                entries[asset.Name] = new ManifestEntry(parsed, hash.GetString()!, bytes);
            }

            var objects = new List<string>();
            if (root.TryGetProperty("objects", out var listed))
            {
                if (listed.ValueKind != JsonValueKind.Array)
                {
                    throw Fail(where, "\"objects\" must be a list of hashes.");
                }

                foreach (var item in listed.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || !IsHash(item.GetString()!))
                    {
                        throw Fail(where, $"\"objects\" must hold only hashes, and {item.GetRawText()} isn't one.");
                    }

                    objects.Add(item.GetString()!);
                }
            }

            return new ContentManifest(target.GetString()!, entries, objects);
        }
        catch (JsonException exception)
        {
            throw Fail(where, exception.Message);
        }
    }

    /// <summary>Writes the manifest as JSON with names sorted ordinally, <c>\n</c> newlines and no byte order mark.</summary>
    public byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("target", Target);
            writer.WriteStartObject("assets");
            foreach (var (name, entry) in Assets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(name);
                writer.WriteString("kind", KindName(entry.Kind));
                writer.WriteString("hash", entry.Hash);
                writer.WriteNumber("size", entry.Size);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            // Left out when empty, so content without shared objects writes what it always did.
            if (Objects.Count > 0)
            {
                writer.WriteStartArray("objects");
                foreach (string hash in Objects)
                {
                    writer.WriteStringValue(hash);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static ContentException Fail(string where, string message)
    {
        return new ContentException([$"{where}: {message}"]);
    }

    private static string KindName(ContentKind kind)
    {
        string name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static bool TryParseKind(string name, out ContentKind kind)
    {
        foreach (var candidate in Enum.GetValues<ContentKind>())
        {
            if (KindName(candidate) == name)
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}
