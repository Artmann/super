using System.Security.Cryptography;

namespace Super.Cook;

/// <param name="Target">The platform the content is cooked for.</param>
/// <param name="CacheFolder">Where cooked results are kept between runs. Null means no cache.</param>
public sealed record CookOptions(string Target = "desktop", string? CacheFolder = null)
{
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
}

public sealed record CookResult(IReadOnlyList<CookProblem> Problems, int Cooked, int FromCache)
{
    public bool Succeeded => !Problems.Any(p => p.IsError);
}

/// <summary>Cooks every source in some roots into a store. The store only gets a new manifest when nothing went wrong.</summary>
public sealed class CookPipeline(IReadOnlyList<ICooker> cookers, CookOptions options)
{
    /// <exception cref="IOException">A source is open in another program, or the store couldn't be written. Nothing was committed, so trying again is safe.</exception>
    public CookResult Cook(IReadOnlyList<ISourceRoot> roots, ICookedStore store)
    {
        var settings = new CookSettings(roots, cookers.Select(cooker => cooker.Id).ToHashSet(StringComparer.Ordinal));
        var (files, gatherProblems) = Sources.Gather(roots);
        var problems = new List<CookProblem>(settings.Problems);
        problems.AddRange(gatherProblems);

        var assets = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);

        // The source each name came from, so a name made twice can name both.
        var madeBy = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
        void AddAsset(SourceFile file, string? part, ManifestEntry entry)
        {
            string name = part is null ? file.Name : $"{file.Name}#{part}";
            if (madeBy.TryGetValue(name, out var first))
            {
                problems.Add(new CookProblem(file.Describe(), null, first == file
                    ? $"{file.Name} makes {name} twice."
                    : $"{name} is made by both {first.Describe()} and {file.Describe()}. A name must come from one place.", true));
                return;
            }

            madeBy[name] = file;
            assets[name] = entry;
        }

        var cache = options.CacheFolder is { } cacheFolder ? new CookCache(cacheFolder) : null;
        int cooked = 0;
        int fromCache = 0;
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
            if (!cook)
            {
                continue;
            }

            string? key = null;
            if (cache is not null && cache.SourceHash(file) is { } sourceHash)
            {
                key = CookCache.Key(cooker.Id, cooker.Version, options.Target, values, sourceHash);
                if (cache.TryRestore(key, file, store) is { } hit)
                {
                    foreach (var (message, line) in hit.Warnings)
                    {
                        problems.Add(new CookProblem(file.Describe(), line, message, false));
                    }

                    foreach (var restored in hit.Outputs)
                    {
                        AddAsset(file, restored.Part, new ManifestEntry(restored.Kind, restored.Hash, restored.Size));
                    }

                    fromCache++;
                    continue;
                }
            }

            var output = new CookOutput();
            CookInput? cookInput = null;
            try
            {
                cookInput = new CookInput(file, file.Root.Read(file.Name), values, options.Target);
                cooker.Cook(cookInput, output);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && !InUse(exception))
            {
                output.Error(exception.Message);
            }

            foreach (var (message, line, isError) in output.Problems)
            {
                problems.Add(new CookProblem(file.Describe(), line, message, isError));
            }

            if (output.Problems.Any(problem => problem.IsError))
            {
                continue;
            }

            foreach (var (part, kind, bytes) in output.Assets)
            {
                string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (!store.Has(hash))
                {
                    store.Put(hash, bytes);
                }

                AddAsset(file, part, new ManifestEntry(kind, hash, bytes.LongLength));
            }

            if (key is not null && cookInput is not null)
            {
                cache!.Save(key, file, cookInput.Inputs, output.Assets,
                    output.Problems.Select(problem => (problem.Message, problem.Line)).ToList());
            }

            cooked++;
        }

        cache?.Flush();
        if (!problems.Any(problem => problem.IsError))
        {
            store.Commit(new ContentManifest(options.Target, assets));
        }

        return new CookResult(problems, cooked, fromCache);
    }

    /// <summary>
    /// Whether a file couldn't be read because another program has it open. That isn't a mistake in
    /// the source, so it isn't reported as one: the <see cref="IOException"/> leaves
    /// <see cref="Cook"/> and the caller can try again.
    /// </summary>
    /// <remarks>Windows only: it knows Windows' sharing and lock violations. Other systems don't lock files against reading.</remarks>
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
