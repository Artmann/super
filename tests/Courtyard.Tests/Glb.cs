using System.Numerics;
using System.Text.Json.Nodes;

namespace Courtyard.Tests;

/// <summary>Reads one mesh's positions and first UVs straight out of a binary glTF file, for checking the model.</summary>
internal static class Glb
{
    public static (List<Vector3> Positions, List<Vector2> Uvs) Mesh(string path, string name)
    {
        var bytes = File.ReadAllBytes(path);
        int jsonLength = BitConverter.ToInt32(bytes, 12);
        var json = JsonNode.Parse(bytes.AsSpan(20, jsonLength))!;
        int binStart = 20 + jsonLength + 8;

        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var mesh = json["meshes"]!.AsArray().Single(m => (string?)m!["name"] == name)!;
        foreach (var primitive in mesh["primitives"]!.AsArray())
        {
            var attributes = primitive!["attributes"]!;
            var position = Floats(json, bytes, binStart, (int)attributes["POSITION"]!, 3);
            var uv = Floats(json, bytes, binStart, (int)attributes["TEXCOORD_0"]!, 2);
            for (int i = 0; i < position.Length / 3; i++)
            {
                positions.Add(new Vector3(position[i * 3], position[i * 3 + 1], position[i * 3 + 2]));
                uvs.Add(new Vector2(uv[i * 2], uv[i * 2 + 1]));
            }
        }

        return (positions, uvs);
    }

    /// <summary>The box round every position in every mesh of a binary glTF file, as its accessors declare it.</summary>
    public static (Vector3 Min, Vector3 Max) Bounds(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int jsonLength = BitConverter.ToInt32(bytes, 12);
        var json = JsonNode.Parse(bytes.AsSpan(20, jsonLength))!;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in json["meshes"]!.AsArray())
        {
            foreach (var primitive in mesh!["primitives"]!.AsArray())
            {
                var accessor = json["accessors"]![(int)primitive!["attributes"]!["POSITION"]!]!;
                var low = accessor["min"]!.AsArray().Select(value => (float)value!).ToArray();
                var high = accessor["max"]!.AsArray().Select(value => (float)value!).ToArray();
                min = Vector3.Min(min, new Vector3(low[0], low[1], low[2]));
                max = Vector3.Max(max, new Vector3(high[0], high[1], high[2]));
            }
        }

        return (min, max);
    }

    private static float[] Floats(JsonNode json, byte[] bytes, int binStart, int index, int width)
    {
        var accessor = json["accessors"]![index]!;
        if ((int)accessor["componentType"]! != 5126)
        {
            throw new InvalidDataException($"Accessor {index} isn't floats.");
        }

        var view = json["bufferViews"]![(int)accessor["bufferView"]!]!;
        int count = (int)accessor["count"]!;
        int start = binStart + ((int?)view["byteOffset"] ?? 0) + ((int?)accessor["byteOffset"] ?? 0);
        int stride = (int?)view["byteStride"] ?? width * 4;
        var floats = new float[count * width];
        for (int i = 0; i < count; i++)
        {
            for (int k = 0; k < width; k++)
            {
                floats[i * width + k] = BitConverter.ToSingle(bytes, start + i * stride + k * 4);
            }
        }

        return floats;
    }
}
