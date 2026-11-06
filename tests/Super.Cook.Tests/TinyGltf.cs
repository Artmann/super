using System.Text.Json.Nodes;

namespace Super.Cook.Tests;

/// <summary>Writes the smallest glTF files the model cooker can read: one triangle per node, all sharing one material.</summary>
internal static class TinyGltf
{
    /// <summary>
    /// Writes <paramref name="name"/> into <paramref name="folder"/>. Node <c>i</c> holds a right
    /// triangle <c>i + 1</c> metres on a side, standing 4 metres along x from the one before, so
    /// each node can be told apart by its size. With <paramref name="bufferFile"/>, the triangles
    /// go in that file beside it instead of inside it. With <paramref name="uvs"/>, six numbers
    /// (u, v for each corner, in the file's own top-left convention), every triangle gets those UVs.
    /// </summary>
    public static void Write(string folder, string name, string materialName, string[] nodeNames, string? bufferFile = null, float[]? uvs = null)
    {
        byte[] buffer = Buffer(nodeNames.Length, uvs);
        string uri = bufferFile ?? "data:application/octet-stream;base64," + Convert.ToBase64String(buffer);
        if (bufferFile is not null)
        {
            File.WriteAllBytes(Path.Combine(folder, bufferFile), buffer);
        }

        var nodes = new JsonArray();
        var meshes = new JsonArray();
        var views = new JsonArray();
        var accessors = new JsonArray();
        var sceneNodes = new JsonArray();
        for (int i = 0; i < nodeNames.Length; i++)
        {
            float x = i * 4f;
            float size = i + 1f;
            var attributes = new JsonObject { ["POSITION"] = i };
            if (uvs is not null)
            {
                attributes["TEXCOORD_0"] = nodeNames.Length;
            }

            nodes.Add(new JsonObject { ["name"] = nodeNames[i], ["mesh"] = i });
            meshes.Add(new JsonObject
            {
                ["name"] = nodeNames[i],
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = attributes,
                    ["material"] = 0,
                }),
            });
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = i * 36, ["byteLength"] = 36 });
            accessors.Add(new JsonObject
            {
                ["bufferView"] = i,
                ["componentType"] = 5126,
                ["count"] = 3,
                ["type"] = "VEC3",
                ["min"] = new JsonArray(x, 0f, 0f),
                ["max"] = new JsonArray(x + size, size, 0f),
            });
            sceneNodes.Add(i);
        }

        // One set of UVs after the corners, shared by every triangle.
        if (uvs is not null)
        {
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = nodeNames.Length * 36, ["byteLength"] = 24 });
            accessors.Add(new JsonObject
            {
                ["bufferView"] = nodeNames.Length,
                ["componentType"] = 5126,
                ["count"] = 3,
                ["type"] = "VEC2",
            });
        }

        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = sceneNodes }),
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["materials"] = new JsonArray(new JsonObject { ["name"] = materialName }),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = buffer.Length, ["uri"] = uri }),
            ["bufferViews"] = views,
            ["accessors"] = accessors,
        };
        File.WriteAllText(Path.Combine(folder, name), gltf.ToJsonString());
    }

    private static byte[] Buffer(int triangles, float[]? uvs)
    {
        var floats = new List<float>();
        for (int i = 0; i < triangles; i++)
        {
            float x = i * 4f;
            float size = i + 1f;
            floats.AddRange([x, 0f, 0f, x + size, 0f, 0f, x, size, 0f]);
        }

        if (uvs is not null)
        {
            if (uvs.Length != 6)
            {
                throw new ArgumentException("A triangle has three corners, so it takes six UV numbers.", nameof(uvs));
            }

            floats.AddRange(uvs);
        }

        var bytes = new byte[floats.Count * 4];
        System.Buffer.BlockCopy(floats.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
