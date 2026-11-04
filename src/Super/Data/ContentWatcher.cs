using System.IO.Enumeration;

namespace Super;

/// <summary>
/// Watches files in one folder while a game is being worked on, such as the cooked
/// <c>content.manifest</c>. Files are written in several steps, so it waits for them to go
/// quiet before saying anything changed.
/// </summary>
internal sealed class ContentWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly ITimer _quiet;
    private readonly TimeSpan _wait;
    private readonly string _filter;

    /// <param name="folder">The folder to watch. Its subfolders aren't watched.</param>
    /// <param name="filter">The file names that count, such as <c>content.manifest</c> or <c>*.json</c>.</param>
    /// <param name="changed">Called once the files have been quiet for <paramref name="wait"/>.</param>
    /// <param name="wait">How long the files must be quiet.</param>
    /// <param name="clock">The clock the wait runs on.</param>
    public ContentWatcher(string folder, string filter, Action changed, TimeSpan wait, TimeProvider? clock = null)
    {
        _wait = wait;
        _filter = filter;
        _quiet = (clock ?? TimeProvider.System).CreateTimer(_ => changed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watcher = new FileSystemWatcher(folder, filter) { IncludeSubdirectories = false };
        _watcher.Changed += OnFile;
        _watcher.Created += OnFile;
        _watcher.Deleted += OnFile;

        // Writers replace a file by moving a temp file over it, which shows up as a rename.
        _watcher.Renamed += OnFile;
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _quiet.Dispose();
    }

    /// <summary>Notes a change to a file, relative to the folder, and starts the wait again if the filter names it.</summary>
    internal void Saw(string name)
    {
        if (FileSystemName.MatchesSimpleExpression(_filter, name, ignoreCase: false))
        {
            _quiet.Change(_wait, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnFile(object sender, FileSystemEventArgs change) => Saw(change.Name!);
}
