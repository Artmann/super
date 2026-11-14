using System.Diagnostics;

namespace Rendition;

/// <summary>
/// Compiles shaders again as their sources are saved, for a game run from source. It watches the folders named by
/// the <c>.sources</c> files that Shaders.targets leaves in the <c>shaders</c> folder next to the program, and
/// writes each compiled shader over the one there. Ask <see cref="TakeChanged"/> between frames and hand what it
/// gives to <see cref="Renderer.ReloadShaders"/>. A shader that doesn't compile is reported and the old one stays.
/// </summary>
public sealed class ShaderWatch : IDisposable
{
    private const int QuietMilliseconds = 100;

    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>();

    private readonly ShaderSources _sources;
    private readonly Action<string> _log;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _quiet;
    private readonly Lock _gate = new();
    private readonly Lock _compiling = new();
    private readonly HashSet<string> _saved = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _changed = [];

    private ShaderWatch(ShaderSources sources, Action<string> log)
    {
        _sources = sources;
        _log = log;
        _quiet = new Timer(_ => CompileSaved());
    }

    /// <summary>The <c>shaders</c> folder next to the program, where the renderer reads compiled shaders.</summary>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "shaders");

    /// <summary>
    /// Whether this folder of compiled shaders says where its sources are, and they are there. False for a
    /// published game.
    /// </summary>
    public static bool CanWatch(string outputFolder) => ShaderSources.Read(outputFolder) is not null;

    /// <summary>
    /// Starts watching the sources of the compiled shaders in a folder, or returns null when it names none.
    /// </summary>
    /// <param name="outputFolder">The folder of compiled shaders, usually <see cref="DefaultFolder"/>.</param>
    /// <param name="log">Told what compiled and what didn't. Called from another thread.</param>
    public static ShaderWatch? Start(string outputFolder, Action<string> log)
    {
        var watch = Open(outputFolder, log);
        if (watch is null)
        {
            return null;
        }

        foreach (string folder in watch._sources.Folders)
        {
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            watcher.Changed += (_, e) => watch.Saved(e.FullPath);
            watcher.Created += (_, e) => watch.Saved(e.FullPath);
            watcher.Renamed += (_, e) => watch.Saved(e.FullPath);
            watcher.EnableRaisingEvents = true;
            watch._watchers.Add(watcher);
        }

        return watch;
    }

    /// <summary>The same without watching: <see cref="Compile"/> is called by hand. Null when the folder names no sources.</summary>
    internal static ShaderWatch? Open(string outputFolder, Action<string> log) =>
        ShaderSources.Read(outputFolder) is { } sources ? new ShaderWatch(sources, log) : null;

    /// <summary>
    /// The shaders compiled since the last call, named as <see cref="Renderer.ReloadShaders"/> wants them, such as
    /// <c>lighting.frag</c>. Empty most of the time, and cheap then.
    /// </summary>
    public IReadOnlySet<string> TakeChanged()
    {
        lock (_gate)
        {
            if (_changed.Count == 0)
            {
                return Nothing;
            }

            var changed = _changed;
            _changed = [];
            return changed;
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _quiet.Dispose();
    }

    // An editor saves a file in several steps, so compiling waits until the folder has been quiet for a moment.
    private void Saved(string path)
    {
        lock (_gate)
        {
            _saved.Add(path);
        }

        try
        {
            _quiet.Change(QuietMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // The game is closing.
        }
    }

    private void CompileSaved()
    {
        string[] saved;
        lock (_gate)
        {
            saved = [.. _saved];
            _saved.Clear();
        }

        Compile(saved);
    }

    /// <summary>
    /// Compiles every shader these saved files are part of: a stage file itself, and for any other file each
    /// stage file that includes it, directly or through another include. Those that compile are written over the
    /// compiled ones and wait for <see cref="TakeChanged"/>.
    /// </summary>
    internal void Compile(IReadOnlyCollection<string> saved)
    {
        lock (_compiling)
        {
            foreach (var stage in _sources.StagesFor(saved))
            {
                string name = stage.Name;
                var time = Stopwatch.StartNew();
                string? error = _sources.Compile(stage);
                if (error is not null)
                {
                    _log($"{name} didn't compile, so the game keeps the old one:{Environment.NewLine}{error.TrimEnd()}");
                    continue;
                }

                lock (_gate)
                {
                    _changed.Add(name);
                }

                _log(FormattableString.Invariant($"Compiled {name} in {time.Elapsed.TotalSeconds:0.00} s."));
            }
        }
    }
}
