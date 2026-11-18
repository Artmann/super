using System.ComponentModel;

namespace Super;

/// <summary>
/// Development help for a game run from source: writes the schemas, runs <c>super-cook --watch</c>, and asks for a
/// reload whenever the cooked manifest is written again. It never runs without a window.
/// </summary>
internal sealed class ContentLive
{
    /// <summary>How long the cooked manifest must be quiet before a reload, since a cook can write it more than once.</summary>
    private static readonly TimeSpan ReloadQuietTime = TimeSpan.FromMilliseconds(100);

    private readonly Func<(string Folder, DevSettings Dev)?> _source;
    private readonly Action<string> _writeSchemas;
    private ContentWatcher? _watcher;
    private ICookProcess? _cook;
    private volatile bool _reloadWanted;

    /// <param name="source">
    /// The cooked folder and what the build wrote beside it when the game runs from source, or null, as in a
    /// published game, when there's nothing to watch.
    /// </param>
    /// <param name="writeSchemas">Writes a schema for every catalog into a content root.</param>
    public ContentLive(Func<(string Folder, DevSettings Dev)?> source, Action<string> writeSchemas)
    {
        _source = source;
        _writeSchemas = writeSchemas;
    }

    /// <summary>Whether files are being watched. This never happens without a window.</summary>
    public bool IsLive => _watcher is not null;

    /// <summary>Starts <c>super-cook --watch</c> over the sources into the cooked folder. Tests swap it for a stand-in.</summary>
    public Func<DevSettings, string, ICookProcess> StartCook { get; set; } = (dev, outFolder) => new CookProcess(dev, outFolder);

    /// <summary>Set when a file changed. The next tick reads everything again.</summary>
    public bool ReloadWanted
    {
        get => _reloadWanted;
        set => _reloadWanted = value;
    }

    /// <summary>
    /// Starts watching for a game run from source, which the build marks with <c>content.dev.json</c> beside the
    /// cooked folder: it writes the schemas into the first content root, starts <c>super-cook --watch</c>, and asks
    /// for a reload whenever the cooked manifest is written again. Without that file it does nothing.
    /// </summary>
    public void GoLive()
    {
        if (_watcher is not null || _source() is not (var folder, var dev))
        {
            return;
        }

        if (dev.Roots.Length > 0 && Directory.Exists(dev.Roots[0]))
        {
            _writeSchemas(dev.Roots[0]);
        }

        // Watching starts first, so a cook that lands while the tool starts up isn't missed.
        Directory.CreateDirectory(folder);
        _watcher = new ContentWatcher(folder, ContentManifest.FileName, () => ReloadWanted = true, ReloadQuietTime);
        try
        {
            _cook = StartCook(dev, folder);
        }
        catch (Win32Exception problem)
        {
            // Without dotnet on the path the game still runs, and a cook started by hand is still heard.
            Console.Error.WriteLine($"Could not start super-cook to watch the content: {problem.Message}");
        }
    }

    /// <summary>Stops the cook and stops watching files.</summary>
    public void StopLive()
    {
        _watcher?.Dispose();
        _watcher = null;
        _cook?.Dispose();
        _cook = null;
    }
}
