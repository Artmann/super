using Microsoft.Extensions.DependencyInjection;

namespace Super;

/// <summary>
/// Where cooked content comes from: a manifest naming each asset, and its cooked bytes. Games read the
/// cooked folder beside the program, and tests can cook in memory.
/// </summary>
public interface IContentFiles
{
    /// <summary>What a name holds, or null when the cooked content has no such name. Names are case-sensitive.</summary>
    ContentKind? KindOf(string name);

    /// <summary>
    /// The names of every data asset under a folder, in ordinal order. Null when there are none.
    /// </summary>
    IReadOnlyList<string>? List(string folder);

    /// <summary>The cooked bytes of an asset.</summary>
    byte[] Read(string name);

    /// <summary>
    /// The cooked bytes of an asset as a stream, for big assets such as models that are read piece by piece
    /// rather than held whole. The caller disposes it. It fails the way <see cref="Read"/> does.
    /// </summary>
    Stream Open(string name) => new MemoryStream(Read(name), writable: false);

    /// <summary>The parts cooked from one source, such as <c>tall</c> and <c>young</c> for <c>firs.glb</c>, in ordinal order.</summary>
    IReadOnlyList<string> Parts(string name) => [];

    /// <summary>Reads the manifest again, after the content was cooked again.</summary>
    void Refresh()
    {
    }
}

public static class ContentServices
{
    /// <summary>Reads content from somewhere other than the cooked folder beside the program.</summary>
    public static IServiceCollection AddContentFiles(this IServiceCollection services, IContentFiles files) =>
        services.AddSingleton(files);
}

/// <summary>
/// A folder of cooked content: <c>content.manifest</c> and the objects it names. A game has it beside the
/// program, in <c>content</c>. Without a manifest it's empty.
/// </summary>
public sealed class CookedFolder : IContentFiles
{
    private const string FolderName = "content";

    private readonly string _folder;
    private readonly Lazy<DevSettings?> _dev;
    private volatile ContentManifest _manifest = ContentManifest.Empty;

    public CookedFolder(string folder)
    {
        _folder = Path.GetFullPath(folder);
        _dev = new Lazy<DevSettings?>(() => DevSettings.Read(Path.Combine(_folder, "..", DevSettings.FileName)));
        Refresh();
    }

    /// <summary>The <c>content</c> folder beside the program.</summary>
    public static CookedFolder BesideProgram() => new(Path.Combine(AppContext.BaseDirectory, FolderName));

    /// <summary>The folder's full path.</summary>
    internal string Folder => _folder;

    /// <summary>What the build wrote beside the folder when the game runs from source, or null for a shipped game.</summary>
    internal DevSettings? Dev => _dev.Value;

    public ContentKind? KindOf(string name) => _manifest.Assets.TryGetValue(name, out var entry) ? entry.Kind : null;

    public IReadOnlyList<string>? List(string folder) => ContentNames.DataUnder(_manifest.Assets, folder);

    public IReadOnlyList<string> Parts(string name) => ContentNames.PartsIn(_manifest.Assets.Keys, name);

    public byte[] Read(string name)
    {
        return File.ReadAllBytes(ObjectFile(name));
    }

    /// <summary>
    /// Reads from the object file as the caller goes, with a buffer small enough to stay off the large
    /// object heap. The file stays open to deleting and replacing, so a cook running beside the game
    /// isn't stopped by a load in progress.
    /// </summary>
    public Stream Open(string name)
    {
        return new FileStream(ObjectFile(name), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize: 1 << 16);
    }

    public void Refresh()
    {
        string path = Path.Combine(_folder, ContentManifest.FileName);
        _manifest = File.Exists(path) ? ContentManifest.Parse(File.ReadAllBytes(path), path) : ContentManifest.Empty;
    }

    private string ObjectFile(string name)
    {
        if (!_manifest.Assets.TryGetValue(name, out var entry))
        {
            throw new FileNotFoundException(ContentNames.Missing(this, name), name);
        }

        return Path.Combine(_folder, ContentManifest.ObjectPath(entry.Hash));
    }
}

/// <summary>The checks and messages every kind of content files share.</summary>
internal static class ContentNames
{
    /// <summary>Stops with a <see cref="ContentException"/> unless the name holds the wanted kind.</summary>
    public static void Require(IContentFiles files, string name, ContentKind wanted)
    {
        if (Problem(files, name, wanted) is { } problem)
        {
            throw new ContentException([problem]);
        }
    }

    /// <summary>What's wrong with naming this, or null when nothing is.</summary>
    public static string? Problem(IContentFiles files, string name, ContentKind wanted)
    {
        var kind = files.KindOf(name);
        if (kind == wanted)
        {
            return null;
        }

        return kind is { } other ? $"{name} is a {KindName(other)}, not a {KindName(wanted)}." : Missing(files, name);
    }

    public static string Missing(IContentFiles files, string name)
    {
        string message = $"{name} isn't in the cooked content. Check the name, including its case and extension.";
        int hash = name.IndexOf('#');
        if (hash < 0)
        {
            return message;
        }

        string source = name[..hash];
        var parts = files.Parts(source);
        return parts.Count == 0 ? message : $"{message} {source} has the parts: {string.Join(", ", parts)}.";
    }

    public static string KindName(ContentKind kind) => kind.ToString().ToLowerInvariant();

    public static IReadOnlyList<string> PartsIn(IEnumerable<string> names, string source)
    {
        string prefix = source + "#";
        return names
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(name => name[prefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<string>? DataUnder(IReadOnlyDictionary<string, ManifestEntry> assets, string folder)
    {
        string prefix = folder + "/";
        string[] found = assets
            .Where(pair => pair.Value.Kind == ContentKind.Data && pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return found.Length > 0 ? found : null;
    }
}
