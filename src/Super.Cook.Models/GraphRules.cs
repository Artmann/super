using Super.Cook.Materials;

namespace Super.Cook.Models;

/// <summary>Which graph and detail graph a material rule runs, and whether it can.</summary>
internal static class GraphRules
{
    /// <summary>
    /// The graphs a rule will run, by the same rules <see cref="RunGraph"/> and <see cref="RunDetailGraph"/> follow, so
    /// none runs ahead that they wouldn't. Null when it runs none.
    /// </summary>
    public static WantedGraphs? Wanted(MaterialRule? rule)
    {
        if (rule is not { Graph: { } path } || HasOwnTextures(rule))
        {
            return null;
        }

        int size = rule.GraphSize ?? MaterialGraphs.DefaultSize;
        if (MaterialGraphs.SizeProblem("graphSize", size) is not null)
        {
            return null;
        }

        int detailSize = rule.DetailGraphSize ?? MaterialGraphs.DefaultSize;
        bool withDetail = rule.DetailGraph is not null && !HasDetailTextures(rule)
            && MaterialGraphs.SizeProblem("detailGraphSize", detailSize) is null;
        return new WantedGraphs((path, size), withDetail ? (rule.DetailGraph!, detailSize) : null);
    }

    /// <returns>The graph's run, or null when the rule has no graph or it failed, which was reported.</returns>
    public static GraphRun? RunGraph(MaterialRule rule, string materialName, ModelCook cook)
    {
        if (rule.Graph is not { } path)
        {
            return null;
        }

        if (HasOwnTextures(rule))
        {
            cook.MaterialError(materialName, "graph makes the colour, normal and surface textures, so it can't be used with baseColor, normal, surface or alpha.");
            return null;
        }

        return Run(path, "graphSize", rule.GraphSize, materialName, cook);
    }

    /// <returns>The detail graph's run, or null when the rule has none or it failed, which was reported.</returns>
    public static GraphRun? RunDetailGraph(MaterialRule rule, string materialName, ModelCook cook)
    {
        if (rule.DetailGraph is not { } path)
        {
            return null;
        }

        if (rule.Graph is null)
        {
            cook.MaterialError(materialName, "detailGraph tiles at a size the main graph's width sets, so it needs a graph.");
            return null;
        }

        if (HasDetailTextures(rule))
        {
            cook.MaterialError(materialName, "detailGraph makes the detail textures, so it can't be used with detailColor or detailNormal.");
            return null;
        }

        return Run(path, "detailGraphSize", rule.DetailGraphSize, materialName, cook);
    }

    /// <summary>Whether the rule names any of the textures a graph makes.</summary>
    private static bool HasOwnTextures(MaterialRule rule) =>
        rule.BaseColor is not null || rule.Normal is not null || rule.Surface is not null || rule.Alpha is not null;

    /// <summary>Whether the rule names any of the textures a detail graph makes.</summary>
    private static bool HasDetailTextures(MaterialRule rule) => rule.DetailColor is not null || rule.DetailNormal is not null;

    /// <summary>Runs a graph at the size the rule asks for under <paramref name="sizeName"/>, or the default size.</summary>
    private static GraphRun? Run(string path, string sizeName, int? askedSize, string materialName, ModelCook cook)
    {
        void Error(string message) => cook.MaterialError(materialName, message);

        int size = askedSize ?? MaterialGraphs.DefaultSize;
        if (MaterialGraphs.SizeProblem(sizeName, size) is { } problem)
        {
            Error(problem);
            return null;
        }

        return cook.Graphs.Run(path, size, Error, message => cook.MaterialWarning(materialName, message));
    }
}
