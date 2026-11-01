namespace Super;

/// <summary>
/// Watches the content folder while a game is being worked on. Editors save in several
/// steps, so it waits for the folder to go quiet before saying anything changed.
/// </summary>
internal sealed class ContentWatcher : IDisposable
{
    private const string SchemaFolder = ".schemas";

    private readonly FileSystemWatcher _watcher;
    private readonly ITimer _quiet;
    private readonly TimeSpan _wait;

    public ContentWatcher(string root, Action changed, TimeSpan wait, TimeProvider? clock = null)
    {
        _wait = wait;
        _quiet = (clock ?? TimeProvider.System).CreateTimer(_ => changed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watcher = new FileSystemWatcher(root, "*.json") { IncludeSubdirectories = true };
        _watcher.Changed += OnFile;
        _watcher.Created += OnFile;
        _watcher.Deleted += OnFile;
        _watcher.Renamed += OnFile;
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _quiet.Dispose();
    }

    /// <summary>Whether a change at this path, relative to the content folder, is content rather than a generated schema.</summary>
    internal static bool Matters(string name) => !name.StartsWith(SchemaFolder, StringComparison.Ordinal);

    /// <summary>Notes a change to a file, and starts the wait again if it is content.</summary>
    internal void Saw(string name)
    {
        if (Matters(name))
        {
            _quiet.Change(_wait, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnFile(object sender, FileSystemEventArgs change) => Saw(change.Name!);
}
