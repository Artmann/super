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
    public CookResult Cook(IReadOnlyList<ISourceRoot> roots, ICookedStore store)
    {
        var settings = new CookSettings(roots, cookers.Select(cooker => cooker.Id).ToHashSet(StringComparer.Ordinal));
        var (files, gatherProblems) = Sources.Gather(roots);
        var problems = new List<CookProblem>(settings.Problems);
        problems.AddRange(gatherProblems);

        var assets = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
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
                        assets[restored.Part is null ? file.Name : $"{file.Name}#{restored.Part}"] =
                            new ManifestEntry(restored.Kind, restored.Hash, restored.Size);
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
            catch (Exception exception) when (exception is not OutOfMemoryException)
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

                assets[part is null ? file.Name : $"{file.Name}#{part}"] = new ManifestEntry(kind, hash, bytes.LongLength);
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
