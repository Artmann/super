using Rendition.Passes;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>
/// Remembers what each tile of the point and spot lights' shadow atlas was last drawn with, so a frame redraws only the
/// tiles whose light moved, whose place in the atlas changed, or whose casters changed. A tile with a caster that moves
/// by itself, such as a skinned character, or a plant in the wind, is drawn every frame.
/// </summary>
internal sealed class ShadowTileCache
{
    private Dictionary<(int X, int Y, int Size), ulong> _drawn = [];
    private Dictionary<(int X, int Y, int Size), ulong> _drawing = [];
    private ulong _world;

    /// <summary>
    /// Starts a frame. <paramref name="world"/> stands for everything every tile depends on, such as which meshes and
    /// textures there are, and a change to it forgets every tile.
    /// </summary>
    public void Begin(ulong world)
    {
        if (world != _world)
        {
            _drawn.Clear();
            _world = world;
        }

        _drawing.Clear();
    }

    /// <summary>
    /// Whether the tile has to be drawn this frame, given the casters it sees, the frame's <paramref name="wind"/>, and
    /// each material's <see cref="Vulkan.ResourceTable.ShadowShapeOf"/>. A tile that isn't drawn keeps last frame's
    /// depths.
    /// </summary>
    public bool NeedsDrawing(in ShadowView view, ReadOnlySpan<DrawItem> casters, WindReach wind, Func<MaterialHandle, int> shape)
    {
        var hash = new XxHash3();
        var place = new TilePlace(view.Matrix, view.X, view.Y, view.Size);
        hash.Append(MemoryMarshal.AsBytes(new ReadOnlySpan<TilePlace>(in place)));
        foreach (ref readonly var caster in casters)
        {
            // Not kept, so the tile is drawn again next frame too, and once more when the caster stops.
            if (MovesByItself(caster, wind))
            {
                return true;
            }

            var key = new CasterKey(caster, shape(caster.Material));
            hash.Append(MemoryMarshal.AsBytes(new ReadOnlySpan<CasterKey>(in key)));
        }

        ulong drawnWith = hash.GetCurrentHashAsUInt64();
        var rect = (view.X, view.Y, view.Size);
        _drawing[rect] = drawnWith;
        return !_drawn.TryGetValue(rect, out ulong last) || last != drawnWith;
    }

    // A skin or the wind moves a caster's vertices on the GPU from frame to frame, with nothing in the draw
    // changing.
    private static bool MovesByItself(in DrawItem caster, WindReach wind) =>
        caster.Bones.Count > 0 || wind.Pad(caster.Material) > 0f;

    /// <summary>Ends a frame: the tiles it drew or kept are the ones the next frame can keep.</summary>
    public void End() => (_drawn, _drawing) = (_drawing, _drawn);

    // Where a tile is and what it looks through, laid out with no padding so its bytes hash the same every frame.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TilePlace(Matrix4x4 matrix, int x, int y, int size)
    {
        private readonly Matrix4x4 _matrix = matrix;
        private readonly int _x = x;
        private readonly int _y = y;
        private readonly int _size = size;
    }

    // What of a caster shapes its shadow, laid out with no padding, as a draw's nullable fields have padding whose bytes
    // aren't promised to be the same from frame to frame.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CasterKey(in DrawItem draw, int shape)
    {
        private readonly Matrix4x4 _transform = draw.Transform;
        private readonly int _mesh = draw.Mesh.Index;
        private readonly uint _firstIndex = draw.FirstIndex;
        private readonly uint _indexCount = draw.IndexCount;
        private readonly int _material = draw.Material.Index;
        private readonly int _shape = shape;
        private readonly float _dither = draw.Dither;
        private readonly SwimMotion _swim = draw.Swim;
    }
}
