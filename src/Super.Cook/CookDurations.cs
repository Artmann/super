using System.Text.Json;

namespace Super.Cook;

/// <summary>
/// How long each source took the last time it was cooked, kept in <c>durations.json</c> in the cache folder, so the next
/// cook starts the slowest first and a big model isn't left to cook alone at the end. Losing it costs nothing but the order.
/// </summary>
internal sealed class CookDurations
{
    private const string FileName = "durations.json";

    /// <summary>The most sources kept. Past it, those this cook didn't time are dropped.</summary>
    internal const int MaxSources = 20_000;

    private readonly string _folder;
    private readonly Dictionary<string, double> _seconds;

    private CookDurations(string folder, Dictionary<string, double> seconds)
    {
        _folder = folder;
        _seconds = seconds;
    }

    /// <summary>The durations kept in <paramref name="folder"/>, or none when there's no file or it doesn't read.</summary>
    public static CookDurations Load(string folder)
    {
        var seconds = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllBytes(Path.Combine(folder, FileName)));
            foreach (var (source, value) in read ?? [])
            {
                if (double.IsFinite(value) && value >= 0)
                {
                    seconds[source] = value;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // No timings: the cook falls back to sizes.
        }

        return new CookDurations(folder, seconds);
    }

    /// <summary>The seconds <paramref name="file"/> took the last time it was cooked, or null when it hasn't been timed.</summary>
    public double? Seconds(SourceFile file) => _seconds.TryGetValue(file.Describe(), out double seconds) ? seconds : null;

    /// <summary>Keeps the time of every source this cook cooked, not those it took from the cache, which take no time.</summary>
    public void Save(IEnumerable<CookedFile> files, IReadOnlyList<SourceFile> sources)
    {
        var timed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, source) in files.Zip(sources))
        {
            if (file.Outcome != CookOutcome.FromCache)
            {
                _seconds[source.Describe()] = Math.Round(file.Elapsed.TotalSeconds, 3);
                timed.Add(source.Describe());
            }
        }

        if (timed.Count == 0)
        {
            return;
        }

        if (_seconds.Count > MaxSources)
        {
            foreach (string source in _seconds.Keys.Where(source => !timed.Contains(source)).ToList())
            {
                _seconds.Remove(source);
            }
        }

        // Written beside it and moved over it, so a cook that stops halfway, or another cook reading it, never sees half a file.
        string path = Path.Combine(_folder, FileName);
        string temporary = Path.Combine(_folder, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(
                new SortedDictionary<string, double>(_seconds, StringComparer.Ordinal)));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another cook may be moving its own over it. Either one's timings will do.
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The order to start <paramref name="files"/> in, as indices into it: those never timed first, by their cooker's
    /// <see cref="ICooker.StartRank"/> and then size, then the rest from the slowest last time. Ties keep source order.
    /// </summary>
    public static int[] StartOrder(IReadOnlyList<(SourceFile File, ICooker Cooker)> files, CookDurations? durations)
    {
        var known = new double?[files.Count];
        var sizes = new long[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            known[i] = durations?.Seconds(files[i].File);
            sizes[i] = known[i] is null ? SizeOf(files[i].File) : 0;
        }

        return [.. Enumerable.Range(0, files.Count)
            .OrderBy(i => known[i] is null ? 0 : 1)
            .ThenByDescending(i => known[i] is null ? files[i].Cooker.StartRank : 0)
            .ThenByDescending(i => known[i] ?? sizes[i])
            .ThenBy(i => i)];
    }

    private static long SizeOf(SourceFile file)
    {
        try
        {
            return file.Root.FullPath(file.Name) is { } path ? new FileInfo(path).Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
