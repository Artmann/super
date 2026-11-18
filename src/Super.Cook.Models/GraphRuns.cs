using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Super.Cook.Materials;

namespace Super.Cook.Models;

/// <summary>One graph's images, packed into the pixels of its textures.</summary>
internal sealed record GraphRun(string Key, int Size, float Width, float HeightDepth, MaterialPixels Pixels);

/// <summary>A material's graph, and the detail graph it runs when that graph works.</summary>
internal sealed record WantedGraphs((string Path, int Size) Main, (string Path, int Size)? Detail);

/// <summary>The graphs a model's materials name, each run once per (path, size) however many materials or parts use it.</summary>
internal sealed class GraphRuns(CookInput input)
{
    /// <summary>
    /// How many of a model's graphs run at once. Each already runs its nodes in parallel, so a few at once is enough
    /// to fill the gaps, and each holds its float images while it runs, which at 4096 pixels are up to 256 MiB each.
    /// </summary>
    private const int AtOnce = 3;

    private readonly Dictionary<string, GraphRun?> _runs = new(StringComparer.Ordinal);

    /// <summary>Runs made by <see cref="RunAhead"/> that no material has asked for yet, with the errors they gave.</summary>
    private readonly Dictionary<string, (PackedImages? Packed, List<string> Errors)> _ahead = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs <paramref name="wanted"/> at once, before the materials ask for them one by one with <see cref="Run"/>. Their
    /// errors wait until then, so they're reported by the material that asks first, in the order the materials come.
    /// </summary>
    public void RunAhead(IReadOnlyList<WantedGraphs> wanted)
    {
        // One graph alone gains nothing by running ahead.
        if (FreshCount(wanted) < 2)
        {
            return;
        }

        // A graph two materials name runs once: the second waits for the first's.
        var runs = new ConcurrentDictionary<string, Lazy<(PackedImages? Packed, List<string> Errors)>>(StringComparer.Ordinal);

        // Each material's graphs, in the order the materials will ask for them.
        var failures = new Exception?[wanted.Count];
        Parallel.For(0, wanted.Count, new ParallelOptions { MaxDegreeOfParallelism = AtOnce }, i =>
        {
            try
            {
                RunMaterial(wanted[i], runs);
            }
            catch (Exception exception)
            {
                failures[i] = exception;
            }
        });

        foreach (var (key, run) in runs)
        {
            if (run.IsValueCreated)
            {
                _ahead[key] = run.Value;
            }
        }

        // The first material's failure is the one the materials would have met asking one by one. The pipeline tells
        // a locked file from other failures by the exception itself, so it's thrown as it was.
        if (failures.FirstOrDefault(failure => failure is not null) is { } first)
        {
            ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    /// <returns>The run, or null after reporting why there is none to <paramref name="error"/>.</returns>
    public GraphRun? Run(string path, int size, Action<string> error, Action<string> warning)
    {
        path = path.Replace('\\', '/');
        string key = $"{path}@{size}";
        if (_runs.TryGetValue(key, out var known))
        {
            return known;
        }

        PackedImages? made;
        if (_ahead.Remove(key, out var ahead))
        {
            foreach (string message in ahead.Errors)
            {
                error(message);
            }

            made = ahead.Packed;
        }
        else
        {
            made = MaterialGraphs.RunPacked(input, path, size, error);
        }

        GraphRun? run = null;
        if (made is { } packed)
        {
            foreach (string part in packed.Pixels.NotNumbers)
            {
                warning($"{path}: {part} has pixels that aren't numbers. They were written as 0.");
            }

            run = new GraphRun(key, packed.Size, packed.Width, packed.HeightDepth, packed.Pixels);
        }

        _runs[key] = run;
        return run;
    }

    private static string KeyOf((string Path, int Size) run) => $"{run.Path.Replace('\\', '/')}@{run.Size}";

    /// <summary>How many different graphs <paramref name="wanted"/> names that haven't run.</summary>
    private int FreshCount(IReadOnlyList<WantedGraphs> wanted) => wanted
        .SelectMany(item => item.Detail is { } detail ? new[] { item.Main, detail } : [item.Main])
        .Where(Fresh)
        .Select(KeyOf)
        .Distinct(StringComparer.Ordinal)
        .Count();

    /// <summary>Whether a graph has neither run for a material nor run ahead.</summary>
    private bool Fresh((string Path, int Size) run) => !_runs.ContainsKey(KeyOf(run)) && !_ahead.ContainsKey(KeyOf(run));

    /// <summary>
    /// Runs one material's graphs ahead. A detail graph only runs when its material's main graph worked, as it would
    /// when the material asked.
    /// </summary>
    private void RunMaterial(WantedGraphs wanted, ConcurrentDictionary<string, Lazy<(PackedImages? Packed, List<string> Errors)>> runs)
    {
        if (!Fresh(wanted.Main))
        {
            return;
        }

        if (Make(wanted.Main, runs).Packed is not null && wanted.Detail is { } detail && Fresh(detail))
        {
            Make(detail, runs);
        }
    }

    private (PackedImages? Packed, List<string> Errors) Make(
        (string Path, int Size) run, ConcurrentDictionary<string, Lazy<(PackedImages? Packed, List<string> Errors)>> runs)
    {
        string path = run.Path.Replace('\\', '/');
        return runs.GetOrAdd(KeyOf(run), _ => new Lazy<(PackedImages?, List<string>)>(() =>
        {
            var errors = new List<string>();
            var packed = MaterialGraphs.RunPacked(input, path, run.Size, message =>
            {
                lock (errors)
                {
                    errors.Add(message);
                }
            });
            return (packed, errors);
        })).Value;
    }
}
