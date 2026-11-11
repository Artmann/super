using System.Runtime.CompilerServices;
using Microsoft.Build.Graph;
using Microsoft.Build.Locator;

namespace Super.Build.Tests;

/// <summary>Finds the installed SDK's MSBuild once for the whole process, before any test touches a project.</summary>
public sealed class InstalledMsBuild
{
    public InstalledMsBuild()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }
}

/// <summary>Registering MSBuild changes how the whole process loads assemblies, so nothing else may run beside it.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MsBuildCollection : ICollectionFixture<InstalledMsBuild>
{
    public const string Name = "MSBuild";
}

// Evaluates a game and every project it references.
[Collection(MsBuildCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class ProjectGraphTests
{
    /// <summary>
    /// dotnet watch loads a game's project graph and keeps its projects in a dictionary by path and target
    /// framework. A project that is in the graph twice, such as once for Debug and once for Release, stops
    /// it with "An item with the same key has already been added" before the game starts.
    /// </summary>
    [Fact]
    public void A_games_project_graph_holds_each_project_once_for_a_target_framework()
    {
        var projects = GraphProjects(Path.Combine(RepositoryRoot(), "examples", "Crafting", "Crafting.csproj"));

        // The graph was really read: the game and the engine it references are in it.
        Assert.Contains("Crafting.csproj [net10.0]", projects);
        Assert.Contains("Super.csproj [net10.0]", projects);

        var repeated = projects
            .GroupBy(project => project)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} x{group.Count()}")
            .Order()
            .ToArray();

        Assert.Empty(repeated);
    }

    /// <summary>Every project in the graph as "Name.csproj [framework]", one entry for each time it's there.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string[] GraphProjects(string projectPath)
    {
        var graph = new ProjectGraph(projectPath);

        return graph.ProjectNodes
            .Select(node => $"{Path.GetFileName(node.ProjectInstance.FullPath)} [{node.ProjectInstance.GetPropertyValue("TargetFramework")}]")
            .ToArray();
    }

    private static string RepositoryRoot([CallerFilePath] string here = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
    }
}
