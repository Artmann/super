using System.Security.Cryptography;
using System.Text;

namespace Super.Cook.Tests;

/// <summary>Objects a cooker adds by hash, such as textures models share: kept once, listed, pruned and cached.</summary>
public sealed class SharedObjectsTests : IDisposable
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

    /// <summary>Adds the source's bytes as a shared object, and an asset holding the hash it got back.</summary>
    private sealed class SharingCooker : ICooker
    {
        public string Id => "sharing";

        public int Version => 1;

        public IReadOnlyList<string> Extensions { get; } = [".share"];

        public void Cook(CookInput input, CookOutput output)
        {
            string hash = output.AddObject(input.Bytes);
            output.Add(ContentKind.Data, Encoding.UTF8.GetBytes(hash));
        }
    }

    private static CookResult Cook(string src, ICookedStore store, string? cache = null) =>
        new CookPipeline([.. BuiltInCookers.All, new SharingCooker()], new CookOptions(CacheFolder: cache)).Cook([new FolderRoot(src)], store);

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string ObjectFile(string folder, string hash) => Path.Combine(folder, "objects", hash[..2], hash);

    [Fact]
    public void Two_sources_adding_the_same_object_keep_one_listed_copy()
    {
        string src = Temp();
        File.WriteAllText(Path.Combine(src, "a.share"), "bark");
        File.WriteAllText(Path.Combine(src, "b.share"), "bark");
        var store = new MemoryStore();

        var result = Cook(src, store);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        string hash = Hash("bark");
        Assert.Equal([hash], store.Manifest.Objects);
        Assert.Equal("bark", Encoding.UTF8.GetString(store.Get(hash)));
        Assert.Equal(hash, Encoding.UTF8.GetString(store.Get(store.Manifest.Assets["a.share"].Hash)));
    }

    [Fact]
    public void A_folder_keeps_a_listed_object_and_prunes_it_one_commit_after_it_stops_being_listed()
    {
        string src = Temp();
        string output = Temp();
        File.WriteAllText(Path.Combine(src, "a.share"), "old");

        Assert.True(Cook(src, new FolderStore(output)).Succeeded);
        Assert.True(File.Exists(ObjectFile(output, Hash("old"))));

        File.WriteAllText(Path.Combine(src, "a.share"), "new");
        Assert.True(Cook(src, new FolderStore(output)).Succeeded);

        // A game may still be loading from the manifest just replaced, so the old object stays one commit.
        Assert.True(File.Exists(ObjectFile(output, Hash("old"))));
        Assert.True(File.Exists(ObjectFile(output, Hash("new"))));

        Assert.True(Cook(src, new FolderStore(output)).Succeeded);

        Assert.False(File.Exists(ObjectFile(output, Hash("old"))));
        Assert.True(File.Exists(ObjectFile(output, Hash("new"))));
    }

    [Fact]
    public void A_cache_hit_restores_the_shared_objects_into_an_empty_folder()
    {
        string src = Temp();
        string cache = Temp();
        string first = Temp();
        string second = Temp();
        File.WriteAllText(Path.Combine(src, "a.share"), "bark");
        Assert.Equal(1, Cook(src, new FolderStore(first), cache).Cooked);

        var result = Cook(src, new FolderStore(second), cache);

        Assert.Equal((0, 1), (result.Cooked, result.FromCache));
        Assert.Equal("bark", File.ReadAllText(ObjectFile(second, Hash("bark"))));
        var manifest = ContentManifest.Parse(File.ReadAllBytes(Path.Combine(second, ContentManifest.FileName)), "content.manifest");
        Assert.Equal([Hash("bark")], manifest.Objects);
    }

    [Fact]
    public void A_cache_record_whose_shared_object_is_gone_cooks_again()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "a.share"), "bark");
        Assert.Equal(1, Cook(src, new MemoryStore(), cache).Cooked);
        File.Delete(ObjectFile(cache, Hash("bark")));
        var store = new MemoryStore();

        var result = Cook(src, store, cache);

        Assert.Equal((1, 0), (result.Cooked, result.FromCache));
        Assert.Equal("bark", Encoding.UTF8.GetString(store.Get(Hash("bark"))));
        Assert.Contains(result.Report.Files, file => file.Miss == $"cached object gone: {Hash("bark")}");
    }
}
