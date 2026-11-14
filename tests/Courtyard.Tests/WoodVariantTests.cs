using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Super.Cook;
using Super.Cook.Models;

namespace Courtyard.Tests;

/// <summary>
/// The barrel's and the deck's wood are variants of <c>wood.material.cs</c>. Cooked the way the game's cook cooks them,
/// every setting they name must reach the graph and change the wood.
/// </summary>
[Trait("Speed", "Slow")]
public sealed class WoodVariantTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "courtyard-wood-variants-" + Guid.NewGuid().ToString("N"));

    public WoodVariantTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    private static string Materials([CallerFilePath] string here = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "examples", "Courtyard", "content", "materials"));
    }

    [Theory]
    [InlineData("barrel-staves.material.json", new[] { "weathering", "tone", "rustAt" })]
    [InlineData("deck-boards.material.json", new[] { "weathering", "tone" })]
    public void Every_setting_of_the_variant_changes_the_cooked_wood(string variant, string[] settings)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Materials(), variant)))!.AsObject();
        foreach (var setting in settings)
        {
            Assert.True(json.ContainsKey(setting), $"{variant} doesn't set {setting}.");
        }

        // The real variant, and beside it one copy for each setting with that setting left out.
        File.Copy(Path.Combine(Materials(), "wood.material.cs"), Path.Combine(_folder, "wood.material.cs"));
        File.WriteAllText(Path.Combine(_folder, "cook.json"), """{ "rules": [{ "match": "*.material.*", "material": { "size": 128 } }] }""");
        File.WriteAllText(Path.Combine(_folder, variant), json.ToJsonString());
        foreach (var setting in settings)
        {
            var without = json.DeepClone().AsObject();
            without.Remove(setting);
            File.WriteAllText(Path.Combine(_folder, $"without-{setting}.material.json"), without.ToJsonString());
        }

        var store = new MemoryStore();
        var result = new CookPipeline(ModelCookers.All, new CookOptions()).Cook([new FolderRoot(_folder)], store);

        Assert.True(result.Problems.Count == 0, string.Join(Environment.NewLine, result.Problems.Select(problem => problem.ToString())));
        var cooked = store.Manifest.Assets[$"{variant}#color"].Hash;
        foreach (var setting in settings)
        {
            var without = store.Manifest.Assets[$"without-{setting}.material.json#color"].Hash;
            Assert.True(cooked != without, $"{variant} cooks the same wood without {setting}, so its {setting} doesn't reach the graph.");
        }
    }
}
