using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>
/// The point and spot lights' shadow tiles in the atlas: what each tile draws, which tiles a frame draws again, and
/// their recording. A tile whose light and casters stand still keeps the depths an earlier frame drew.
/// </summary>
internal sealed class LightShadowTiles(
    Vk api, ShadowPass pass, FrameSwitches switches, (MeshStore Meshes, ShadowWorld World, MaterialTable Materials) casters)
{
    // How far a simpler model may stray in a shadow. Shadows are soft, so they can afford more than the camera.
    private const float ShadowDetailError = 2f;

    // Which tiles this frame draws, and what the others were last drawn with, so a tile whose light and casters stand
    // still keeps its depths.
    private readonly ShadowPass.TileDraws[] _ranges = new ShadowPass.TileDraws[LocalLights.MaxTiles];
    private readonly ShadowTileCache _cache = new();
    private readonly Func<MaterialHandle, int> _shapeOf = material => casters.Materials[material].ShadowShape;
    private DrawItem[] _draws = [];
    private int _drawCount;
    private int _tileCount;

    // Whether the atlas holds what earlier frames drew. On its first frame it holds nothing.
    private bool _atlasKept;

    /// <summary>How many tiles the last frame drew, and how many it kept as earlier frames drew them.</summary>
    public (int Drawn, int Kept) Counts { get; private set; }

    /// <summary>The draws of the tiles the last frame draws again, one tile after another.</summary>
    public ReadOnlySpan<DrawItem> Drawn => _draws.AsSpan(0, _drawCount);

    /// <summary>
    /// Picks what each tile can see, one tile after another, and returns how many draws there are in all. A tile the
    /// atlas still holds as it would be drawn is left out, with no draws. <paramref name="measured"/> is of
    /// <paramref name="draws"/>.
    /// </summary>
    public int Select(ReadOnlySpan<ShadowView> tiles, ReadOnlySpan<DrawItem> draws, DrawBalls measured, float meshDetailScale)
    {
        int total = 0;
        int drawn = 0;
        _cache.Begin(casters.World.Version);
        for (int tile = 0; tile < tiles.Length; tile++)
        {
            if (_draws.Length < total + draws.Length * 2)
            {
                Array.Resize(ref _draws, Math.Max(_draws.Length * 2, total + draws.Length * 2));
            }

            ref readonly var view = ref tiles[tile];
            var shadow = new DetailView(
                view.Matrix, view.Eye, view.PixelsAtOneUnit, 0f, switches.DetailError(ShadowDetailError, meshDetailScale), far: true, cull: switches.Culls);
            int count = DrawSelection.Select(shadow, draws, measured, _draws.AsSpan(total));
            bool redraw = _cache.NeedsDrawing(view, _draws.AsSpan(total, count), switches.WindReach, _shapeOf);
            _ranges[tile] = new ShadowPass.TileDraws(total, redraw ? count : 0, redraw);
            total += _ranges[tile].Count;
            drawn += redraw ? 1 : 0;
        }

        _cache.End();
        _tileCount = tiles.Length;
        _drawCount = total;
        Counts = (drawn, tiles.Length - drawn);
        return total;
    }

    /// <summary>
    /// Draws the tiles that need it into the atlas. Tiles that weren't drawn keep their depths from earlier frames, so
    /// the atlas keeps its contents from frame to frame. On its first frame it has none, and the cache remembers no
    /// tile, so every tile is drawn.
    /// </summary>
    public unsafe void Record(CommandBuffer cmd, DescriptorSet frameSet, ReadOnlySpan<ShadowView> tiles, InstanceBuffer instances)
    {
        // The lighting pass binds the atlas even when no light has a tile in it.
        var atlas = pass.Atlas.Image;
        var kept = _atlasKept ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined;
        var ranges = _ranges.AsSpan(0, _tileCount);
        if (ShadowPass.AnyRedrawn(ranges))
        {
            api.Transition(cmd, new ImageTransition(atlas, ImageAspectFlags.DepthBit,
                kept, ImageLayout.DepthAttachmentOptimal), (ShadowReadStages, SampledAccess),
                (DepthStages, DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit));
            pass.RecordTiles(cmd, frameSet, tiles, ranges,
                new SceneGeometry(_draws, casters.Meshes.MeshSlots, casters.Meshes.DetailSlots, instances));
            api.Transition(cmd, new ImageTransition(atlas, ImageAspectFlags.DepthBit,
                ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal), (DepthStages, DepthWrite), (ShadowReadStages, SampledAccess));
        }
        else if (!_atlasKept)
        {
            api.Transition(cmd, new ImageTransition(atlas, ImageAspectFlags.DepthBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal), (ShadowReadStages, SampledAccess), (ShadowReadStages, SampledAccess));
        }

        _atlasKept = true;
    }
}
