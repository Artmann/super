namespace Super.Cook.Tests;

/// <summary>The tests below change an environment variable of the whole process, so nothing else may run beside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

[Collection(ProcessEnvironmentCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class CacheEnvironmentTests
{
    [Fact]
    public void Default_cache_folder_follows_SUPER_CACHE()
    {
        string? before = Environment.GetEnvironmentVariable("SUPER_CACHE");
        try
        {
            Environment.SetEnvironmentVariable("SUPER_CACHE", "/some/where");
            Assert.Equal("/some/where", CookOptions.DefaultCacheFolder);
            Environment.SetEnvironmentVariable("SUPER_CACHE", null);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".super", "cache"), CookOptions.DefaultCacheFolder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUPER_CACHE", before);
        }
    }

    [Fact]
    public void Default_cache_limit_follows_SUPER_CACHE_SIZE()
    {
        string? before = Environment.GetEnvironmentVariable("SUPER_CACHE_SIZE");
        try
        {
            Environment.SetEnvironmentVariable("SUPER_CACHE_SIZE", "8GB");
            Assert.Equal(8L << 30, CookOptions.DefaultCacheLimit);
            Environment.SetEnvironmentVariable("SUPER_CACHE_SIZE", "none");
            Assert.Equal(0, CookOptions.DefaultCacheLimit);
            Environment.SetEnvironmentVariable("SUPER_CACHE_SIZE", null);
            Assert.Equal(4L << 30, CookOptions.DefaultCacheLimit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUPER_CACHE_SIZE", before);
        }
    }

    [Fact]
    public void A_SUPER_CACHE_SIZE_that_isnt_a_size_cooks_with_the_default_and_warns()
    {
        string? before = Environment.GetEnvironmentVariable("SUPER_CACHE_SIZE");
        string src = Path.Combine(Path.GetTempPath(), "super-limit-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(Path.GetTempPath(), "super-limit-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "a.json"), "{}");
            Environment.SetEnvironmentVariable("SUPER_CACHE_SIZE", "lots");

            var result = new CookPipeline(BuiltInCookers.All, new CookOptions(CacheFolder: cache)).Cook([new FolderRoot(src)], new MemoryStore());

            var warning = Assert.Single(result.Problems);
            Assert.False(warning.IsError);
            Assert.Equal(
                "SUPER_CACHE_SIZE: \"lots\" isn't a size. Give a number with KB, MB, GB or TB, such as 4GB, 500MB or 1.5GB, "
                    + "a number of bytes, or none for no limit. Using the default, 4.0 GB.",
                warning.Message);
            Assert.Equal(4L << 30, result.Report.Trim!.Limit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUPER_CACHE_SIZE", before);
            Directory.Delete(src, true);
            Directory.Delete(cache, true);
        }
    }
}
