using Rendition;
using Super.Cook.Models;

namespace Super.Cook.Tests;

/// <summary>A material rule's <c>noDecals</c>.</summary>
public sealed class NoDecalsRuleTests : IDisposable
{
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("{ \"match\": \"bark\", \"noDecals\": true }", MaterialFlags.NoDecals)]
    [InlineData("{ \"match\": \"bark\" }", MaterialFlags.None)]
    public void NoDecals_sets_the_materials_flag(string rule, MaterialFlags expected)
    {
        string src = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N"));
        _folders.Add(src);
        Directory.CreateDirectory(src);
        TinyGltf.WriteQuads(src, "quads.gltf", "bark", uvs2: null);
        File.WriteAllText(Path.Combine(src, "quads.gltf.cook.json"), $"{{ \"compressTextures\": false, \"materials\": [ {rule} ] }}");
        var store = new MemoryStore();

        var result = new CookPipeline(ModelCookers.All, new CookOptions()).Cook([new FolderRoot(src)], store);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = ModelData.Read(new MemoryStream(store.Get(store.Manifest.Assets["quads.gltf"].Hash)), "quads.gltf",
            hash => TextureData.Read(new MemoryStream(store.Get(hash)), hash));
        Assert.Equal(expected, model.Materials[0].Flags & MaterialFlags.NoDecals);
    }
}
