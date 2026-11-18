using System.Globalization;

namespace Super.Cook.Tool;

/// <summary>A mistake in the command line. It prints with the usage.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>Reads a command line into options.</summary>
internal static class Arguments
{
    /// <summary>
    /// Reads <paramref name="args"/> into <paramref name="into"/>. A flag sets something on its own, and an option takes the
    /// next argument as its value. Anything else that doesn't start with <c>--</c> goes to <paramref name="positional"/>
    /// when there is one; the rest are unknown options.
    /// </summary>
    public static T Parse<T>(string[] args, T into, Dictionary<string, Action<T>> flags,
        Dictionary<string, Action<T, string>> options, Action<T, string>? positional = null)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (flags.TryGetValue(args[i], out var flag))
            {
                flag(into);
            }
            else if (options.TryGetValue(args[i], out var option))
            {
                option(into, Value(args, ref i));
            }
            else if (positional is not null && !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                positional(into, args[i]);
            }
            else
            {
                throw new UsageException($"Unknown option {args[i]}.");
            }
        }

        return into;
    }

    /// <summary>A size given to <c>--cache-limit</c>.</summary>
    public static long Limit(string text)
    {
        return CacheSize.TryParse(text, out long bytes, out string? error) ? bytes : throw new UsageException($"--cache-limit: {error}");
    }

    /// <summary>The folders given to <c>--content</c>, which must exist, with the globs given to <c>--only</c> after each.</summary>
    public static List<FolderRoot> Roots(List<(string Folder, List<string> Only)> contents)
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
}

/// <summary>The options of <c>super-cook</c> when it cooks content roots into a content folder.</summary>
internal sealed class CookArguments
{
    private static readonly Dictionary<string, Action<CookArguments>> Flags = new(StringComparer.Ordinal)
    {
        ["--timings"] = parsed => parsed.Timings = true,
        ["--verbose"] = parsed => parsed.Verbose = true,
        ["--no-cache"] = parsed => parsed._noCache = true,
        ["--watch"] = parsed => parsed.Watch = true,
    };

    private static readonly Dictionary<string, Action<CookArguments, string>> Options = new(StringComparer.Ordinal)
    {
        ["--content"] = (parsed, folder) => parsed._contents.Add((folder, [])),
        ["--only"] = (parsed, pattern) => parsed.AddOnly(pattern),
        ["--workers"] = (parsed, count) => parsed._workers = Workers(count),
        ["--out"] = (parsed, folder) => parsed.Output = folder,
        ["--target"] = (parsed, target) => parsed.Target = target,
        ["--cache"] = (parsed, folder) => parsed._cache = folder,
        ["--cache-limit"] = (parsed, size) => parsed._cacheLimit = Arguments.Limit(size),
        ["--dev-file"] = (parsed, path) => parsed.DevFile = path,
    };

    private readonly List<(string Folder, List<string> Only)> _contents = [];
    private string? _cache;
    private bool _noCache;
    private long? _cacheLimit;
    private int _workers;

    /// <summary>The content folder to cook into. <see cref="Parse"/> makes sure it's set.</summary>
    public string? Output { get; private set; }

    public string Target { get; private set; } = "desktop";

    public string? DevFile { get; private set; }

    public bool Watch { get; private set; }

    public bool Timings { get; private set; }

    public bool Verbose { get; private set; }

    public List<FolderRoot> Roots { get; private set; } = [];

    /// <exception cref="UsageException">An option is unknown or misses its value, or two options don't go together.</exception>
    public static CookArguments Parse(string[] args)
    {
        var parsed = Arguments.Parse(args, new CookArguments(), Flags, Options);
        if (parsed.Output is null)
        {
            throw new UsageException("--out is needed.");
        }

        if (parsed._cache is not null && parsed._noCache)
        {
            throw new UsageException("--cache and --no-cache can't be used together.");
        }

        parsed.Roots = Arguments.Roots(parsed._contents);
        if (parsed._cacheLimit is not null && parsed._noCache)
        {
            throw new UsageException("--cache-limit and --no-cache can't be used together.");
        }

        return parsed;
    }

    /// <summary>
    /// The cook's options. Without --cache-limit the cook reads SUPER_CACHE_SIZE itself, only when it has a cache, and warns
    /// if it isn't a size.
    /// </summary>
    public CookOptions ToCookOptions() =>
        new(Target, _noCache ? null : _cache ?? CookOptions.DefaultCacheFolder, _workers) { CacheLimit = _cacheLimit };

    private void AddOnly(string value)
    {
        // Trimmed, so Only="props/** ; textures/**" in a project works.
        string pattern = value.Trim();
        if (_contents.Count == 0)
        {
            throw new UsageException($"--only {pattern} needs a --content before it: it picks files from the folder just before it.");
        }

        // An empty one comes from a trailing semicolon, as in Only="props/**;".
        if (pattern.Length > 0)
        {
            _contents[^1].Only.Add(pattern);
        }
    }

    private static int Workers(string count)
    {
        if (!int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int workers) || workers < 1)
        {
            throw new UsageException($"--workers needs a whole number above 0, not {count}.");
        }

        return workers;
    }
}

/// <summary>The options of <c>super-cook file</c>: the content roots, then the name to write and where to write it.</summary>
internal sealed class FileArguments
{
    private static readonly Dictionary<string, Action<FileArguments>> Flags = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, Action<FileArguments, string>> Options = new(StringComparer.Ordinal)
    {
        ["--content"] = (parsed, folder) => parsed._contents.Add((folder, [])),
    };

    private readonly List<(string Folder, List<string> Only)> _contents = [];
    private readonly List<string> _positional = [];

    /// <summary>The asset, or asset and part, to write.</summary>
    public string Name => _positional[0];

    /// <summary>The file to write it to.</summary>
    public string Output => _positional[1];

    public List<FolderRoot> Roots { get; private set; } = [];

    /// <exception cref="UsageException">An option is unknown or misses its value, or the name or output path is missing.</exception>
    public static FileArguments Parse(string[] args)
    {
        var parsed = Arguments.Parse(args, new FileArguments(), Flags, Options, (into, value) => into._positional.Add(value));
        if (parsed._positional.Count != 2)
        {
            throw new UsageException("file needs a name and an output path.");
        }

        parsed.Roots = Arguments.Roots(parsed._contents);
        return parsed;
    }
}

/// <summary>The options of <c>super-cook cache</c>.</summary>
internal sealed class CacheArguments
{
    private static readonly Dictionary<string, Action<CacheArguments>> Flags = new(StringComparer.Ordinal)
    {
        ["--clear"] = parsed => parsed.Clear = true,
        ["--trim"] = parsed => parsed.Trim = true,
    };

    private static readonly Dictionary<string, Action<CacheArguments, string>> Options = new(StringComparer.Ordinal)
    {
        ["--cache"] = (parsed, folder) => parsed.Folder = folder,
        ["--cache-limit"] = (parsed, size) => parsed.Limit = Arguments.Limit(size),
    };

    /// <summary>The cache folder given, or null for the default.</summary>
    public string? Folder { get; private set; }

    public bool Clear { get; private set; }

    public bool Trim { get; private set; }

    /// <summary>The limit to trim to, or null for the one in <c>SUPER_CACHE_SIZE</c>.</summary>
    public long? Limit { get; private set; }

    /// <exception cref="UsageException">An option is unknown or misses its value, or two options don't go together.</exception>
    public static CacheArguments Parse(string[] args)
    {
        var parsed = Arguments.Parse(args, new CacheArguments(), Flags, Options);
        if (parsed.Clear && parsed.Trim)
        {
            throw new UsageException("--clear and --trim can't be used together.");
        }

        if (parsed.Limit is not null && !parsed.Trim)
        {
            throw new UsageException("--cache-limit only goes with --trim here.");
        }

        return parsed;
    }
}
