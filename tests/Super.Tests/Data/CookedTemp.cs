using Super.Cook;

namespace Super.Tests;

/// <summary>Cooks a folder of sources into a cooked folder on disk, as a game's build does.</summary>
internal static class CookedTemp
{
    /// <summary>Cooks <paramref name="root"/> into <paramref name="into"/>, and fails the test if the cook does.</summary>
    public static CookedFolder Cook(string root, string into)
    {
        var result = new CookPipeline(BuiltInCookers.All, new CookOptions()).Cook([new FolderRoot(root)], new FolderStore(into));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Problems));
        return new CookedFolder(into);
    }
}
