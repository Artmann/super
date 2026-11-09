using System.Text;

namespace Super.Cook.Tests;

/// <summary>A content folder links to the cache's copy of each object, so the bytes are on disk once.</summary>
public sealed class LinkTests : IDisposable
{
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            Directory.Delete(folder, true);
        }
    }

    private string Temp()
    {
        string folder = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N"));
        _folders.Add(folder);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string ObjectIn(string folder, string hash)
    {
        return Path.Combine(folder, "objects", hash[..2], hash);
    }

    private static CookResult Cook(string src, string cache, string output)
    {
        return new CookPipeline(BuiltInCookers.All, new CookOptions(CacheFolder: cache)).Cook([new FolderRoot(src)], new FolderStore(output));
    }

    private static string HashOf(string output, string name)
    {
        var manifest = ContentManifest.Parse(File.ReadAllBytes(Path.Combine(output, ContentManifest.FileName)), output);
        return manifest.Assets[name].Hash;
    }

    /// <summary>
    /// Changes the cache's copy in place, which no cook ever does, and reads the content folder's copy back. Two names for
    /// one file see the change; two copies don't.
    /// </summary>
    private static string ReadAfterChangingCacheCopy(string cache, string output, string hash)
    {
        using (var stream = new FileStream(ObjectIn(cache, hash), FileMode.Open, FileAccess.Write))
        {
            stream.Write("changed"u8);
        }

        return Encoding.UTF8.GetString(File.ReadAllBytes(ObjectIn(output, hash)));
    }

    [Fact]
    public void A_fresh_cook_links_the_content_folder_to_the_cache()
    {
        string src = Temp();
        string cache = Temp();
        string output = Temp();
        File.WriteAllText(Path.Combine(src, "a.json"), "{ \"x\": 1 }");

        Assert.True(Cook(src, cache, output).Succeeded);
        string hash = HashOf(output, "a.json");

        Assert.StartsWith("changed", ReadAfterChangingCacheCopy(cache, output, hash));
    }

    [Fact]
    public void A_cook_from_the_cache_links_the_content_folder_to_the_cache()
    {
        string src = Temp();
        string cache = Temp();
        string first = Temp();
        string second = Temp();
        File.WriteAllText(Path.Combine(src, "a.json"), "{ \"x\": 1 }");
        Cook(src, cache, first);

        var result = Cook(src, cache, second);
        string hash = HashOf(second, "a.json");

        Assert.Equal(1, result.FromCache);
        Assert.StartsWith("changed", ReadAfterChangingCacheCopy(cache, second, hash));
    }

    [Fact]
    public void Linking_to_a_file_that_is_not_there_says_no_and_leaves_nothing_behind()
    {
        string output = Temp();
        var store = new FolderStore(output);
        string hash = new string('a', 64);

        bool linked = store.TryLink(hash, Path.Combine(Temp(), "gone"));

        Assert.False(linked);
        Assert.False(store.Has(hash));
        Assert.Empty(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories));
    }
}
