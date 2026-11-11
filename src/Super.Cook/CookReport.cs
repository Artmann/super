using System.Diagnostics;
using System.Globalization;

namespace Super.Cook;

/// <summary>What happened to one source in a cook.</summary>
public enum CookOutcome
{
    /// <summary>The cooker ran.</summary>
    Cooked,

    /// <summary>The result came from the cache.</summary>
    FromCache,

    /// <summary>The cooker ran and reported an error.</summary>
    Failed,
}

/// <summary>
/// One source's part of a cook: how long it took, where the time went, and why it wasn't taken from the cache.
/// </summary>
/// <param name="Phases">Time per named phase, in the order they first ran. Cookers add their own through <see cref="CookInput.Time"/>.</param>
/// <param name="Miss">Why the cache had nothing, or null when it was a hit or there is no cache.</param>
public sealed record CookedFile(
    string Name,
    string Cooker,
    CookOutcome Outcome,
    TimeSpan Elapsed,
    IReadOnlyList<(string Phase, TimeSpan Elapsed)> Phases,
    string? Miss,
    long OutputBytes);

/// <summary>Where the time of one cook went, for <c>super-cook --timings</c> and <c>--verbose</c>.</summary>
/// <param name="Elapsed">Wall-clock time of the whole cook.</param>
/// <param name="Steps">Wall-clock time of each step of the pipeline, in order.</param>
/// <param name="Workers">How many sources were cooked at once.</param>
public sealed record CookReport(
    TimeSpan Elapsed,
    IReadOnlyList<(string Step, TimeSpan Elapsed)> Steps,
    IReadOnlyList<CookedFile> Files,
    int Workers,
    bool Skipped)
{
    public static CookReport Empty { get; } = new(TimeSpan.Zero, [], [], 1, false);

    /// <summary>What trimming the cache after the cook did, or null when there's no cache or nothing was cooked.</summary>
    public CacheTrim? Trim { get; init; }

    /// <summary>A short summary: the steps, the cache's size, the time the cookers spent per phase, and the slowest files.</summary>
    public IEnumerable<string> Summary(int slowest = 15)
    {
        yield return Invariant($"Cook took {Seconds(Elapsed)} with {Workers} {(Workers == 1 ? "worker" : "workers")}{(Skipped ? ", nothing had changed since the last cook" : "")}.");
        foreach (var (step, elapsed) in Steps)
        {
            yield return Invariant($"  {step,-28} {Seconds(elapsed),9}");
        }

        if (Trim is not null)
        {
            yield return Trim.Describe();
        }

        var phases = Files
            .SelectMany(file => file.Phases)
            .GroupBy(phase => phase.Phase, StringComparer.Ordinal)
            .Select(group => (Phase: group.Key, Elapsed: group.Aggregate(TimeSpan.Zero, (sum, phase) => sum + phase.Elapsed)))
            .OrderByDescending(phase => phase.Elapsed)
            .ToList();
        if (phases.Count > 0)
        {
            yield return "Time in each phase, added up over every source (more than the wall clock when cooking in parallel):";
            foreach (var (phase, elapsed) in phases)
            {
                yield return Invariant($"  {phase,-28} {Seconds(elapsed),9}");
            }
        }

        var cooked = Files.Where(file => file.Outcome != CookOutcome.FromCache).OrderByDescending(file => file.Elapsed).Take(slowest).ToList();
        if (cooked.Count > 0)
        {
            yield return Invariant($"Slowest {cooked.Count} cooked:");
            foreach (var file in cooked)
            {
                yield return Invariant($"  {Seconds(file.Elapsed),9}  {Megabytes(file.OutputBytes),9}  {file.Name}");
            }
        }

        var misses = Files.Where(file => file.Miss is not null)
            .GroupBy(file => MissKind(file.Miss!), StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ToList();
        if (misses.Count > 0)
        {
            yield return "Why sources weren't taken from the cache:";
            foreach (var group in misses)
            {
                yield return Invariant($"  {group.Count(),5}  {group.Key}");
            }
        }
    }

    /// <summary>One line per source, for <c>--verbose</c>.</summary>
    public IEnumerable<string> Details()
    {
        foreach (var file in Files)
        {
            string phases = string.Join(", ", file.Phases.Select(phase => Invariant($"{phase.Phase} {Seconds(phase.Elapsed)}")));
            string miss = file.Miss is null ? "" : $" ({file.Miss})";
            yield return Invariant($"  {Outcome(file.Outcome),-10} {Seconds(file.Elapsed),9}  {file.Name}{miss}{(phases.Length > 0 ? $": {phases}" : "")}");
        }
    }

    internal static string Seconds(TimeSpan elapsed) => Invariant($"{elapsed.TotalSeconds:0.000} s");

    private static string Megabytes(long bytes) => Invariant($"{bytes / (1024.0 * 1024.0):0.0} MB");

    // The reason without the file it names, so misses of the same kind count together.
    private static string MissKind(string miss)
    {
        int colon = miss.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? miss : miss[..colon];
    }

    private static string Outcome(CookOutcome outcome) => outcome switch
    {
        CookOutcome.Cooked => "cooked",
        CookOutcome.FromCache => "cache",
        _ => "failed",
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Adds up time per named phase. Safe to use from several threads, as a cooker that runs steps at once does. Then a
/// phase adds up the time of each step in it, so it can be more than the wall clock.
/// </summary>
public sealed class PhaseClock
{
    private readonly List<(string Phase, TimeSpan Elapsed)> _phases = [];
    private readonly Lock _lock = new();

    internal PhaseClock()
    {
    }

    internal IReadOnlyList<(string Phase, TimeSpan Elapsed)> Phases
    {
        get
        {
            lock (_lock)
            {
                return [.. _phases];
            }
        }
    }

    internal Measure Start(string phase) => new(this, phase, Stopwatch.GetTimestamp());

    internal void Add(string phase, TimeSpan elapsed)
    {
        lock (_lock)
        {
            int index = _phases.FindIndex(existing => existing.Phase == phase);
            if (index < 0)
            {
                _phases.Add((phase, elapsed));
            }
            else
            {
                _phases[index] = (phase, _phases[index].Elapsed + elapsed);
            }
        }
    }

    /// <summary>Stops the clock for its phase when disposed.</summary>
    public readonly struct Measure(PhaseClock clock, string phase, long started) : IDisposable
    {
        public void Dispose() => clock?.Add(phase, Stopwatch.GetElapsedTime(started));
    }
}
