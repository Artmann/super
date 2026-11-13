using System.Globalization;
using Rendition;

namespace Super;

/// <summary>
/// Keeps the bakes the renderer finishes in the background, given to <see cref="Done"/> on the render thread. A space's
/// bake is written into the first content root, under <c>lighting/</c>, through a temp file and a move, so the watch
/// cook brings it in. A chunk's goes to the <see cref="BakeCache"/>, and then <c>written</c> is told its file, so the
/// render thread can forget the bake it holds and read the new one. The writing happens on the thread pool, one bake
/// after another in the order they finished, so a Final is never overwritten by an earlier Preview. Each bake kept logs
/// one line, and each one that can't be logs why. See "Baking without a build step" in docs/rendering.md.
/// </summary>
internal sealed class BakeSaver
{
    private const string Unfinished = ".tmp";

    private readonly string? _spaceFolder;
    private readonly BakeCache? _cache;
    private readonly Action<string> _written;
    private readonly TextWriter _log;
    private readonly TextWriter _errors;
    private readonly Lock _lock = new();
    private Task _last = Task.CompletedTask;

    /// <param name="spaceFolder">The content root a space's bake goes into, or null where there are no sources.</param>
    /// <param name="cache">Where a chunk's bake goes, or null to keep none.</param>
    /// <param name="written">Told each chunk file written to the cache, on the thread pool.</param>
    /// <param name="log">Where each kept bake is said.</param>
    /// <param name="errors">Where each bake that can't be kept is said.</param>
    public BakeSaver(string? spaceFolder, BakeCache? cache, Action<string> written, TextWriter log, TextWriter errors)
    {
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(errors);
        _spaceFolder = spaceFolder;
        _cache = cache;
        _written = written;
        _log = log;
        _errors = errors;
    }

    public string? SpaceFolder => _spaceFolder;

    public BakeCache? Cache => _cache;

    /// <summary>Hands the bake to the thread pool to be kept, and returns at once. Never throws.</summary>
    public void Done(BakeDone done)
    {
        lock (_lock)
        {
            _last = _last.ContinueWith(_ => Keep(done), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the bakes handed over so far to be kept.</summary>
    /// <returns>Whether they all were.</returns>
    public bool Wait(TimeSpan timeout)
    {
        Task last;
        lock (_lock)
        {
            last = _last;
        }

        return last.Wait(timeout);
    }

    private void Keep(BakeDone done)
    {
        try
        {
            // Read first, so a file that doesn't read back is never kept.
            double seconds = LightBakeFile.Read(done.Bytes).Report.Time.TotalSeconds;
            string? path = done.File == BakeFiles.Space(done.Name) ? KeepSpace(done) : KeepChunk(done);
            if (path is null)
            {
                return;
            }

            _log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Baked {done.Name} ({done.Quality}) in {seconds:0.0} s: {path}"));
        }
        catch (Exception e)
        {
            // Nothing else hears of a failure on the pool, and the game carries on drawing this area live.
            _errors.WriteLine($"Couldn't keep the bake of {done.Name}: {e.Message}");
        }
    }

    private string? KeepSpace(BakeDone done)
    {
        if (_spaceFolder is null)
        {
            _errors.WriteLine($"Not keeping the bake of {done.Name}: there are no sources to write it into.");
            return null;
        }

        string path = Path.GetFullPath(Path.Combine(_spaceFolder, done.File));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string unfinished = $"{path}.{Guid.NewGuid():N}{Unfinished}";
        try
        {
            File.WriteAllBytes(unfinished, done.Bytes);
            File.Move(unfinished, path, overwrite: true);
        }
        catch
        {
            File.Delete(unfinished);
            throw;
        }

        return path;
    }

    private string? KeepChunk(BakeDone done)
    {
        if (_cache is null)
        {
            _errors.WriteLine($"Not keeping the bake of {done.Name}: there is no bake cache.");
            return null;
        }

        _cache.Write(done.File, done.Bytes);
        _written(done.File);
        return Path.GetFullPath(Path.Combine(_cache.Folder, done.File));
    }
}
