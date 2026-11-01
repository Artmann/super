namespace Super;

/// <summary>
/// Watches the content folder while a game is being worked on. Editors save in several
/// steps, so it waits for the folder to go quiet before saying anything changed.
/// </summary>
internal sealed class ContentWatcher : IDisposable
{
    private const string SchemaFolder = ".schemas";

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _quiet;
    private readonly TimeSpan _wait;

    public ContentWatcher(string root, Action changed, TimeSpan wait)
    {
        _wait = wait;
        _quiet = new Timer(_ => changed());
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

    private void OnFile(object sender, FileSystemEventArgs change)
    {
        if (!change.Name!.StartsWith(SchemaFolder, StringComparison.Ordinal))
        {
            _quiet.Change(_wait, Timeout.InfiniteTimeSpan);
        }
    }
}
