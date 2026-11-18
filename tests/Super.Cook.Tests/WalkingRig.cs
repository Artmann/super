using System.Text.Json.Nodes;
using Rendition;
using Super.Cook.Models;

namespace Super.Cook.Tests;

/// <summary>A one-joint rig with a walk clip, written as glTF and cooked with a sidecar.</summary>
internal static class WalkingRig
{
    /// <summary>Writes the rig, its walk and the sidecar into a folder and cooks them.</summary>
    public static (CookResult Result, MemoryStore Store) Cook(string folder, string sidecar)
    {
        WriteRig(folder);
        WriteWalk(folder);
        File.WriteAllText(Path.Combine(folder, "rig.gltf.cook.json"), sidecar);
        var store = new MemoryStore();
        var result = new CookPipeline(ModelCookers.All, new CookOptions(CacheFolder: null) { CacheLimit = CacheSize.Default }).Cook([new FolderRoot(folder)], store);
        return (result, store);
    }

    /// <summary>Cooks the rig and reads the cooked model back, failing the test if the cook did.</summary>
    public static ModelData CookModel(string folder, string sidecar)
    {
        var (result, store) = Cook(folder, sidecar);
        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        var entry = store.Manifest.Assets["rig.gltf"];
        return ModelData.Read(new MemoryStream(ModelCookers.StandAlone(store, entry.Hash, "rig.gltf")), "rig.gltf");
    }

    // An armature turned from z-up to y-up, as Mixamo's are, so the hips' own up is their parent's z.
    private static readonly float[] Armature = [-MathF.Sqrt(0.5f), 0f, 0f, MathF.Sqrt(0.5f)];

    /// <summary>A rig with hips one metre up under the turned armature, and a triangle skinned to them.</summary>
    private static void WriteRig(string folder)
    {
        var gltf = new GltfBuilder();
        int position = gltf.Accessor(GltfBuilder.Floats([0f, 0f, 0f, 0.5f, 0f, 0f, 0f, 0.5f, 0f]), 5126, 3, "VEC3");
        int joints = gltf.Accessor(GltfBuilder.Shorts([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]), 5123, 3, "VEC4");
        int weights = gltf.Accessor(GltfBuilder.Floats([1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f]), 5126, 3, "VEC4");
        // The hips' bind in the scene: one metre up.
        int inverseBind = gltf.Accessor(GltfBuilder.Floats([1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, -1f, 0f, 1f]), 5126, 1, "MAT4");
        gltf.Write(Path.Combine(folder, "rig.gltf"), new JsonObject
        {
            ["nodes"] = new JsonArray
            {
                new JsonObject { ["name"] = "armature", ["rotation"] = new JsonArray(Armature.Select(v => (JsonNode)v).ToArray()), ["children"] = new JsonArray(1, 2) },
                new JsonObject { ["name"] = "hips", ["translation"] = new JsonArray(0f, 0f, 1f) },
                new JsonObject { ["name"] = "body", ["mesh"] = 0, ["skin"] = 0 },
            },
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["name"] = "body",
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = position, ["JOINTS_0"] = joints, ["WEIGHTS_0"] = weights },
                    ["material"] = 0,
                }),
            }),
            ["skins"] = new JsonArray(new JsonObject { ["joints"] = new JsonArray(1), ["inverseBindMatrices"] = inverseBind }),
            ["materials"] = new JsonArray(new JsonObject { ["name"] = "skin" }),
        });
    }

    /// <summary>A clip of the same rig whose hips walk two metres along the armature's x and one along its y, and rise half a metre.</summary>
    private static void WriteWalk(string folder)
    {
        var gltf = new GltfBuilder();
        int times = gltf.Accessor(GltfBuilder.Floats([0f, 1f]), 5126, 2, "SCALAR", min: [0f], max: [1f]);
        int moves = gltf.Accessor(GltfBuilder.Floats([0f, 0f, 1f, 2f, 1f, 1.5f]), 5126, 2, "VEC3");
        gltf.Write(Path.Combine(folder, "walk.gltf"), new JsonObject
        {
            ["nodes"] = new JsonArray
            {
                new JsonObject { ["name"] = "armature", ["rotation"] = new JsonArray(Armature.Select(v => (JsonNode)v).ToArray()), ["children"] = new JsonArray(1) },
                new JsonObject { ["name"] = "hips", ["translation"] = new JsonArray(0f, 0f, 1f) },
            },
            ["animations"] = new JsonArray(new JsonObject
            {
                ["name"] = "walk",
                ["samplers"] = new JsonArray(new JsonObject { ["input"] = times, ["output"] = moves }),
                ["channels"] = new JsonArray(new JsonObject { ["sampler"] = 0, ["target"] = new JsonObject { ["node"] = 1, ["path"] = "translation" } }),
            }),
        });
    }
}
