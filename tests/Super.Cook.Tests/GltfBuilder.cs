using System.Text.Json.Nodes;

namespace Super.Cook.Tests;

/// <summary>Writes a small glTF with its buffer inlined, accessor by accessor.</summary>
internal sealed class GltfBuilder
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

    public static byte[] Floats(float[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    public static byte[] Shorts(ushort[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
}
