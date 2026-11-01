using System.Numerics;
using Rendition;

namespace Super;

/// <summary>A picture made by code, as RGBA bytes row by row. It must be square.</summary>
public sealed record GeneratedTexture(int Size, byte[] Pixels);

/// <summary>A surface made by code rather than loaded from a file, such as generated ground.</summary>
public sealed record GeneratedMesh(Vertex[] Vertices, uint[] Indices, GeneratedTexture Texture, float Roughness);

/// <summary>
/// Names what a game wants to draw. It touches no GPU and reads no files, so games run
/// the same with no window. The window loads what's named here in the background, and a
/// loading screen watches <see cref="IsLoaded"/> and <see cref="Progress"/>. Headless runs,
/// screenshots, recordings and profiles report everything loaded at once, so the speed of
/// a disk can't change what a seed does.
/// </summary>
public sealed class Content
{
    private readonly List<object> _items = [];
    private int _loaded;
    private int _batchStart;

    internal IReadOnlyList<object> Items => _items;

    /// <summary>True only in the interactive window, where loading happens over real time.</summary>
    internal bool Streaming { get; set; }

    /// <summary>Set by tests, to keep a loading screen up for as long as they need it.</summary>
    internal bool Held { get; set; }

    /// <summary>Whether everything named so far is ready to draw.</summary>
    public bool IsLoaded => !Held && (!Streaming || _loaded >= _items.Count);

    /// <summary>
    /// How far loading has got, from 0 to 1. It counts what's been named since the last
    /// time everything was loaded, so a second loading screen starts again from 0.
    /// </summary>
    public float Progress
    {
        get
        {
            if (IsLoaded)
            {
                return 1f;
            }

            return Held ? 0f : (_loaded - _batchStart) / (float)(_items.Count - _batchStart);
        }
    }

    /// <summary>Names a cooked model, by its path inside assets/cooked.</summary>
    public ModelRef Model(string path) => Add(path);

    /// <summary>Hands over a surface made by code.</summary>
    public ModelRef Mesh(GeneratedMesh mesh) => Add(mesh);

    /// <summary>Counts one more item as ready. The window calls this as it finishes each one.</summary>
    internal void ItemLoaded()
    {
        _loaded++;
        if (_loaded == _items.Count)
        {
            _batchStart = _loaded;
        }
    }

    private ModelRef Add(object item)
    {
        _items.Add(item);

        // Index 0 is the built-in cube.
        return new ModelRef(_items.Count);
    }
}
