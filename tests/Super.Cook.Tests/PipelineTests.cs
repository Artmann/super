using System.Security.Cryptography;
using System.Text;

namespace Super.Cook.Tests;

public sealed class PipelineTests : IDisposable
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

    private static CookPipeline Pipeline()
    {
        return new CookPipeline(BuiltInCookers.All, new CookOptions());
    }

    private sealed class PartCooker : ICooker
    {
        public string Id => "part";

        public int Version => 1;

        public IReadOnlyList<string> Extensions { get; } = [".part"];

        public void Cook(CookInput input, CookOutput output)
        {
            output.AddPart("a", ContentKind.Data, [1]);
            output.AddPart("b", ContentKind.Data, [2]);
        }
    }

    [Fact]
    public void Cooks_json_unchanged_with_its_hash()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{ \"damage\": 4 }");
        var root = new MemoryRoot();
        root.Add("items/axe.json", bytes);
        var store = new MemoryStore();

        var result = Pipeline().Cook([root], store);

        Assert.True(result.Succeeded);
        var entry = store.Manifest.Assets["items/axe.json"];
        Assert.Equal(ContentKind.Data, entry.Kind);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), entry.Hash);
        Assert.Equal(bytes.Length, entry.Size);
        Assert.Equal(bytes, store.Get(entry.Hash));
    }

    [Fact]
    public void Json_with_a_byte_order_mark_cooks_unchanged()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{ \"damage\": 4 }")];
        var root = new MemoryRoot();
        root.Add("items/axe.json", bytes);
        var store = new MemoryStore();

        var result = Pipeline().Cook([root], store);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Problems));
        Assert.Equal(bytes, store.Get(store.Manifest.Assets["items/axe.json"].Hash));
    }

    [Fact]
    public void Bad_json_reports_the_line()
    {
        var root = new MemoryRoot { ["bad.json"] = "{\n \"a\": 1,\n oops\n}" };
        var store = new MemoryStore();

        var result = Pipeline().Cook([root], store);

        Assert.False(result.Succeeded);
        var problem = Assert.Single(result.Problems, p => p.IsError);
        Assert.Equal(3, problem.Line);
        Assert.Contains("bad.json", problem.File);
        Assert.Same(ContentManifest.Empty, store.Manifest);
    }

    [Fact]
    public void File_without_cooker_is_an_error_naming_it()
    {
        var root = new MemoryRoot { ["a.xyz"] = "x" };

        var result = Pipeline().Cook([root], new MemoryStore());

        var problem = Assert.Single(result.Problems);
        Assert.True(problem.IsError);
        Assert.Equal("a.xyz has no cooker. Add a rule with \"cook\": false if it isn't an asset.", problem.Message);
    }

    [Fact]
    public void Cook_false_skips_a_file_without_a_cooker()
    {
        var root = new MemoryRoot
        {
            ["a.xyz"] = "x",
            ["cook.json"] = "{ \"rules\": [ { \"match\": \"*.xyz\", \"cook\": false } ] }",
        };
        var store = new MemoryStore();

        var result = Pipeline().Cook([root], store);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Problems);
        Assert.Empty(store.Manifest.Assets);
    }

    [Fact]
    public void Parts_are_named_with_hash()
    {
        var root = new MemoryRoot { ["x.part"] = "x" };
        var store = new MemoryStore();

        var result = new CookPipeline([new PartCooker()], new CookOptions()).Cook([root], store);

        Assert.True(result.Succeeded);
        Assert.Equal(["x.part#a", "x.part#b"], store.Manifest.Assets.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new byte[] { 2 }, store.Get(store.Manifest.Assets["x.part#b"].Hash));
    }

    [Fact]
    public void Deleted_source_leaves_manifest_and_object_is_pruned()
    {
        var root = new MemoryRoot { ["a.json"] = "{ \"a\": 1 }", ["b.json"] = "{ \"b\": 2 }" };
        string content = Temp();
        var store = new FolderStore(content);
        Pipeline().Cook([root], store);
        var manifest = ContentManifest.Parse(File.ReadAllBytes(Path.Combine(content, ContentManifest.FileName)), "m");
        string bObject = Path.Combine(content, ContentManifest.ObjectPath(manifest.Assets["b.json"].Hash));
        Assert.True(File.Exists(bObject));

        root.Remove("b.json");
        var result = Pipeline().Cook([root], store);

        Assert.True(result.Succeeded);
        manifest = ContentManifest.Parse(File.ReadAllBytes(Path.Combine(content, ContentManifest.FileName)), "m");
        Assert.Equal(["a.json"], manifest.Assets.Keys);
        Assert.False(File.Exists(bObject));
        Assert.True(File.Exists(Path.Combine(content, ContentManifest.ObjectPath(manifest.Assets["a.json"].Hash))));
    }

    [Fact]
    public void Renamed_case_takes_the_new_case()
    {
        string source = Temp();
        File.WriteAllText(Path.Combine(source, "fox.json"), "{}");
        var store = new MemoryStore();
        Pipeline().Cook([new FolderRoot(source)], store);

        File.Move(Path.Combine(source, "fox.json"), Path.Combine(source, "Fox.json"));
        Pipeline().Cook([new FolderRoot(source)], store);

        Assert.Equal(["Fox.json"], store.Manifest.Assets.Keys);
    }

    [Fact]
    public void Failed_cook_keeps_the_old_manifest()
    {
        var root = new MemoryRoot { ["a.json"] = "{}" };
        var store = new MemoryStore();
        Pipeline().Cook([root], store);
        var before = store.Manifest;

        root["a.json"] = "{ nope";
        root["b.json"] = "{}";
        var result = Pipeline().Cook([root], store);

        Assert.False(result.Succeeded);
        Assert.Same(before, store.Manifest);
        Assert.Equal(["a.json"], store.Manifest.Assets.Keys);
    }

    [Fact]
    public void Same_sources_give_byte_identical_manifest()
    {
        var root = new MemoryRoot { ["b.json"] = "{ \"b\": 1 }", ["a/x.json"] = "[1]", ["c.json"] = "{}" };
        var one = new MemoryStore();
        var two = new MemoryStore();

        Pipeline().Cook([root], one);
        Pipeline().Cook([root], two);

        Assert.Equal(3, one.Manifest.Assets.Count);
        Assert.Equal(one.Manifest.Serialize(), two.Manifest.Serialize());
    }

    private sealed class TwicePartCooker : ICooker
    {
        public string Id => "twice";

        public int Version => 1;

        public IReadOnlyList<string> Extensions { get; } = [".twice"];

        public void Cook(CookInput input, CookOutput output)
        {
            output.AddPart("a", ContentKind.Data, [1]);
            output.AddPart("a", ContentKind.Data, [2]);
        }
    }

    // Cooks a file whose name ends in #a, as a part of x.part would be named.
    private sealed class HashACooker : ICooker
    {
        public string Id => "hash-a";

        public int Version => 1;

        public IReadOnlyList<string> Extensions { get; } = ["#a"];

        public void Cook(CookInput input, CookOutput output)
        {
            output.Add(ContentKind.Data, [3]);
        }
    }

    [Fact]
    public void Part_added_twice_is_an_error_naming_it()
    {
        var root = new MemoryRoot { ["x.twice"] = "x" };
        var store = new MemoryStore();

        var result = new CookPipeline([new TwicePartCooker()], new CookOptions()).Cook([root], store);

        Assert.False(result.Succeeded);
        Assert.Equal("x.twice makes x.twice#a twice.", Assert.Single(result.Problems, p => p.IsError).Message);
        Assert.Same(ContentManifest.Empty, store.Manifest);
    }

    [Fact]
    public void File_named_like_a_part_is_an_error_naming_both()
    {
        var root = new MemoryRoot { ["x.part"] = "x", ["x.part#a"] = "y" };
        var store = new MemoryStore();

        var result = new CookPipeline([new PartCooker(), new HashACooker()], new CookOptions()).Cook([root], store);

        Assert.False(result.Succeeded);
        Assert.Equal("x.part#a is made by both memory:x.part and memory:x.part#a. A name must come from one place.",
            Assert.Single(result.Problems, p => p.IsError).Message);
        Assert.Same(ContentManifest.Empty, store.Manifest);
    }
}
