using Rendition;
using Silk.NET.Assimp;
using Assimp = Silk.NET.Assimp.Assimp;
using AssimpMaterial = Silk.NET.Assimp.Material;

namespace Super.Cook.Models;

/// <summary>A model's materials as they're read, with what their rules add, each list in the scene's material order.</summary>
internal sealed class ImportedMaterials
{
    public List<ModelMaterial> Materials { get; } = [];

    public List<string> Names { get; } = [];

    public List<ModelMaterialShader?> Shaders { get; } = [];

    public List<WantedOcclusion?> Occlusions { get; } = [];

    public List<ModelMaterialToon?> Toons { get; } = [];

    /// <summary>The width in metres of each material's graph, or 0 for a material without one.</summary>
    public List<float> GraphWidths { get; } = [];

    /// <summary>Reads every material of the scene. What's wrong with them is reported.</summary>
    public static unsafe ImportedMaterials Read(ModelCook cook, Assimp assimp, Scene* scene)
    {
        // The graphs run at once first, since one graph alone leaves cores idle while it does its serial steps. The
        // materials then take their runs in order, so problems come out as if each ran its own.
        cook.Graphs.RunAhead(GraphsWanted(cook, assimp, scene));
        var imported = new ImportedMaterials();
        for (uint i = 0; i < scene->MNumMaterials; i++)
        {
            imported.Add(cook, assimp, scene->MMaterials[i]);
        }

        return imported;
    }

    /// <summary>The graphs and detail graphs the materials will run, in material order.</summary>
    private static unsafe List<WantedGraphs> GraphsWanted(ModelCook cook, Assimp assimp, Scene* scene)
    {
        var wanted = new List<WantedGraphs>();
        for (uint i = 0; i < scene->MNumMaterials; i++)
        {
            if (GraphRules.Wanted(cook.RuleFor(AssimpMaterials.NameOf(assimp, scene->MMaterials[i]))) is { } graphs)
            {
                wanted.Add(graphs);
            }
        }

        return wanted;
    }

    private unsafe void Add(ModelCook cook, Assimp assimp, AssimpMaterial* source)
    {
        var material = MaterialImporter.Import(assimp, source, cook, out string name, out float graphWidth);
        Occlusions.Add(MaterialExtras.OcclusionWanted(assimp, source, name, cook));
        Toons.Add(MaterialExtras.ImportToon(name, cook));
        cook.Textures.WidenRoughness(material.SurfaceTexture, material.NormalTexture);
        Materials.Add(material);
        Names.Add(name);
        GraphWidths.Add(graphWidth);
        Shaders.Add(cook.RuleFor(name) is { } rule ? MaterialExtras.ImportShader(rule, name, cook) : null);
    }
}
