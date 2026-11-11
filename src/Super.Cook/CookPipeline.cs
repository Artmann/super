using System.Runtime.ExceptionServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Super.Cook;

/// <param name="Target">The platform the content is cooked for.</param>
/// <param name="CacheFolder">Where cooked results are kept between runs. Null means no cache.</param>
/// <param name="Workers">How many sources cook at once. Zero means one per processor, up to <see cref="MaxDefaultWorkers"/>.</param>
public sealed record CookOptions(string Target = "desktop", string? CacheFolder = null, int Workers = 0)
{
    /// <summary>
    /// The most sources cooked at once when <see cref="Workers"/> isn't set. Each one holds its decoded
    /// textures, which can be hundreds of megabytes for a big model, so more workers than this cost
    /// memory without saving much time.
    /// </summary>
    public const int MaxDefaultWorkers = 8;

    /// <summary>The folder in <c>SUPER_CACHE</c>, or <c>~/.super/cache</c> when that isn't set.</summary>
    public static string DefaultCacheFolder
    {
        get
        {
            string? set = Environment.GetEnvironmentVariable("SUPER_CACHE");
            return string.IsNullOrEmpty(set)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".super", "cache")
                : set;
        }
    }

    /// <summary>
    /// The most bytes the cache may hold after a cook, least recently used files deleted first, or 0 for no limit.
    /// Null means <see cref="DefaultCacheLimit"/>.
    /// </summary>
    public long? CacheLimit { get; init; }

    /// <summary>
    /// The limit in <c>SUPER_CACHE_SIZE</c>, such as <c>8GB</c> or <c>none</c>, or <see cref="CacheSize.Default"/> when that
    /// isn't set. A value that isn't a size gives the default, and <paramref name="error"/> says what's wrong with it.
    /// </summary>
    public static long DefaultCacheLimitOrError(out string? error)
    {
        error = null;
        string? set = Environment.GetEnvironmentVariable("SUPER_CACHE_SIZE");
        if (string.IsNullOrWhiteSpace(set))
        {
            return CacheSize.Default;
        }

        if (CacheSize.TryParse(set, out long bytes, out string? problem))
        {
            return bytes;
        }

        error = $"SUPER_CACHE_SIZE: {problem}";
        return CacheSize.Default;
    }

    /// <summary>The limit in <c>SUPER_CACHE_SIZE</c>, or <see cref="CacheSize.Default"/> when that isn't set or isn't a size.</summary>
    public static long DefaultCacheLimit => DefaultCacheLimitOrError(out _);

    internal int WorkerCount => Workers > 0 ? Workers : Math.Clamp(Environment.ProcessorCount, 1, MaxDefaultWorkers);
}

public sealed record CookResult(IReadOnlyList<CookProblem> Problems, int Cooked, int FromCache)
{
    public bool Succeeded => !Problems.Any(p => p.IsError);

    /// <summary>Where the time went.</summary>
    public CookReport Report { get; init; } = CookReport.Empty;
}

/// <summary>
/// Cooks every source in some roots into a store. The store only gets a new manifest when nothing went wrong.
/// Sources cook in parallel, so a cooker's <see cref="ICooker.Cook"/> may run for several sources at once.
/// Problems and names come out in source order whatever order the cooks finish in.
/// </summary>
public sealed class CookPipeline(IReadOnlyList<ICooker> cookers, CookOptions options)
{
    /// <summary>A source to cook, with everything worked out before the cooking starts.</summary>
    private sealed record Job(SourceFile File, ICooker Cooker, JsonObject Settings);

    /// <summary>What cooking one source gave: its problems and its assets, already in the store.</summary>
    private sealed class Done
    {
        public List<CookProblem> Problems { get; } = [];

        public List<(string? Part, ManifestEntry Entry)> Assets { get; } = [];

        /// <summary>The hashes of the objects its assets point to, already in the store.</summary>
        public List<string> Objects { get; } = [];

        public CookedFile? Report { get; set; }
    }

    /// <exception cref="IOException">A source is open in another program, or the store couldn't be written. Nothing was committed, so trying again is safe.</exception>
    public CookResult Cook(IReadOnlyList<ISourceRoot> roots, ICookedStore store)
    {
        long started = Stopwatch.GetTimestamp();
        var steps = new List<(string, TimeSpan)>();
        long step = started;
        void EndStep(string name)
        {
            long now = Stopwatch.GetTimestamp();
            steps.Add((name, Stopwatch.GetElapsedTime(step, now)));
            step = now;
        }

        // Each root is listed once for the whole cook.
        IReadOnlyList<ISourceRoot> listed = [.. roots.Select(root => root as ListedRoot ?? new ListedRoot(root))];
        EndStep("list sources");

        var settings = new CookSettings(listed, cookers.Select(cooker => cooker.Id).ToHashSet(StringComparer.Ordinal));
        var (files, gatherProblems) = Sources.Gather(listed);
        var problems = new List<CookProblem>(settings.Problems);
        problems.AddRange(gatherProblems);

        var jobs = new List<Job>();
        foreach (var file in files)
        {
            var cooker = FindCooker(file.Name);
            if (cooker is null)
            {
                // The Cook flag doesn't depend on the cooker id, so any id will do.
                if (settings.For(file, "").Cook)
                {
                    problems.Add(new CookProblem(file.Describe(), null,
                        $"{file.Name} has no cooker. Add a rule with \"cook\": false if it isn't an asset.", true));
                }

                continue;
            }

            var (cook, values) = settings.For(file, cooker.Id);
            if (cook)
            {
                jobs.Add(new Job(file, cooker, values));
            }
        }

        EndStep("read settings");

        // One note of what this cook uses, shared by the record and step caches, so trimming keeps all of it.
        var usage = new CacheUsage(DateTime.UtcNow);
        var cache = options.CacheFolder is { } cacheFolder ? new CookCache(cacheFolder, usage) : null;
        var stepCache = options.CacheFolder is { } stepFolder ? new StepCache(stepFolder, usage) : null;
        var writer = new StoreWriter(store);
        var done = new Done[jobs.Count];
        int workers = Math.Max(1, Math.Min(options.WorkerCount, jobs.Count));
        try
        {
            Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = workers },
                i => done[i] = CookOne(jobs[i], cache, stepCache, writer));
        }
        catch (AggregateException aggregate) when (aggregate.InnerExceptions.Count > 0)
        {
            // Callers tell a locked file from other failures by the exception itself, as when cooking one at a time.
            ExceptionDispatchInfo.Capture(aggregate.InnerExceptions[0]).Throw();
            throw;
        }
        EndStep("cook");

        cache?.Flush();
        EndStep("save cache index");

        CacheTrim? trim = null;
        if (cache is not null)
        {
            string? limitError = null;
            long limit = options.CacheLimit ?? CookOptions.DefaultCacheLimitOrError(out limitError);
            if (limitError is not null)
            {
                problems.Add(new CookProblem(cache.Folder, null, $"{limitError} Using the default, {CacheSize.Format(CacheSize.Default)}.", false));
            }

            trim = cache.Trim(limit);
            if (trim.Warning() is { } warning)
            {
                problems.Add(new CookProblem(cache.Folder, null, warning, false));
            }

            EndStep("trim cache");
        }

        var assets = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        var objects = new HashSet<string>(StringComparer.Ordinal);

        // The source each name came from, so a name made twice can name both.
        var madeBy = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
        int cooked = 0;
        int fromCache = 0;
        for (int i = 0; i < jobs.Count; i++)
        {
            var file = jobs[i].File;
            problems.AddRange(done[i].Problems);
            objects.UnionWith(done[i].Objects);
            foreach (var (part, entry) in done[i].Assets)
            {
                string name = part is null ? file.Name : $"{file.Name}#{part}";
                if (madeBy.TryGetValue(name, out var first))
                {
                    problems.Add(new CookProblem(file.Describe(), null, first == file
                        ? $"{file.Name} makes {name} twice."
                        : $"{name} is made by both {first.Describe()} and {file.Describe()}. A name must come from one place.", true));
                    continue;
                }

                madeBy[name] = file;
                assets[name] = entry;
            }

            switch (done[i].Report!.Outcome)
            {
                case CookOutcome.Cooked:
                    cooked++;
                    break;
                case CookOutcome.FromCache:
                    fromCache++;
                    break;
            }
        }

        if (!problems.Any(problem => problem.IsError))
        {
            store.Commit(new ContentManifest(options.Target, assets, objects));
        }

        EndStep("write manifest");

        var report = new CookReport(Stopwatch.GetElapsedTime(started), steps, [.. done.Select(d => d.Report!)], workers, false) { Trim = trim };
        return new CookResult(problems, cooked, fromCache) { Report = report };
    }

    /// <summary>Takes one source from the cache, or cooks it and puts it in the cache. Runs on a worker thread.</summary>
    private Done CookOne(Job job, CookCache? cache, StepCache? stepCache, StoreWriter writer)
    {
        var (file, cooker, values) = job;
        var done = new Done();
        long started = Stopwatch.GetTimestamp();
        var clock = new PhaseClock();

        string? key = null;
        string? miss = null;
        if (cache is not null)
        {
            string? sourceHash;
            using (clock.Start("hash source"))
            {
                sourceHash = cache.SourceHash(file);
            }

            if (sourceHash is null)
            {
                miss = "source couldn't be hashed";
            }
            else
            {
                key = CookCache.Key(cooker.Id, cooker.Version, options.Target, values, sourceHash);
                CacheHit? hit;
                using (clock.Start("restore from cache"))
                {
                    hit = cache.TryRestore(key, file, writer, out miss);
                }

                if (hit is not null)
                {
                    foreach (var (message, line) in hit.Warnings)
                    {
                        done.Problems.Add(new CookProblem(file.Describe(), line, message, false));
                    }

                    long size = 0;
                    foreach (var restored in hit.Outputs)
                    {
                        done.Assets.Add((restored.Part, new ManifestEntry(restored.Kind, restored.Hash, restored.Size)));
                        size += restored.Size;
                    }

                    foreach (var (hash, objectSize) in hit.Objects)
                    {
                        done.Objects.Add(hash);
                        size += objectSize;
                    }

                    done.Report = new CookedFile(file.Name, cooker.Id, CookOutcome.FromCache, Stopwatch.GetElapsedTime(started), clock.Phases, null, size);
                    return done;
                }
            }
        }

        var output = new CookOutput();
        CookInput? cookInput = null;
        try
        {
            byte[] bytes;
            using (clock.Start("read source"))
            {
                bytes = file.Root.Read(file.Name);
            }

            cookInput = new CookInput(file, bytes, values, options.Target, stepCache);
            long cooking = Stopwatch.GetTimestamp();
            try
            {
                cooker.Cook(cookInput, output);
            }
            finally
            {
                // The cooker's own phases, then whatever it didn't time.
                var inside = TimeSpan.Zero;
                foreach (var (phase, elapsed) in cookInput.Clock.Phases)
                {
                    clock.Add(phase, elapsed);
                    inside += elapsed;
                }

                var rest = Stopwatch.GetElapsedTime(cooking) - inside;
                if (rest > TimeSpan.Zero)
                {
                    clock.Add(cookInput.Clock.Phases.Count == 0 ? $"{cooker.Id} cooker" : $"{cooker.Id} cooker, untimed", rest);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !InUse(exception))
        {
            output.Error(exception.Message);
        }

        foreach (var (message, line, isError) in output.Problems)
        {
            done.Problems.Add(new CookProblem(file.Describe(), line, message, isError));
        }

        if (output.Problems.Any(problem => problem.IsError))
        {
            done.Report = new CookedFile(file.Name, cooker.Id, CookOutcome.Failed, Stopwatch.GetElapsedTime(started), clock.Phases, miss, 0);
            return done;
        }

        var hashes = new string[output.Assets.Count];
        long outputBytes = 0;
        using (clock.Start("hash output"))
        {
            for (int i = 0; i < output.Assets.Count; i++)
            {
                hashes[i] = Convert.ToHexStringLower(SHA256.HashData(output.Assets[i].Bytes));
            }
        }

        // The cache first, so the store can link to the cache's copy rather than keep the bytes a second time.
        if (key is not null && cookInput is not null)
        {
            using (clock.Start("write cache"))
            {
                cache!.Save(key, file, cookInput.Inputs, output.Assets, hashes, output.Objects,
                    output.Problems.Select(problem => (problem.Message, problem.Line)).ToList());
            }
        }

        using (clock.Start("write store"))
        {
            for (int i = 0; i < output.Assets.Count; i++)
            {
                var (part, kind, bytes) = output.Assets[i];
                writer.Put(hashes[i], bytes, cache?.SavedObject(hashes[i]));
                done.Assets.Add((part, new ManifestEntry(kind, hashes[i], bytes.LongLength)));
                outputBytes += bytes.LongLength;
            }

            // Shared objects, such as textures, are already hashed, and most are in the store from another source.
            foreach (var (hash, bytes) in output.Objects)
            {
                writer.Put(hash, bytes, cache?.SavedObject(hash));
                done.Objects.Add(hash);
                outputBytes += bytes.LongLength;
            }
        }

        done.Report = new CookedFile(file.Name, cooker.Id, CookOutcome.Cooked, Stopwatch.GetElapsedTime(started), clock.Phases, miss, outputBytes);
        return done;
    }

    /// <summary>
    /// Whether a file couldn't be read because another program has it open. That isn't a mistake in
    /// the source, so it isn't reported as one: the <see cref="IOException"/> leaves
    /// <see cref="Cook"/> and the caller can try again.
    /// </summary>
    /// <remarks>
    /// Windows only: it knows Windows' sharing and lock violations. Other systems don't lock files against reading.
    /// A manifest that can't be replaced because the game has it open comes as access denied; <see cref="FolderStore"/>
    /// turns that into a <see cref="FileInUseException"/>, which carries a sharing violation.
    /// </remarks>
    internal static bool InUse(Exception exception)
    {
        const int SharingViolation = 32;
        const int LockViolation = 33;
        return exception is IOException && (exception.HResult & 0xFFFF) is SharingViolation or LockViolation;
    }

    private ICooker? FindCooker(string name)
    {
        ICooker? best = null;
        int bestLength = 0;
        foreach (var cooker in cookers)
        {
            foreach (string extension in cooker.Extensions)
            {
                if (extension.Length > bestLength && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    best = cooker;
                    bestLength = extension.Length;
                }
            }
        }

        return best;
    }
}

/// <summary>
/// Lets several workers put objects into one store. Each object is written once, however many sources make it,
/// and two workers never touch the same object at the same time. Different objects are written at once, which
/// <see cref="ICookedStore"/> allows.
/// </summary>
internal sealed class StoreWriter(ICookedStore store)
{
    private readonly ConcurrentDictionary<string, object> _gates = new(StringComparer.Ordinal);

    /// <summary>Puts the object in the store unless it's already there.</summary>
    /// <param name="linkFrom">A file that already holds the bytes, such as the cache's copy, for the store to link to.</param>
    public void Put(string hash, byte[] bytes, string? linkFrom = null) => PutOnce(hash, () => bytes, linkFrom);

    /// <summary>
    /// Puts the object in the store unless it's already there, making its bytes only when it isn't. The store links to
    /// <paramref name="linkFrom"/> when it can, and is given the bytes when it can't.
    /// </summary>
    /// <returns>False when <paramref name="bytes"/> gave null, so nothing was put.</returns>
    public bool PutOnce(string hash, Func<byte[]?> bytes, string? linkFrom = null)
    {
        lock (_gates.GetOrAdd(hash, _ => new object()))
        {
            if (store.Has(hash))
            {
                return true;
            }

            byte[]? made = bytes();
            if (made is null)
            {
                return false;
            }

            if (linkFrom is null || !store.TryLink(hash, linkFrom))
            {
                store.Put(hash, made);
            }

            return true;
        }
    }
}
