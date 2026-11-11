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
        var contents = new List<(string Folder, List<string> Only)>();
        string? output = null;
        string target = "desktop";
        string? cache = null;
        bool noCache = false;
        long? cacheLimit = null;
        string? devFile = null;
        bool watch = false;
        int workers = 0;
        bool timings = false;
        bool verbose = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--content":
                    contents.Add((Value(args, ref i), []));
                    break;
                case "--only":
                    // Trimmed, so Only="props/** ; textures/**" in a project works.
                    string pattern = Value(args, ref i).Trim();
                    if (contents.Count == 0)
                    {
                        throw new UsageException($"--only {pattern} needs a --content before it: it picks files from the folder just before it.");
                    }

                    // An empty one comes from a trailing semicolon, as in Only="props/**;".
                    if (pattern.Length > 0)
                    {
                        contents[^1].Only.Add(pattern);
                    }

                    break;
                case "--workers":
                    string count = Value(args, ref i);
                    if (!int.TryParse(count, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out workers) || workers < 1)
                    {
                        throw new UsageException($"--workers needs a whole number above 0, not {count}.");
                    }

                    break;
                case "--timings":
                    timings = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--out":
                    output = Value(args, ref i);
                    break;
                case "--target":
                    target = Value(args, ref i);
                    break;
                case "--cache":
                    cache = Value(args, ref i);
                    break;
                case "--no-cache":
                    noCache = true;
                    break;
                case "--cache-limit":
                    cacheLimit = Limit(Value(args, ref i));
                    break;
                case "--dev-file":
                    devFile = Value(args, ref i);
                    break;
                case "--watch":
                    watch = true;
                    break;
                default:
                    throw new UsageException($"Unknown option {args[i]}.");
            }
        }

        if (output is null)
        {
            throw new UsageException("--out is needed.");
        }

        if (cache is not null && noCache)
        {
            throw new UsageException("--cache and --no-cache can't be used together.");
        }

        var roots = Roots(contents);
        var reporting = Reporting.FromEnvironment(timings, verbose);
        if (cacheLimit is not null && noCache)
        {
            throw new UsageException("--cache-limit and --no-cache can't be used together.");
        }

        // Without --cache-limit the cook reads SUPER_CACHE_SIZE itself, only when it has a cache, and warns if it isn't a size.
        var options = new CookOptions(target, noCache ? null : cache ?? CookOptions.DefaultCacheFolder, workers)
        {
            CacheLimit = cacheLimit,
        };
        var pipeline = new CookPipeline(ModelCookers.All, options);
        var store = new FolderStore(output);
        bool succeeded = CookAndReport(pipeline, roots, store, reporting);

        if (devFile is not null)
        {
            var only = roots.Where(root => root.Only.Count > 0).ToDictionary(root => root.Label, root => root.Only.ToArray());
            new DevSettings([.. roots.Select(root => root.Label)], typeof(Program).Assembly.Location, target, only.Count > 0 ? only : null).Write(devFile);
        }

        if (!watch)
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
        var contents = new List<(string Folder, List<string> Only)>();
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--content")
            {
                contents.Add((Value(args, ref i), []));
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException($"Unknown option {args[i]}.");
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        if (positional.Count != 2)
        {
            throw new UsageException("file needs a name and an output path.");
        }

        string name = positional[0];
        string output = positional[1];
        var store = new MemoryStore();
        var pipeline = new CookPipeline(ModelCookers.All, new CookOptions(CacheFolder: CookOptions.DefaultCacheFolder));
        if (!CookAndReport(pipeline, Roots(contents), store, Reporting.FromEnvironment(false, false)))
        {
            return 1;
        }

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
        string? folder = null;
        bool clear = false;
        bool trim = false;
        long? limit = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cache":
                    folder = Value(args, ref i);
                    break;
                case "--clear":
                    clear = true;
                    break;
                case "--trim":
                    trim = true;
                    break;
                case "--cache-limit":
                    limit = Limit(Value(args, ref i));
                    break;
                default:
                    throw new UsageException($"Unknown option {args[i]}.");
            }
        }

        if (clear && trim)
        {
            throw new UsageException("--clear and --trim can't be used together.");
        }

        if (limit is not null && !trim)
        {
            throw new UsageException("--cache-limit only goes with --trim here.");
        }

        folder = Path.GetFullPath(folder ?? CookOptions.DefaultCacheFolder);
        if (clear)
        {
            try
            {
                var (files, bytes) = CacheFolder.Clear(folder);
                Console.WriteLine($"Cleared {folder}: deleted {files} {(files == 1 ? "file" : "files")}, {CacheSize.Format(bytes)}.");
                return 0;
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

        if (trim)
        {
            try
            {
                var result = CacheFolder.Trim(folder, limit ?? EnvironmentLimit());
                Console.WriteLine(result.Describe());
                if (result.Limit > 0 && result.Size > result.Limit)
                {
                    Console.WriteLine("Files from the last two hours are kept, which covers every file used in the last hour, in case a cook is using them. "
                        + "--clear deletes all of the cache's files.");
                }

                return 0;
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

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

        return 0;
    }

    /// <summary>A size given to <c>--cache-limit</c>.</summary>
    private static long Limit(string text)
    {
        return CacheSize.TryParse(text, out long bytes, out string? error) ? bytes : throw new UsageException($"--cache-limit: {error}");
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
        return result.Succeeded;
    }

    private static List<FolderRoot> Roots(List<(string Folder, List<string> Only)> contents)
    {
        if (contents.Count == 0)
        {
            throw new UsageException("--content is needed at least once.");
        }

        foreach (var (folder, _) in contents)
        {
            if (!Directory.Exists(folder))
            {
                throw new UsageException($"The content folder {Path.GetFullPath(folder)} doesn't exist.");
            }
        }

        return [.. contents.Select(content => new FolderRoot(content.Folder, content.Only))];
    }

    private static string Value(string[] args, ref int i)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new UsageException($"{args[i]} needs a value.");
        }

        i++;
        return args[i];
    }

    private sealed class UsageException(string message) : Exception(message);
}
