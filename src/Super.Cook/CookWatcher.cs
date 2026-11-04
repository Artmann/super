namespace Super.Cook;

/// <summary>
/// Cooks again whenever a source changes. Editors save in several steps, so it waits for
/// the roots to go quiet first. A failed cook keeps the old manifest, and a file another
/// program is holding open is tried again after the same wait.
/// </summary>
public sealed class CookWatcher : IDisposable
{
    private readonly CookPipeline _pipeline;
    private readonly IReadOnlyList<FolderRoot> _roots;
    private readonly ICookedStore _store;
    private readonly TextWriter _log;
    private readonly TimeSpan _quiet;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ITimer _timer;
    private readonly object _cooking = new();
    private bool _disposed;

    public CookWatcher(CookPipeline pipeline, IReadOnlyList<FolderRoot> roots, ICookedStore store, TextWriter log, TimeSpan quiet, TimeProvider? clock = null)
    {
        _pipeline = pipeline;
        _roots = roots;
        _store = store;
        _log = log;
        _quiet = quiet;
        _timer = (clock ?? TimeProvider.System).CreateTimer(_ => CookNow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        foreach (var root in roots)
        {
            var watcher = new FileSystemWatcher(root.Label)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += OnFile;
            watcher.Created += OnFile;
            watcher.Deleted += OnFile;
            watcher.Renamed += OnFile;

            // Too many changes at once overflow the watcher's buffer. Cooking everything again is always right.
            watcher.Error += (_, _) => Schedule();
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    /// <summary>Stops watching, after any cook in progress has finished.</summary>
    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        lock (_cooking)
        {
            Volatile.Write(ref _disposed, true);
            _timer.Dispose();
        }
    }

    /// <summary>Whether a change at this path, relative to a root, can change what cooks. Dot-files and dot-folders never do.</summary>
    internal static bool Matters(string relative)
    {
        return !relative.Split('/', '\\').Any(part => part.StartsWith('.'));
    }

    private void OnFile(object sender, FileSystemEventArgs change)
    {
        // A file renamed to a dot-name is gone as far as cooking goes, so the old name counts too.
        bool renamedAway = change is RenamedEventArgs { OldName: { } old } && Matters(old);
        if (change.Name is null || Matters(change.Name) || renamedAway)
        {
            Schedule();
        }
    }

    /// <summary>Starts the wait again, so the cook happens once the roots have been quiet for the whole wait.</summary>
    /// <remarks>It doesn't wait for a cook in progress, so the watchers keep reading changes while a slow cook runs.</remarks>
    private void Schedule()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        try
        {
            _timer.Change(_quiet, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check and the change. Nothing more to cook.
        }
    }

    private void CookNow()
    {
        lock (_cooking)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var result = _pipeline.Cook(_roots, _store);
                foreach (var problem in result.Problems)
                {
                    _log.WriteLine(problem.ToString());
                }

                int errors = result.Problems.Count(problem => problem.IsError);
                _log.WriteLine(errors == 0 ? "Cooked." : $"Not cooked: {errors} errors.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Usually another program, an editor or the game, has a file open for a moment.
                _log.WriteLine($"Not cooked: {exception.Message} Trying again.");
                _timer.Change(_quiet, Timeout.InfiniteTimeSpan);
            }
        }
    }
}
