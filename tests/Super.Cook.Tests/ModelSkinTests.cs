using System.Text.Json.Nodes;
using Rendition;
using Super.Cook.Models;

namespace Super.Cook.Tests;

public sealed class ModelSkinTests : IDisposable
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

    private static CookResult Cook(string folder)
    {
        var store = new MemoryStore();
        return new CookPipeline(ModelCookers.All, new CookOptions(CacheFolder: null) { CacheLimit = CacheSize.Default }).Cook([new FolderRoot(folder)], store);
    }

    [Fact]
    public void Joints_of_the_skin_that_a_mesh_never_weights_are_not_counted_as_its_influences()
    {
        // Assimp gives every joint of the skin that a mesh doesn't use a zero weight on its first vertex, so a mesh
        // weighted to one joint of a six-joint skin would read as following six.
        string folder = Temp();
        WriteRig(folder, joints: 6);

        var result = Cook(folder);

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void A_vertex_weighted_to_more_than_four_joints_still_warns()
    {
        string folder = Temp();
        WriteRig(folder, joints: 6, weightedTo: 5);

        var result = Cook(folder);

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Equal(["1 vertices of mesh 0 follow more than four joints; the weakest were dropped"], result.Problems.Select(problem => problem.Message));
    }

    /// <summary>
    /// A chain of joints each half a metre over the last, and a triangle skinned to the first. With
    /// <paramref name="weightedTo"/> over one, its first vertex is shared among that many joints instead, through a
    /// second set of joints and weights.
    /// </summary>
    private static void WriteRig(string folder, int joints, int weightedTo = 1)
    {
        var gltf = new GltfBuilder();
        int position = gltf.Accessor(GltfBuilder.Floats([0f, 0f, 0f, 0.5f, 0f, 0f, 0f, 0.5f, 0f]), 5126, 3, "VEC3");
        float share = 1f / weightedTo;
        ushort[] firstJoints = [0, 1, 2, 3, 0, 0, 0, 0, 0, 0, 0, 0];
        float[] firstWeights = [share, weightedTo > 1 ? share : 0f, weightedTo > 2 ? share : 0f, weightedTo > 3 ? share : 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f];
        var attributes = new JsonObject
        {
            ["POSITION"] = position,
            ["JOINTS_0"] = gltf.Accessor(GltfBuilder.Shorts(firstJoints), 5123, 3, "VEC4"),
            ["WEIGHTS_0"] = gltf.Accessor(GltfBuilder.Floats(firstWeights), 5126, 3, "VEC4"),
        };
        if (weightedTo > 4)
        {
            ushort[] moreJoints = [4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
            float[] moreWeights = [share, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f];
            attributes["JOINTS_1"] = gltf.Accessor(GltfBuilder.Shorts(moreJoints), 5123, 3, "VEC4");
            attributes["WEIGHTS_1"] = gltf.Accessor(GltfBuilder.Floats(moreWeights), 5126, 3, "VEC4");
        }

        var inverseBinds = new List<float>();
        var nodes = new JsonArray();
        for (int joint = 0; joint < joints; joint++)
        {
            inverseBinds.AddRange([1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, -0.5f * (joint + 1), 0f, 1f]);
            var node = new JsonObject { ["name"] = $"joint{joint}", ["translation"] = new JsonArray(0f, 0.5f, 0f) };
            if (joint + 1 < joints)
            {
                node["children"] = new JsonArray(joint + 2);
            }

            nodes.Add(node);
        }

        int inverseBind = gltf.Accessor(GltfBuilder.Floats([.. inverseBinds]), 5126, joints, "MAT4");
        var root = new JsonObject { ["name"] = "armature", ["children"] = new JsonArray(1, joints + 1) };
        nodes.Insert(0, root);
        nodes.Add(new JsonObject { ["name"] = "body", ["mesh"] = 0, ["skin"] = 0 });
        gltf.Write(Path.Combine(folder, "rig.gltf"), new JsonObject
        {
            ["nodes"] = nodes,
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["name"] = "body",
                ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = attributes, ["material"] = 0 }),
            }),
            ["skins"] = new JsonArray(new JsonObject
            {
                ["joints"] = new JsonArray(Enumerable.Range(1, joints).Select(index => (JsonNode)index).ToArray()),
                ["inverseBindMatrices"] = inverseBind,
            }),
            ["materials"] = new JsonArray(new JsonObject { ["name"] = "skin" }),
        });
    }
}
