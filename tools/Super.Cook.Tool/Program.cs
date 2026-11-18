using Super;
using Super.Cook;
using Super.Cook.Models;

namespace Super.Cook.Tool;

/// <summary>
/// <c>super-cook</c>: cooks content roots into a content folder, for builds, scripts and a game run
/// from source. Problems print one per line in the format MSBuild and editors link to the file.
/// </summary>
public static class Program
{
    private const string Usage = """
        Usage:
          super-cook --content <dir> [--only <glob> ...] [--content <dir> ...] --out <dir> [--target desktop] [--cache <dir> | --no-cache] [--dev-file <path>] [--watch]
                     [--cache-limit <size>] [--workers <n>] [--timings] [--verbose]
          super-cook file --content <dir> [--content <dir> ...] <name> <output path>
          super-cook cache [--cache <dir>] [--clear | --trim] [--cache-limit <size>]

        --only cooks just the files of the --content before it that match the glob, such as props/**. Give it
        several times for several globs. The other files stay readable, so a model still finds its textures.
        --cache-limit is the most the cache keeps after a cook, such as 8GB, 500MB or none; the least recently
        used files go first. It's 4GB unless SUPER_CACHE_SIZE says otherwise. What a cook uses is always kept.
        --timings prints where the time went: each step, each phase of the cookers, the slowest sources
        and why sources weren't taken from the cache. --verbose adds one line per source.
        SUPER_COOK_TIMINGS=1 or SUPER_COOK_VERBOSE=1 do the same, for a cook started by a build or a game.
        cache prints where the cache is and how big each part of it is. --clear deletes it, and --trim
        deletes the least recently used files until it's under its limit.
        """;

    /// <summary>What to print besides the problems and the totals.</summary>
    private sealed record Reporting(bool Timings, bool Verbose)
    {
        public static Reporting FromEnvironment(bool timings, bool verbose) => new(
            timings || verbose || IsSet("SUPER_COOK_TIMINGS") || IsSet("SUPER_COOK_VERBOSE"),
            verbose || IsSet("SUPER_COOK_VERBOSE"));

        private static bool IsSet(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value && value != "0";
    }

    /// <summary>How long the roots must be quiet before watch mode cooks. Editors save in several steps.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(100);

    public static int Main(string[] args)
    {
        // Console.Out flushes every write, so a game running this as a child sees each line as it's written.
        try
        {
            return args.Length > 0 && args[0] == "file" ? CookOne(args[1..])
                : args.Length > 0 && args[0] == "cache" ? Cache(args[1..])
                : CookAll(args);
        }
        catch (UsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(Usage);
            return 1;
        }
    }

    private static int CookAll(string[] args)
    {
        var arguments = CookArguments.Parse(args);
        var roots = arguments.Roots;
        var pipeline = new CookPipeline(ModelCookers.All, arguments.ToCookOptions());
        var store = new FolderStore(arguments.Output!);
        bool succeeded = CookAndReport(pipeline, roots, store, Reporting.FromEnvironment(arguments.Timings, arguments.Verbose));

        if (arguments.DevFile is not null)
        {
            var only = roots.Where(root => root.Only.Count > 0).ToDictionary(root => root.Label, root => root.Only.ToArray());
            new DevSettings([.. roots.Select(root => root.Label)], typeof(Program).Assembly.Location, arguments.Target, only.Count > 0 ? only : null)
                .Write(arguments.DevFile);
        }

        if (!arguments.Watch)
        {
            return succeeded ? 0 : 1;
        }

        using (new CookWatcher(pipeline, roots, store, Console.Out, Quiet))
        {
            Console.WriteLine($"Watching {roots.Count} content {(roots.Count == 1 ? "root" : "roots")}. Close stdin to stop.");

            // The game that started this owns stdin. When it closes, or the game ends, so does this.
            Console.In.ReadToEnd();
        }

        return 0;
    }

    /// <summary>Cooks the roots and writes the bytes of one asset or part to a file.</summary>
    private static int CookOne(string[] args)
    {
        var arguments = FileArguments.Parse(args);
        var store = new MemoryStore();
        var pipeline = new CookPipeline(ModelCookers.All, new CookOptions(CacheFolder: CookOptions.DefaultCacheFolder));
        if (!CookAndReport(pipeline, arguments.Roots, store, Reporting.FromEnvironment(false, false)))
        {
            return 1;
        }

        return WriteAsset(store, arguments.Name, arguments.Output);
    }

    /// <summary>Writes the bytes of one cooked asset or part to a file. Fails when nothing has that name.</summary>
    private static int WriteAsset(MemoryStore store, string name, string output)
    {
        if (!store.Manifest.Assets.TryGetValue(name, out var entry))
        {
            var parts = store.Manifest.Assets.Keys
                .Where(asset => asset.StartsWith(name + "#", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToList();
            Console.WriteLine(parts.Count > 0
                ? $"super-cook: error SUPER001: {name} is cooked in parts. Ask for one of: {string.Join(", ", parts)}."
                : $"super-cook: error SUPER001: Nothing cooked is named {name}. A name is the path from its root, such as items/axe.json, or models/fox.glb#part for a part.");
            return 1;
        }

        string? folder = Path.GetDirectoryName(Path.GetFullPath(output));
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
        }

        // A model alone in a file has nowhere to find shared textures, so they go inside it.
        File.WriteAllBytes(output, entry.Kind == ContentKind.Model ? ModelCookers.StandAlone(store, entry.Hash, name) : store.Get(entry.Hash));
        return 0;
    }

    /// <summary><c>super-cook cache</c>: shows the cache's folder and size, or clears or trims it.</summary>
    private static int Cache(string[] args)
    {
        var arguments = CacheArguments.Parse(args);
        string folder = Path.GetFullPath(arguments.Folder ?? CookOptions.DefaultCacheFolder);
        if (arguments.Clear)
        {
            return ChangeCache(() => ClearCache(folder));
        }

        if (arguments.Trim)
        {
            return ChangeCache(() => TrimCache(folder, arguments.Limit));
        }

        ShowCache(folder);
        return 0;
    }

    /// <summary>Runs a change to the cache. When it refuses, such as for a folder that isn't a cache, it prints why and fails.</summary>
    private static int ChangeCache(Action change)
    {
        try
        {
            change();
            return 0;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void ClearCache(string folder)
    {
        var (files, bytes) = CacheFolder.Clear(folder);
        Console.WriteLine($"Cleared {folder}: deleted {files} {(files == 1 ? "file" : "files")}, {CacheSize.Format(bytes)}.");
    }

    private static void TrimCache(string folder, long? limit)
    {
        var result = CacheFolder.Trim(folder, limit ?? EnvironmentLimit());
        Console.WriteLine(result.Describe());
        if (result.Limit > 0 && result.Size > result.Limit)
        {
            Console.WriteLine("Files from the last two hours are kept, which covers every file used in the last hour, in case a cook is using them. "
                + "--clear deletes all of the cache's files.");
        }
    }

    private static void ShowCache(string folder)
    {
        var parts = CacheFolder.Parts(folder);
        var cacheParts = parts.Where(part => part.Part != CacheFolder.NotTheCaches).ToList();
        long total = cacheParts.Sum(part => part.Size);
        long cacheLimit = EnvironmentLimit();
        Console.WriteLine(folder);
        Console.WriteLine($"{CacheSize.Format(total)} in {cacheParts.Sum(part => part.Files)} files, limit {(cacheLimit > 0 ? CacheSize.Format(cacheLimit) : "none")}.");
        foreach (var (part, size, files) in parts)
        {
            Console.WriteLine(FormattableString.Invariant($"  {part,-28} {CacheSize.Format(size),9}  {files,6} {(files == 1 ? "file" : "files")}"));
        }
    }

    /// <summary>
    /// The limit in <c>SUPER_CACHE_SIZE</c>, or the default. A value that isn't a size warns and gives the default, as a
    /// cook does.
    /// </summary>
    private static long EnvironmentLimit()
    {
        long limit = CookOptions.DefaultCacheLimitOrError(out string? error);
        if (error is not null)
        {
            Console.Error.WriteLine($"super-cook: warning SUPER001: {error} Using the default, {CacheSize.Format(CacheSize.Default)}.");
        }

        return limit;
    }

    /// <summary>Cooks once and prints every problem, then the totals. Returns whether it succeeded.</summary>
    private static bool CookAndReport(CookPipeline pipeline, IReadOnlyList<FolderRoot> roots, ICookedStore store, Reporting reporting)
    {
        CookResult result;
        try
        {
            result = pipeline.Cook(roots, store);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file held open by another program, or an output folder that can't be written.
            Console.WriteLine($"super-cook: error SUPER001: {exception.Message}");
            Console.WriteLine("Cooked 0, from cache 0, 1 errors.");
            return false;
        }

        Report(result, reporting);
        return result.Succeeded;
    }

    private static void Report(CookResult result, Reporting reporting)
    {
        // Notes first, so the warnings and errors sit next to the totals. A note has no "warning" in it, so MSBuild shows it as a message.
        foreach (var note in result.Notes)
        {
            Console.WriteLine(note.ToString());
        }

        foreach (var problem in result.Problems)
        {
            Console.WriteLine(problem.ToString());
        }

        if (reporting.Verbose)
        {
            Console.WriteLine("Each source:");
            foreach (string line in result.Report.Details())
            {
                Console.WriteLine(line);
            }
        }

        if (reporting.Timings)
        {
            foreach (string line in result.Report.Summary())
            {
                Console.WriteLine(line);
            }
        }

        int errors = result.Problems.Count(problem => problem.IsError);
        Console.WriteLine(FormattableString.Invariant(
            $"Cooked {result.Cooked}, from cache {result.FromCache}, {errors} errors, in {result.Report.Elapsed.TotalSeconds:0.0} s."));
    }
}
