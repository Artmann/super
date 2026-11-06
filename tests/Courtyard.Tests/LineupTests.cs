using System.Runtime.CompilerServices;
using Super.Cook;
using Super.Cook.Models;

namespace Courtyard.Tests;

public sealed class LineupTests
{
    private static readonly string[] Materials = ["bricks", "flagstones", "dirt", "wood", "leather", "rusty-metal"];

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
        foreach (string material in Materials)
        {
            Assert.Contains($"materials/{material}.material.cs#material", store.Manifest.Assets.Keys);
        }
    }
}
