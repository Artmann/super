using System.Numerics;
using System.Text.Json.Nodes;
using Rendition;
using Super.Cook.Models;

namespace Super.Cook.Tests;

public sealed class ModelInPlaceTests : IDisposable
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

    private static ModelData Cook(string folder, string sidecar)
    {
        WriteRig(folder);
        WriteWalk(folder);
        File.WriteAllText(Path.Combine(folder, "rig.gltf.cook.json"), sidecar);
        var store = new MemoryStore();
        var result = new CookPipeline(ModelCookers.All, new CookOptions(CacheFolder: null) { CacheLimit = CacheSize.Default }).Cook([new FolderRoot(folder)], store);
        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        var entry = store.Manifest.Assets["rig.gltf"];
        return ModelData.Read(new MemoryStream(ModelCookers.StandAlone(store, entry.Hash, "rig.gltf")), "rig.gltf");
    }

    /// <summary>Where the clip's frame puts the hips in the cooked model, after the skeleton's normalization.</summary>
    private static Vector3 HipsAt(ModelData model, AnimationClip clip, int frame)
    {
        var skeleton = model.Skeleton!;
        var global = new Matrix4x4[skeleton.Names.Length];
        for (int joint = 0; joint < global.Length; joint++)
        {
            var local = clip.Frames[frame * clip.JointCount + joint].Matrix;
            global[joint] = skeleton.Parents[joint] >= 0 ? local * global[skeleton.Parents[joint]] : local;
        }

        int hips = skeleton.IndexOf("hips");
        Assert.True(hips >= 0, "The skeleton has no hips joint.");
        return Vector3.Transform(Vector3.Zero, global[hips] * skeleton.RootTransform);
    }

    [Fact]
    public void An_in_place_clip_keeps_the_hips_over_where_they_start_but_lets_them_rise()
    {
        var model = Cook(Temp(), """{ "animations": [{ "file": "walk.gltf", "name": "Walk", "inPlace": true }] }""");
        var walk = model.Clips.Single(c => c.Name == "Walk");

        var start = HipsAt(model, walk, 0);
        var end = HipsAt(model, walk, walk.FrameCount - 1);
        Assert.True(new Vector2(end.X - start.X, end.Z - start.Z).Length() < 0.01f, $"the hips went from {start} to {end}");
        Assert.True(end.Y - start.Y > 0.1f, $"the hips didn't rise, from {start} to {end}");
    }

    [Fact]
    public void A_clip_walks_away_unless_it_is_in_place()
    {
        var model = Cook(Temp(), """{ "animations": [{ "file": "walk.gltf", "name": "Walk" }] }""");
        var walk = model.Clips.Single(c => c.Name == "Walk");

        var start = HipsAt(model, walk, 0);
        var end = HipsAt(model, walk, walk.FrameCount - 1);
        Assert.True(new Vector2(end.X - start.X, end.Z - start.Z).Length() > 0.5f, $"the hips only went from {start} to {end}");
    }

    // An armature turned from z-up to y-up, as Mixamo's are, so the hips' own up is their parent's z.
    private static readonly float[] Armature = [-MathF.Sqrt(0.5f), 0f, 0f, MathF.Sqrt(0.5f)];

    /// <summary>A rig with hips one metre up under the turned armature, and a triangle skinned to them.</summary>
    private static void WriteRig(string folder)
    {
        var gltf = new Gltf();
        int position = gltf.Accessor(Floats([0f, 0f, 0f, 0.5f, 0f, 0f, 0f, 0.5f, 0f]), 5126, 3, "VEC3");
        int joints = gltf.Accessor(Shorts([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]), 5123, 3, "VEC4");
        int weights = gltf.Accessor(Floats([1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f]), 5126, 3, "VEC4");
        // The hips' bind in the scene: one metre up.
        int inverseBind = gltf.Accessor(Floats([1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, -1f, 0f, 1f]), 5126, 1, "MAT4");
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
        var gltf = new Gltf();
        int times = gltf.Accessor(Floats([0f, 1f]), 5126, 2, "SCALAR", min: [0f], max: [1f]);
        int moves = gltf.Accessor(Floats([0f, 0f, 1f, 2f, 1f, 1.5f]), 5126, 2, "VEC3");
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

    private sealed class Gltf
    {
        private readonly List<byte> _buffer = [];
        private readonly JsonArray _views = [];
        private readonly JsonArray _accessors = [];

        public int Accessor(byte[] data, int componentType, int count, string type, float[]? min = null, float[]? max = null)
        {
            _views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = _buffer.Count, ["byteLength"] = data.Length });
            _buffer.AddRange(data);
            var accessor = new JsonObject
            {
                ["bufferView"] = _views.Count - 1,
                ["componentType"] = componentType,
                ["count"] = count,
                ["type"] = type,
            };
            if (min is not null && max is not null)
            {
                accessor["min"] = new JsonArray(min.Select(v => (JsonNode)v).ToArray());
                accessor["max"] = new JsonArray(max.Select(v => (JsonNode)v).ToArray());
            }

            _accessors.Add(accessor);
            return _accessors.Count - 1;
        }

        public void Write(string path, JsonObject parts)
        {
            parts["asset"] = new JsonObject { ["version"] = "2.0" };
            parts["scene"] = 0;
            parts["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) });
            parts["buffers"] = new JsonArray(new JsonObject
            {
                ["byteLength"] = _buffer.Count,
                ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(_buffer.ToArray()),
            });
            parts["bufferViews"] = _views;
            parts["accessors"] = _accessors;
            File.WriteAllText(path, parts.ToJsonString());
        }
    }

    private static byte[] Floats(float[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    private static byte[] Shorts(ushort[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
}
