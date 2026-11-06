using System.Numerics;
using System.Text.Json.Nodes;
using Rendition;
using Super.Cook.Models;

namespace Super.Cook.Tests;

public sealed class ModelMeshNamesTests : IDisposable
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

    private static (CookResult Result, MemoryStore Store) Cook(ISourceRoot root, string? cache = null)
    {
        var store = new MemoryStore();
        var result = new CookPipeline(ModelCookers.All, new CookOptions(CacheFolder: cache)).Cook([root], store);
        return (result, store);
    }

    private static ModelData Model(MemoryStore store, string name)
    {
        var entry = store.Manifest.Assets[name];
        return ModelData.Read(new MemoryStream(store.Get(entry.Hash)), name);
    }

    [Fact]
    public void Static_meshes_that_share_a_material_merge_into_one_unnamed_part_by_default()
    {
        string src = Temp();
        TinyGltf.Write(src, "pair.gltf", "bark", ["left", "right"]);

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "pair.gltf");
        Assert.Single(model.Parts);
        Assert.Empty(model.MeshNames);
    }

    [Fact]
    public void Static_meshes_with_keep_meshes_stay_separate_parts_named_by_their_meshes()
    {
        string src = Temp();
        TinyGltf.Write(src, "pair.gltf", "bark", ["left", "right"]);
        File.WriteAllText(Path.Combine(src, "pair.gltf.cook.json"), "{ \"keepMeshes\": true }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "pair.gltf");
        Assert.Equal(2, model.Parts.Length);
        Assert.Equal(["left", "right"], model.MeshNames);
    }

    [Fact]
    public void Keep_meshes_names_each_part_by_the_node_that_holds_it_not_the_mesh()
    {
        string src = Temp();
        TinyGltf.Write(src, "pair.gltf", "bark", ["left", "right"]);
        var gltf = JsonNode.Parse(File.ReadAllText(Path.Combine(src, "pair.gltf")))!.AsObject();
        gltf["nodes"]![0]!.AsObject()["name"] = "Left_Hand";
        File.WriteAllText(Path.Combine(src, "pair.gltf"), gltf.ToJsonString());
        File.WriteAllText(Path.Combine(src, "pair.gltf.cook.json"), "{ \"keepMeshes\": true }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal(["Left_Hand", "right"], Model(store, "pair.gltf").MeshNames);
    }

    [Fact]
    public void A_mesh_with_two_materials_gives_two_parts_with_one_name()
    {
        string src = Temp();
        TinyGltf.Write(src, "two.gltf", "bark", ["trunk", "unused"]);
        var gltf = JsonNode.Parse(File.ReadAllText(Path.Combine(src, "two.gltf")))!.AsObject();
        gltf["meshes"]![0]!["primitives"]!.AsArray().Add(new JsonObject
        {
            ["attributes"] = new JsonObject { ["POSITION"] = 1 },
            ["material"] = 1,
        });
        gltf["materials"]!.AsArray().Add(new JsonObject { ["name"] = "leaf" });
        gltf["nodes"]!.AsArray().RemoveAt(1);
        gltf["meshes"]!.AsArray().RemoveAt(1);
        gltf["scenes"]![0]!["nodes"] = new JsonArray(0);
        File.WriteAllText(Path.Combine(src, "two.gltf"), gltf.ToJsonString());
        File.WriteAllText(Path.Combine(src, "two.gltf.cook.json"), "{ \"keepMeshes\": true }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal(["trunk", "trunk"], Model(store, "two.gltf").MeshNames);
    }

    [Fact]
    public void Keep_meshes_places_each_mesh_by_its_node_transform()
    {
        string src = Temp();
        TinyGltf.Write(src, "moved.gltf", "bark", ["left", "right", "spun"]);
        var gltf = JsonNode.Parse(File.ReadAllText(Path.Combine(src, "moved.gltf")))!.AsObject();
        var nodes = gltf["nodes"]!.AsArray();
        nodes[0]!.AsObject()["translation"] = new JsonArray(2f, 0f, 0f);
        nodes[1]!.AsObject()["translation"] = new JsonArray(0f, 3f, 0f);
        nodes[2]!.AsObject()["rotation"] = new JsonArray(0f, 0f, MathF.Sin(MathF.PI / 4f), MathF.Cos(MathF.PI / 4f));
        File.WriteAllText(Path.Combine(src, "moved.gltf"), gltf.ToJsonString());
        File.WriteAllText(Path.Combine(src, "moved.gltf.cook.json"), "{ \"keepMeshes\": true }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "moved.gltf");
        Assert.Equal(3, model.Parts.Length);

        // The cook shifts the whole model to centre it, which moves every part alike, so only the
        // gaps between parts show the placements. Left's lowest corner is (2, 0, 0) after its
        // translation and right's is (4, 3, 0), so right is (2, 3, 0) from left. Spun's triangle
        // starts at (8, 0, 0) and a quarter turn about z puts its lowest corner at (-3, 8, 0),
        // so spun is (-5, 8, 0) from left.
        var left = Lowest(model, 0);
        AssertNear(new Vector3(2f, 3f, 0f), Lowest(model, 1) - left);
        AssertNear(new Vector3(-5f, 8f, 0f), Lowest(model, 2) - left);
    }

    [Fact]
    public void Keep_meshes_that_is_not_true_or_false_is_an_error_naming_it()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "bark", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"), "{ \"keepMeshes\": \"yes\" }");

        var (result, _) = Cook(new FolderRoot(src));

        Assert.False(result.Succeeded);
        Assert.Contains("keepMeshes", Assert.Single(result.Problems, p => p.IsError).Message);
    }

    [Fact]
    public void Changing_keep_meshes_cooks_again()
    {
        string src = Temp();
        string cache = Temp();
        TinyGltf.Write(src, "pair.gltf", "bark", ["left", "right"]);

        var merged = Cook(new FolderRoot(src), cache);
        File.WriteAllText(Path.Combine(src, "pair.gltf.cook.json"), "{ \"keepMeshes\": true }");
        var named = Cook(new FolderRoot(src), cache);

        Assert.True(named.Result.Succeeded, string.Join("; ", named.Result.Problems));
        Assert.Equal((1, 0), (merged.Result.Cooked, merged.Result.FromCache));
        Assert.Equal((1, 0), (named.Result.Cooked, named.Result.FromCache));
        Assert.Equal(["left", "right"], Model(named.Store, "pair.gltf").MeshNames);
    }

    [Fact]
    public void Default_centres_the_model_on_its_bounds_and_stands_it_on_the_ground()
    {
        string src = Temp();
        WriteLifted(src, "lifted.gltf", 5f);

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var (min, max) = Bounds(Model(store, "lifted.gltf"));
        AssertNear(new Vector3(-5.5f, 0f, 0f), min);
        AssertNear(new Vector3(5.5f, 3f, 0f), max);
    }

    [Fact]
    public void Keep_origin_leaves_every_vertex_at_its_source_position()
    {
        string src = Temp();
        WriteLifted(src, "lifted.gltf", 5f);
        File.WriteAllText(Path.Combine(src, "lifted.gltf.cook.json"), "{ \"keepOrigin\": true }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var (min, max) = Bounds(Model(store, "lifted.gltf"));
        AssertNear(new Vector3(0f, 5f, 0f), min);
        AssertNear(new Vector3(11f, 8f, 0f), max);
    }

    [Fact]
    public void Keep_origin_turns_the_model_about_the_source_origin()
    {
        string src = Temp();
        WriteLifted(src, "lifted.gltf", 5f);
        File.WriteAllText(Path.Combine(src, "lifted.gltf.cook.json"), "{ \"keepOrigin\": true, \"rotateY\": 90 }");

        var (result, store) = Cook(new FolderRoot(src));

        // A quarter turn about y takes (x, y, z) to (z, y, -x), so the model lies from z = 0 to -11.
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var (min, max) = Bounds(Model(store, "lifted.gltf"));
        AssertNear(new Vector3(0f, 5f, -11f), min);
        AssertNear(new Vector3(0f, 8f, 0f), max);
    }

    [Fact]
    public void Keep_origin_scales_the_model_about_the_source_origin()
    {
        string src = Temp();
        WriteLifted(src, "lifted.gltf", 5f);
        File.WriteAllText(Path.Combine(src, "lifted.gltf.cook.json"), "{ \"keepOrigin\": true, \"targetLength\": 22 }");

        var (result, store) = Cook(new FolderRoot(src));

        // The longest axis is 11 metres, so 22 doubles everything about the origin.
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var (min, max) = Bounds(Model(store, "lifted.gltf"));
        AssertNear(new Vector3(0f, 10f, 0f), min);
        AssertNear(new Vector3(22f, 16f, 0f), max);
    }

    [Fact]
    public void Keep_origin_keeps_a_rigs_skeleton_where_its_source_put_it()
    {
        string src = Temp();
        WriteSkinned(src, "hero.gltf", ["Body_03a", "Head_05b"], hipsHeight: 2f);
        File.WriteAllText(Path.Combine(src, "hero.gltf.cook.json"), "{ \"keepOrigin\": true }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "hero.gltf");
        Assert.NotNull(model.Skeleton);
        AssertNear(new Vector3(0f, 2f, 0f), HipsAt(model));
        AssertNear(new Vector3(0f, 0f, 0f), Lowest(model, 0));
    }

    [Fact]
    public void Default_moves_a_rigs_skeleton_with_its_mesh()
    {
        string src = Temp();
        WriteSkinned(src, "hero.gltf", ["Body_03a", "Head_05b"], hipsHeight: 2f);

        var (result, store) = Cook(new FolderRoot(src));

        // The bounds run from x 0 to 5, so the model moves 2.5 metres left, and the hips with it.
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "hero.gltf");
        Assert.NotNull(model.Skeleton);
        AssertNear(new Vector3(-2.5f, 2f, 0f), HipsAt(model));
        AssertNear(new Vector3(-2.5f, 0f, 0f), Lowest(model, 0));
    }

    [Fact]
    public void Keep_origin_that_is_not_true_or_false_is_an_error_naming_it()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "bark", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"), "{ \"keepOrigin\": \"yes\" }");

        var (result, _) = Cook(new FolderRoot(src));

        Assert.False(result.Succeeded);
        Assert.Contains("keepOrigin", Assert.Single(result.Problems, p => p.IsError).Message);
    }

    [Fact]
    public void Changing_keep_origin_cooks_again()
    {
        string src = Temp();
        string cache = Temp();
        WriteLifted(src, "lifted.gltf", 5f);

        var centred = Cook(new FolderRoot(src), cache);
        File.WriteAllText(Path.Combine(src, "lifted.gltf.cook.json"), "{ \"keepOrigin\": true }");
        var origin = Cook(new FolderRoot(src), cache);

        Assert.True(origin.Result.Succeeded, string.Join("; ", origin.Result.Problems));
        Assert.Equal((1, 0), (centred.Result.Cooked, centred.Result.FromCache));
        Assert.Equal((1, 0), (origin.Result.Cooked, origin.Result.FromCache));
        AssertNear(new Vector3(0f, 5f, 0f), Bounds(Model(origin.Store, "lifted.gltf")).Min);
    }

    /// <summary>Writes the three triangles of <see cref="TinyGltf"/> raised by <paramref name="height"/> metres.</summary>
    private static void WriteLifted(string folder, string name, float height)
    {
        TinyGltf.Write(folder, name, "bark", ["left", "right", "spun"]);
        var gltf = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, name)))!.AsObject();
        foreach (var node in gltf["nodes"]!.AsArray())
        {
            node!.AsObject()["translation"] = new JsonArray(0f, height, 0f);
        }

        File.WriteAllText(Path.Combine(folder, name), gltf.ToJsonString());
    }

    private static (Vector3 Min, Vector3 Max) Bounds(ModelData model)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in model.Vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        return (min, max);
    }

    // The joint's place in the cooked model, after the normalization the skeleton carries.
    private static Vector3 HipsAt(ModelData model)
    {
        var skeleton = model.Skeleton!;
        int hips = skeleton.IndexOf("hips");
        Assert.True(hips >= 0, "The skeleton has no hips joint.");
        return Vector3.Transform(skeleton.Rest[hips].Position, skeleton.RootTransform);
    }

    private static Vector3 Lowest(ModelData model, int part)
    {
        var range = model.Parts[part];
        var lowest = new Vector3(float.MaxValue);
        for (uint i = range.FirstIndex; i < range.FirstIndex + range.IndexCount; i++)
        {
            lowest = Vector3.Min(lowest, model.Vertices[model.Indices[i]].Position);
        }

        return lowest;
    }

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Z, actual.Z, 3);
    }

    [Fact]
    public void Unskinned_mesh_under_a_node_with_no_bones_follows_that_node_as_a_joint()
    {
        string src = Temp();
        WriteHeldSword(src, "held.gltf");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "held.gltf");
        var skeleton = model.Skeleton!;
        int holder = JointNamed(skeleton, "holder");
        Assert.Equal(JointNamed(skeleton, "hips"), skeleton.Parents[holder]);
        int part = Array.IndexOf(model.MeshNames, "sword");
        Assert.True(part >= 0, "The model has no sword part.");
        foreach (var vertex in PartVertices(model, part))
        {
            Assert.Equal((uint)holder, vertex.Joints & 0xffu);
            Assert.Equal(255u, vertex.Weights & 0xffu);
        }
    }

    [Fact]
    public void Unskinned_mesh_sits_where_the_file_puts_it_at_rest()
    {
        string src = Temp();
        WriteHeldSword(src, "held.gltf");

        var (result, store) = Cook(new FolderRoot(src));

        // At rest the container's skin matrix is the identity, so its mesh stays where the file's
        // node tree put it. Without the container as a joint the mesh follows the hips instead.
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var skeleton = Model(store, "held.gltf").Skeleton!;
        var skin = new Matrix4x4[skeleton.Count];
        skeleton.SkinMatrices(skeleton.Rest, skin);
        AssertMatrix(Matrix4x4.Identity, skin[JointNamed(skeleton, "holder")]);
    }

    [Fact]
    public void Moving_the_hips_moves_the_unskinned_mesh_with_them()
    {
        string src = Temp();
        WriteHeldSword(src, "held.gltf");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var skeleton = Model(store, "held.gltf").Skeleton!;
        int hips = JointNamed(skeleton, "hips");
        int holder = JointNamed(skeleton, "holder");
        var poses = (JointPose[])skeleton.Rest.Clone();
        poses[hips].Position += new Vector3(1f, 0f, 0f);
        var skin = new Matrix4x4[skeleton.Count];
        skeleton.SkinMatrices(poses, skin);

        // The holder's own pose is unchanged, so it moves only because the hips do: its skin
        // matrix ends up the same as the hips', and that matrix is not the identity.
        AssertMatrix(skin[hips], skin[holder]);
        Assert.NotEqual(Elements(Matrix4x4.Identity), Elements(skin[holder]));
    }

    [Fact]
    public void Unskinned_mesh_under_a_bone_sits_where_the_file_puts_it_when_the_bind_pose_differs_from_rest()
    {
        string src = Temp();
        WriteHeldSword(src, "held.gltf", inHolder: false, bindHeight: 1f);
        File.WriteAllText(Path.Combine(src, "held.gltf.cook.json"), "{ \"keepOrigin\": true }");

        var (result, store) = Cook(new FolderRoot(src));

        // The hips rest 2 metres up but were bound at 1, so at rest their skin matrix lifts a
        // vertex 1 metre. The sword hangs straight off the hips, so the file puts its triangle at
        // y = 2 to 3. Skinned at rest, it must land there, not a metre higher.
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "held.gltf");
        var skeleton = model.Skeleton!;
        var skin = new Matrix4x4[skeleton.Count];
        skeleton.SkinMatrices(skeleton.Rest, skin);
        int part = Array.IndexOf(model.MeshNames, "sword");
        Assert.True(part >= 0, "The model has no sword part.");
        var lowest = new Vector3(float.MaxValue);
        foreach (var vertex in PartVertices(model, part))
        {
            Assert.Equal((uint)JointNamed(skeleton, "hips"), vertex.Joints & 0xffu);
            var placed = Vector3.Transform(vertex.Position, skin[(int)(vertex.Joints & 0xffu)] * skeleton.RootTransform);
            lowest = Vector3.Min(lowest, placed);
        }

        AssertNear(new Vector3(0f, 2f, 0f), lowest);
    }

    [Fact]
    public void Rigged_meshes_are_named_by_the_node_that_holds_them()
    {
        string src = Temp();
        WriteHeldSword(src, "held.gltf");

        var (result, store) = Cook(new FolderRoot(src));

        // The body's node is "body" and its mesh "Body_03a"; the node's name is the one kept.
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal(["body", "sword"], Model(store, "held.gltf").MeshNames.Order());
    }

    [Fact]
    public void Rigged_meshes_stay_separate_parts()
    {
        string src = Temp();
        WriteSkinned(src, "hero.gltf", ["Body_03a", "Head_05b"]);

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "hero.gltf");
        Assert.NotNull(model.Skeleton);
        Assert.Equal(2, model.Parts.Length);
        Assert.Equal(["Body_03a", "Head_05b"], model.MeshNames);
    }

    /// <summary>
    /// Writes a glTF with one skeleton joint, <c>hips</c>, and one skinned triangle per name, each
    /// a mesh of that name fully weighted to the joint. Mesh <c>i</c> sits 4 metres along x from the one before.
    /// The hips sit <paramref name="hipsHeight"/> metres up, and the inverse bind matrix undoes that,
    /// so the meshes are in their bind pose as the file has them.
    /// </summary>
    private static void WriteSkinned(string folder, string name, string[] meshNames, float hipsHeight = 0f)
    {
        var buffer = new List<byte>();
        var views = new JsonArray();
        var accessors = new JsonArray();

        int Accessor(byte[] data, int componentType, int count, string type)
        {
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = buffer.Count, ["byteLength"] = data.Length });
            buffer.AddRange(data);
            accessors.Add(new JsonObject
            {
                ["bufferView"] = views.Count - 1,
                ["componentType"] = componentType,
                ["count"] = count,
                ["type"] = type,
            });
            return accessors.Count - 1;
        }

        var nodes = new JsonArray(new JsonObject { ["name"] = "hips", ["translation"] = new JsonArray(0f, hipsHeight, 0f) });
        var meshes = new JsonArray();
        var sceneNodes = new JsonArray(0);
        for (int i = 0; i < meshNames.Length; i++)
        {
            float x = i * 4f;
            int position = Accessor(Floats([x, 0f, 0f, x + 1f, 0f, 0f, x, 1f, 0f]), 5126, 3, "VEC3");
            int joints = Accessor(Shorts([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]), 5123, 3, "VEC4");
            int weights = Accessor(Floats([1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f]), 5126, 3, "VEC4");
            meshes.Add(new JsonObject
            {
                ["name"] = meshNames[i],
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = position, ["JOINTS_0"] = joints, ["WEIGHTS_0"] = weights },
                    ["material"] = 0,
                }),
            });
            nodes.Add(new JsonObject { ["name"] = meshNames[i], ["mesh"] = i, ["skin"] = 0 });
            sceneNodes.Add(i + 1);
        }

        int inverseBind = Accessor(Floats([1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, -hipsHeight, 0f, 1f]), 5126, 1, "MAT4");
        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = sceneNodes }),
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["skins"] = new JsonArray(new JsonObject { ["joints"] = new JsonArray(0), ["inverseBindMatrices"] = inverseBind }),
            ["materials"] = new JsonArray(new JsonObject { ["name"] = "bark" }),
            ["buffers"] = new JsonArray(new JsonObject
            {
                ["byteLength"] = buffer.Count,
                ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(buffer.ToArray()),
            }),
            ["bufferViews"] = views,
            ["accessors"] = accessors,
        };
        File.WriteAllText(Path.Combine(folder, name), gltf.ToJsonString());
    }

    /// <summary>
    /// Writes a rig with a bone, <c>hips</c>, two metres up, that has a child node <c>holder</c>
    /// with no bones, one metre above the hips. A <c>sword</c> mesh with no skin hangs under the
    /// holder, and a <c>body</c> mesh is skinned fully to the hips so the file has bones. Without
    /// <paramref name="inHolder"/> the sword hangs straight off the hips and there is no holder. The
    /// hips' inverse bind matrix says they were bound <paramref name="bindHeight"/> metres up.
    /// </summary>
    private static void WriteHeldSword(string folder, string name, bool inHolder = true, float bindHeight = 2f)
    {
        var buffer = new List<byte>();
        var views = new JsonArray();
        var accessors = new JsonArray();

        int Accessor(byte[] data, int componentType, int count, string type)
        {
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = buffer.Count, ["byteLength"] = data.Length });
            buffer.AddRange(data);
            accessors.Add(new JsonObject
            {
                ["bufferView"] = views.Count - 1,
                ["componentType"] = componentType,
                ["count"] = count,
                ["type"] = type,
            });
            return accessors.Count - 1;
        }

        int swordPosition = Accessor(Floats([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f]), 5126, 3, "VEC3");
        int bodyPosition = Accessor(Floats([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f]), 5126, 3, "VEC3");
        int joints = Accessor(Shorts([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]), 5123, 3, "VEC4");
        int weights = Accessor(Floats([1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f]), 5126, 3, "VEC4");
        int inverseBind = Accessor(Floats([1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, -bindHeight, 0f, 1f]), 5126, 1, "MAT4");

        var nodes = inHolder
            ? new JsonArray
            {
                new JsonObject { ["name"] = "hips", ["translation"] = new JsonArray(0f, 2f, 0f), ["children"] = new JsonArray(1, 3) },
                new JsonObject { ["name"] = "holder", ["translation"] = new JsonArray(0f, 1f, 0f), ["children"] = new JsonArray(2) },
                new JsonObject { ["name"] = "sword", ["mesh"] = 0 },
                new JsonObject { ["name"] = "body", ["mesh"] = 1, ["skin"] = 0 },
            }
            : new JsonArray
            {
                new JsonObject { ["name"] = "hips", ["translation"] = new JsonArray(0f, 2f, 0f), ["children"] = new JsonArray(1, 2) },
                new JsonObject { ["name"] = "sword", ["mesh"] = 0 },
                new JsonObject { ["name"] = "body", ["mesh"] = 1, ["skin"] = 0 },
            };
        var meshes = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "sword",
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = swordPosition },
                    ["material"] = 0,
                }),
            },
            new JsonObject
            {
                ["name"] = "Body_03a",
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = bodyPosition, ["JOINTS_0"] = joints, ["WEIGHTS_0"] = weights },
                    ["material"] = 0,
                }),
            },
        };
        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["skins"] = new JsonArray(new JsonObject { ["joints"] = new JsonArray(0), ["inverseBindMatrices"] = inverseBind }),
            ["materials"] = new JsonArray(new JsonObject { ["name"] = "bark" }),
            ["buffers"] = new JsonArray(new JsonObject
            {
                ["byteLength"] = buffer.Count,
                ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(buffer.ToArray()),
            }),
            ["bufferViews"] = views,
            ["accessors"] = accessors,
        };
        File.WriteAllText(Path.Combine(folder, name), gltf.ToJsonString());
    }

    private static int JointNamed(Skeleton skeleton, string name)
    {
        int joint = skeleton.IndexOf(name);
        Assert.True(joint >= 0, $"The skeleton has no {name} joint.");
        return joint;
    }

    private static IEnumerable<Vertex> PartVertices(ModelData model, int part)
    {
        var range = model.Parts[part];
        for (uint i = range.FirstIndex; i < range.FirstIndex + range.IndexCount; i++)
        {
            yield return model.Vertices[model.Indices[i]];
        }
    }

    private static float[] Elements(Matrix4x4 m) =>
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    private static void AssertMatrix(Matrix4x4 expected, Matrix4x4 actual)
    {
        var want = Elements(expected);
        var got = Elements(actual);
        for (int i = 0; i < want.Length; i++)
        {
            Assert.Equal(want[i], got[i], 3);
        }
    }

    private static byte[] Floats(float[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    private static byte[] Shorts(ushort[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
}
