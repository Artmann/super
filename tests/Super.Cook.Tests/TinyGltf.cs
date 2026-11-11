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
    /// With <paramref name="uvs2"/>, six more, every triangle gets them as its second UV set, TEXCOORD_1,
    /// and when <paramref name="uvs"/> is null they're its first set too, since files always have a first.
    /// With <paramref name="nodeMaterials"/>, node <c>i</c> uses the material of that name instead, listed after <paramref name="materialName"/>.
    /// </summary>
    public static void Write(string folder, string name, string materialName, string[] nodeNames, string? bufferFile = null, float[]? uvs = null, float[]? uvs2 = null,
        string[]? nodeMaterials = null)
    {
        List<string> materialNames = [materialName, .. (nodeMaterials ?? []).Where(material => material != materialName).Distinct()];
        uvs ??= uvs2;
        byte[] buffer = Buffer(nodeNames.Length, uvs, uvs2);
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

            if (uvs2 is not null)
            {
                attributes["TEXCOORD_1"] = nodeNames.Length + 1;
            }

            nodes.Add(new JsonObject { ["name"] = nodeNames[i], ["mesh"] = i });
            meshes.Add(new JsonObject
            {
                ["name"] = nodeNames[i],
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = attributes,
                    ["material"] = nodeMaterials is null ? 0 : materialNames.IndexOf(nodeMaterials[i]),
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

        // The second set after the first.
        if (uvs2 is not null)
        {
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = nodeNames.Length * 36 + 24, ["byteLength"] = 24 });
            accessors.Add(new JsonObject
            {
                ["bufferView"] = nodeNames.Length + 1,
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
            ["materials"] = new JsonArray([.. materialNames.Select(material => (JsonNode)new JsonObject { ["name"] = material })]),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = buffer.Length, ["uri"] = uri }),
            ["bufferViews"] = views,
            ["accessors"] = accessors,
        };
        File.WriteAllText(Path.Combine(folder, name), gltf.ToJsonString());
    }

    /// <summary>
    /// Writes one mesh of two 1-metre quads facing +z, quad 0 from x 0 to 1 and quad 1 from x 2 to 3, each two
    /// triangles sharing their corners. Corners go (x, 0), (x + 1, 0), (x + 1, 1), (x, 1). Each quad's first UVs cover
    /// 0 to 1. With <paramref name="uvs2"/>, sixteen numbers (u, v for each of the eight corners, in the file's own
    /// top-left convention), the mesh gets them as its second UV set, TEXCOORD_1.
    /// </summary>
    public static void WriteQuads(string folder, string name, string materialName, float[]? uvs2)
    {
        if (uvs2 is not null && uvs2.Length != 16)
        {
            throw new ArgumentException("Two quads have eight corners, so they take sixteen UV numbers.", nameof(uvs2));
        }

        var floats = new List<float>();
        for (int quad = 0; quad < 2; quad++)
        {
            float x = quad * 2f;
            floats.AddRange([x, 0f, 0f, x + 1f, 0f, 0f, x + 1f, 1f, 0f, x, 1f, 0f]);
        }

        for (int corner = 0; corner < 8; corner++)
        {
            floats.AddRange([0f, 0f, 1f]);
        }

        for (int quad = 0; quad < 2; quad++)
        {
            floats.AddRange([0f, 1f, 1f, 1f, 1f, 0f, 0f, 0f]);
        }

        floats.AddRange(uvs2 ?? []);
        uint[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7];
        var bytes = new byte[floats.Count * 4 + indices.Length * 4];
        System.Buffer.BlockCopy(floats.ToArray(), 0, bytes, 0, floats.Count * 4);
        System.Buffer.BlockCopy(indices, 0, bytes, floats.Count * 4, indices.Length * 4);

        var views = new JsonArray();
        var accessors = new JsonArray();
        int offset = 0;
        void Add(int length, int count, string type, int componentType, JsonObject? extra = null)
        {
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = length });
            var accessor = new JsonObject { ["bufferView"] = views.Count - 1, ["componentType"] = componentType, ["count"] = count, ["type"] = type };
            foreach (var (key, value) in extra ?? [])
            {
                accessor[key] = value?.DeepClone();
            }

            accessors.Add(accessor);
            offset += length;
        }

        Add(96, 8, "VEC3", 5126, new JsonObject { ["min"] = new JsonArray(0f, 0f, 0f), ["max"] = new JsonArray(3f, 1f, 0f) });
        Add(96, 8, "VEC3", 5126);
        Add(64, 8, "VEC2", 5126);
        var attributes = new JsonObject { ["POSITION"] = 0, ["NORMAL"] = 1, ["TEXCOORD_0"] = 2 };
        if (uvs2 is not null)
        {
            Add(64, 8, "VEC2", 5126);
            attributes["TEXCOORD_1"] = 3;
        }

        int indexAccessor = accessors.Count;
        Add(48, 12, "SCALAR", 5125);

        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = new JsonArray(new JsonObject { ["name"] = "quads", ["mesh"] = 0 }),
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["name"] = "quads",
                ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = attributes, ["indices"] = indexAccessor, ["material"] = 0 }),
            }),
            ["materials"] = new JsonArray(new JsonObject { ["name"] = materialName }),
            ["buffers"] = new JsonArray(new JsonObject
            {
                ["byteLength"] = bytes.Length,
                ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes),
            }),
            ["bufferViews"] = views,
            ["accessors"] = accessors,
        };
        File.WriteAllText(Path.Combine(folder, name), gltf.ToJsonString());
    }

    /// <summary>
    /// Writes one mesh of two 1-metre quads facing +z, each its own primitive with its own material: quad 0 from x 0 to 1
    /// with <paramref name="first"/>'s, and quad 1 from x 2 to 3 with <paramref name="second"/>'s. Corners go (x, 0),
    /// (x + 1, 0), (x + 1, 1), (x, 1), and each quad's first UVs cover 0 to 1. A quad with second UVs, eight numbers
    /// (u, v for each corner), gets them as TEXCOORD_1; one with null has none.
    /// </summary>
    public static void WriteQuadPair(string folder, string name, (string Material, float[]? Uvs2) first, (string Material, float[]? Uvs2) second) =>
        WriteQuadRow(folder, name, first, second);

    /// <summary>
    /// As <see cref="WriteQuadPair"/>, for any number of quads: quad <c>i</c> stands from x <c>2i</c> to <c>2i + 1</c>, in
    /// its own primitive. Quads that name the same material share it, and materials are listed in the order first named.
    /// </summary>
    public static void WriteQuadRow(string folder, string name, params (string Material, float[]? Uvs2)[] quads)
    {
        var floats = new List<float>();
        var views = new JsonArray();
        var accessors = new JsonArray();
        var primitives = new JsonArray();
        var materials = new JsonArray();

        int Add(float[] values, string type, int count, JsonObject? extra = null)
        {
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = floats.Count * 4, ["byteLength"] = values.Length * 4 });
            floats.AddRange(values);
            var accessor = new JsonObject { ["bufferView"] = views.Count - 1, ["componentType"] = 5126, ["count"] = count, ["type"] = type };
            foreach (var (key, value) in extra ?? [])
            {
                accessor[key] = value?.DeepClone();
            }

            accessors.Add(accessor);
            return accessors.Count - 1;
        }

        var indexViews = new List<int>();
        var materialNames = new List<string>();
        int quad = 0;
        foreach (var (material, uvs2) in quads)
        {
            if (uvs2 is not null && uvs2.Length != 8)
            {
                throw new ArgumentException("A quad has four corners, so it takes eight UV numbers.", nameof(quads));
            }

            float x = quad * 2f;
            var attributes = new JsonObject
            {
                ["POSITION"] = Add([x, 0f, 0f, x + 1f, 0f, 0f, x + 1f, 1f, 0f, x, 1f, 0f], "VEC3", 4,
                    new JsonObject { ["min"] = new JsonArray(x, 0f, 0f), ["max"] = new JsonArray(x + 1f, 1f, 0f) }),
                ["NORMAL"] = Add([0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f], "VEC3", 4),
                ["TEXCOORD_0"] = Add([0f, 1f, 1f, 1f, 1f, 0f, 0f, 0f], "VEC2", 4),
            };
            if (uvs2 is not null)
            {
                attributes["TEXCOORD_1"] = Add(uvs2, "VEC2", 4);
            }

            if (!materialNames.Contains(material))
            {
                materialNames.Add(material);
                materials.Add(new JsonObject { ["name"] = material });
            }

            primitives.Add(new JsonObject { ["attributes"] = attributes, ["material"] = materialNames.IndexOf(material) });
            indexViews.Add(primitives.Count - 1);
            quad++;
        }

        // The indices go last, after every float, as unsigned ints.
        uint[] indices = [0, 1, 2, 0, 2, 3];
        int indexOffset = floats.Count * 4;
        var bytes = new byte[indexOffset + indices.Length * 4];
        System.Buffer.BlockCopy(floats.ToArray(), 0, bytes, 0, indexOffset);
        System.Buffer.BlockCopy(indices, 0, bytes, indexOffset, indices.Length * 4);
        views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = indexOffset, ["byteLength"] = indices.Length * 4 });
        accessors.Add(new JsonObject { ["bufferView"] = views.Count - 1, ["componentType"] = 5125, ["count"] = indices.Length, ["type"] = "SCALAR" });
        foreach (int primitive in indexViews)
        {
            primitives[primitive]!["indices"] = accessors.Count - 1;
        }

        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = new JsonArray(new JsonObject { ["name"] = "quads", ["mesh"] = 0 }),
            ["meshes"] = new JsonArray(new JsonObject { ["name"] = "quads", ["primitives"] = primitives }),
            ["materials"] = materials,
            ["buffers"] = new JsonArray(new JsonObject
            {
                ["byteLength"] = bytes.Length,
                ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes),
            }),
            ["bufferViews"] = views,
            ["accessors"] = accessors,
        };
        File.WriteAllText(Path.Combine(folder, name), gltf.ToJsonString());
    }

    /// <summary>
    /// Writes one mesh of <paramref name="count"/> triangles facing +z that share no corner, so each is a lightmap chart
    /// of its own: half a metre on a side, in a square grid of rows a metre apart. It has first UVs but no second set.
    /// </summary>
    public static void WriteScattered(string folder, string name, string materialName, int count)
    {
        int row = (int)Math.Ceiling(Math.Sqrt(count));
        var floats = new float[count * 3 * 8];
        for (int t = 0; t < count; t++)
        {
            float x = t % row;
            float y = t / row;
            float[] positions = [x, y, 0f, x + 0.5f, y, 0f, x, y + 0.5f, 0f];
            positions.CopyTo(floats, t * 9);
            for (int corner = 0; corner < 3; corner++)
            {
                floats[count * 9 + (t * 3 + corner) * 3 + 2] = 1f;
            }

            float[] uvs = [0f, 1f, 1f, 1f, 0f, 0f];
            uvs.CopyTo(floats, count * 18 + t * 6);
        }

        int vertices = count * 3;
        var indices = Enumerable.Range(0, vertices).Select(index => (uint)index).ToArray();
        var bytes = new byte[floats.Length * 4 + indices.Length * 4];
        System.Buffer.BlockCopy(floats, 0, bytes, 0, floats.Length * 4);
        System.Buffer.BlockCopy(indices, 0, bytes, floats.Length * 4, indices.Length * 4);

        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = new JsonArray(new JsonObject { ["name"] = "scattered", ["mesh"] = 0 }),
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["name"] = "scattered",
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = 0, ["NORMAL"] = 1, ["TEXCOORD_0"] = 2 },
                    ["indices"] = 3,
                    ["material"] = 0,
                }),
            }),
            ["materials"] = new JsonArray(new JsonObject { ["name"] = materialName }),
            ["buffers"] = new JsonArray(new JsonObject
            {
                ["byteLength"] = bytes.Length,
                ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes),
            }),
            ["bufferViews"] = new JsonArray(
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = vertices * 12 },
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = vertices * 12, ["byteLength"] = vertices * 12 },
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = vertices * 24, ["byteLength"] = vertices * 8 },
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = vertices * 32, ["byteLength"] = vertices * 4 }),
            ["accessors"] = new JsonArray(
                new JsonObject
                {
                    ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = vertices, ["type"] = "VEC3",
                    ["min"] = new JsonArray(0f, 0f, 0f), ["max"] = new JsonArray(row - 0.5f, (count - 1) / row + 0.5f, 0f),
                },
                new JsonObject { ["bufferView"] = 1, ["componentType"] = 5126, ["count"] = vertices, ["type"] = "VEC3" },
                new JsonObject { ["bufferView"] = 2, ["componentType"] = 5126, ["count"] = vertices, ["type"] = "VEC2" },
                new JsonObject { ["bufferView"] = 3, ["componentType"] = 5125, ["count"] = vertices, ["type"] = "SCALAR" }),
        };
        File.WriteAllText(Path.Combine(folder, name), gltf.ToJsonString());
    }

    private static byte[] Buffer(int triangles, float[]? uvs, float[]? uvs2)
    {
        var floats = new List<float>();
        for (int i = 0; i < triangles; i++)
        {
            float x = i * 4f;
            float size = i + 1f;
            floats.AddRange([x, 0f, 0f, x + size, 0f, 0f, x, size, 0f]);
        }

        foreach (float[]? set in new[] { uvs, uvs2 })
        {
            if (set is null)
            {
                continue;
            }

            if (set.Length != 6)
            {
                throw new ArgumentException("A triangle has three corners, so it takes six UV numbers.", nameof(uvs));
            }

            floats.AddRange(set);
        }

        var bytes = new byte[floats.Count * 4];
        System.Buffer.BlockCopy(floats.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
