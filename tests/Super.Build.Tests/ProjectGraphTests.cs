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

    /// <summary>
    /// A game built in Debug can ask for the engine in Release, so it plays faster. Super.Materials, which
    /// Super.targets references for material graphs, must then build in Release too, or Super.Random, which
    /// both it and Super reference, is in the graph once for each configuration.
    /// </summary>
    [Fact]
    public void A_game_that_builds_the_engine_in_release_holds_each_project_once()
    {
        var folder = Directory.CreateTempSubdirectory("super-graph-");

        try
        {
            var source = Path.Combine(RepositoryRoot(), "src");
            var game = Path.Combine(folder.FullName, "Game.csproj");

            File.WriteAllText(game, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{source}/Super/Super.csproj" SetConfiguration="Configuration=Release" />
                    <ProjectReference Include="{source}/Rendition/Rendition.csproj" SetConfiguration="Configuration=Release" />
                  </ItemGroup>
                  <Import Project="{source}/Super.Build/Super.targets" />
                </Project>
                """);

            var graph = new ProjectGraph(game);
            var projects = graph.ProjectNodes
                .Select(node => $"{Path.GetFileName(node.ProjectInstance.FullPath)} [{node.ProjectInstance.GetPropertyValue("Configuration")}]")
                .Order()
                .ToArray();

            Assert.Equal(
                ["Game.csproj [Debug]", "Rendition.csproj [Release]", "Super.csproj [Release]", "Super.Materials.csproj [Release]", "Super.Random.csproj [Release]"],
                projects);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
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
