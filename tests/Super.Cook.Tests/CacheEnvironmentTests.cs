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
}
