using System.Runtime.CompilerServices;
using Super.Cook;
using Super.Cook.Models;

namespace Courtyard.Tests;

// Cooks every Courtyard model, graphs and mask bakes included, which takes minutes.
[Trait("Speed", "Slow")]
public sealed class LineupTests
{
    /// <summary>The example's content folder, found from this file so the test reads the real graphs.</summary>
    private static string ContentFolder([CallerFilePath] string here = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "examples", "Courtyard", "content"));
    }

    [Fact]
    public void Every_material_cooks_without_warnings()
    {
        var store = new MemoryStore();
        var result = new CookPipeline(ModelCookers.All, new CookOptions()).Cook([new FolderRoot(ContentFolder())], store);

        // The problems are the message, so a failure prints every warning in full.
        Assert.True(result.Problems.Count == 0, string.Join(Environment.NewLine, result.Problems.Select(problem => problem.ToString())));
        // The game's own list, so a graph the lineup shows can't go missing from the cook unnoticed.
        foreach (string material in global::Courtyard.LineupGame.Materials)
        {
            Assert.Contains($"{material}#material", store.Manifest.Assets.Keys);
        }
    }
}
