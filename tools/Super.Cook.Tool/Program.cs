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
          super-cook --content <dir> [--content <dir> ...] --out <dir> [--target desktop] [--cache <dir> | --no-cache] [--dev-file <path>] [--watch]
                     [--workers <n>] [--timings] [--verbose]
          super-cook file --content <dir> [--content <dir> ...] <name> <output path>

        --timings prints where the time went: each step, each phase of the cookers, the slowest sources
        and why sources weren't taken from the cache. --verbose adds one line per source.
        SUPER_COOK_TIMINGS=1 or SUPER_COOK_VERBOSE=1 do the same, for a cook started by a build or a game.
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
            return args.Length > 0 && args[0] == "file" ? CookOne(args[1..]) : CookAll(args);
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
        var contents = new List<string>();
        string? output = null;
        string target = "desktop";
        string? cache = null;
        bool noCache = false;
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
                    contents.Add(Value(args, ref i));
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
        var pipeline = new CookPipeline(ModelCookers.All, new CookOptions(target, noCache ? null : cache ?? CookOptions.DefaultCacheFolder, workers));
        var store = new FolderStore(output);
        bool succeeded = CookAndReport(pipeline, roots, store, reporting);

        if (devFile is not null)
        {
            new DevSettings([.. roots.Select(root => root.Label)], typeof(Program).Assembly.Location, target).Write(devFile);
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
        var contents = new List<string>();
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--content")
            {
                contents.Add(Value(args, ref i));
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

        File.WriteAllBytes(output, store.Get(entry.Hash));
        return 0;
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

    private static List<FolderRoot> Roots(List<string> contents)
    {
        if (contents.Count == 0)
        {
            throw new UsageException("--content is needed at least once.");
        }

        foreach (string content in contents)
        {
            if (!Directory.Exists(content))
            {
                throw new UsageException($"The content folder {Path.GetFullPath(content)} doesn't exist.");
            }
        }

        return [.. contents.Select(content => new FolderRoot(content))];
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
