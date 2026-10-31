using System.Numerics;
using Rendition;

namespace Super;

/// <summary>A picture made by code, as RGBA bytes row by row. It must be square.</summary>
public sealed record GeneratedTexture(int Size, byte[] Pixels);

/// <summary>A surface made by code rather than loaded from a file, such as generated ground.</summary>
public sealed record GeneratedMesh(Vertex[] Vertices, uint[] Indices, GeneratedTexture Texture, float Roughness);

/// <summary>
/// Names what a game wants to draw. It touches no GPU and reads no files, so games run
/// the same with no window. The window loads what's named here when it's first needed.
/// </summary>
public sealed class Content
{
    private readonly List<object> _items = [];

    internal IReadOnlyList<object> Items => _items;

    /// <summary>Names a cooked model, by its path inside assets/cooked.</summary>
    public ModelRef Model(string path) => Add(path);

    /// <summary>Hands over a surface made by code.</summary>
    public ModelRef Mesh(GeneratedMesh mesh) => Add(mesh);

    private ModelRef Add(object item)
    {
        _items.Add(item);

        // Index 0 is the built-in cube.
        return new ModelRef(_items.Count);
    }
}
