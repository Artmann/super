using System.Numerics;
using Rendition.Passes;

namespace Rendition;

/// <summary>
/// Picks what each view of a frame draws: leaves out what it can't see, and swaps in simpler models where nobody will
/// notice. Keeps the camera's picked draws, and the chunk each is relit as, for the frame's recording.
/// </summary>
internal sealed class FrameDrawPicker(FrameParts parts, FrameBakes bakes, RendererStats stats)
{
    // How far a simpler model may stray from the camera's view. Shadows, which are soft, afford more.
    private const float CameraDetailError = 1f;

    private DrawItem[] _cameraDraws = [];
    // Which draw each camera draw came from. Only filled on frames that relight a chunk.
    private int[] _cameraSources = [];

    // The world-space ball round each draw this frame, for fitting sun shadows to what casts them.
    private Vector4[] _casterBounds = [];

    // The camera's view as Select made it, which decides whose depth the G-buffer pass draws first.
    private DetailView _cameraView;

    /// <summary>The placed chunk each camera draw is relit as, -1 for none. Only filled on frames that relight a chunk.</summary>
    public int[] CameraChunks { get; private set; } = [];

    /// <summary>The camera's view, whose depth the G-buffer pass draws first, or null when that is left out.</summary>
    public DetailView? DepthFirstView => parts.SceneStores.Switches.Off.HasFlag(RenderEffects.DepthFirst) ? null : _cameraView;

    /// <summary>The camera's first <paramref name="count"/> picked draws.</summary>
    public ReadOnlySpan<DrawItem> CameraDraws(int count) => _cameraDraws.AsSpan(0, count);

    /// <summary>The world-space ball round each draw that casts a shadow, for fitting the sun's shadows to them.</summary>
    public ReadOnlySpan<Vector4> Casters(ReadOnlySpan<DrawItem> draws)
    {
        if (_casterBounds.Length < draws.Length)
        {
            _casterBounds = new Vector4[draws.Length * 2];
        }

        var stores = parts.SceneStores;
        int casters = DrawSelection.Bounds(draws, stores.Meshes.DetailSlots, _casterBounds, stores.Switches.WindReach);
        return _casterBounds.AsSpan(0, casters);
    }

    /// <summary>Leaves out what each pass can't see, and swaps in simpler models where nobody will notice.</summary>
    public (int Camera, int Shadows) Select(
        in FrameView view, in Matrix4x4 viewProjection, ReadOnlySpan<DrawItem> draws, bool castsShadow)
    {
        // A draw near swapping to a finer version is drawn twice, cross-fading, so every view has room for two of each.
        if (_cameraDraws.Length < draws.Length * 2)
        {
            _cameraDraws = new DrawItem[draws.Length * 2];
        }

        var (switches, settings) = (parts.SceneStores.Switches, parts.SceneStores.Settings);
        var sunShadows = parts.SceneLighting.SunShadows;
        var (pixelsAtOneUnit, pixelsPerUnit) = Projection.PixelScale(view.Projection, (int)parts.FrameTargets.GBuffer.Extent.Height);
        var camera = new DetailView(viewProjection, view.CameraPosition, pixelsAtOneUnit, pixelsPerUnit,
            switches.DetailError(CameraDetailError, settings.MeshDetailScale), cull: switches.Culls);
        _cameraView = camera;
        int cameraCount = SelectCamera(camera, draws);
        int shadowCount = sunShadows.Select(view, draws, castsShadow, settings.MeshDetailScale);
        if (settings.MeasureGpu)
        {
            stats.DrawnTriangles = (DrawSelection.Triangles(_cameraDraws.AsSpan(0, cameraCount)), sunShadows.Triangles());
        }

        return (cameraCount, shadowCount);
    }

    /// <summary>
    /// Picks what each point and spot light's shadow tile draws, after <see cref="Select"/>, and returns how many draws
    /// there are in all. While the GPU is measured, the tiles' triangles join the shadows' and the tiles drawn and kept
    /// are counted.
    /// </summary>
    public int SelectTiles(ReadOnlySpan<DrawItem> draws)
    {
        var (shadows, settings) = (parts.SceneLighting.LocalLightShadows, parts.SceneStores.Settings);
        int count = shadows.Select(draws, settings.MeshDetailScale);
        if (settings.MeasureGpu)
        {
            stats.DrawnTriangles = (stats.DrawnTriangles.Camera, stats.DrawnTriangles.Shadows + DrawSelection.Triangles(shadows.Tiles.Drawn));
        }

        stats.ShadowTiles = settings.MeasureGpu ? shadows.Tiles.Counts : default;
        return count;
    }

    /// <summary>
    /// Picks the camera's draws. While chunks are relit, each picked draw also keeps the chunk its source is relit as.
    /// </summary>
    private int SelectCamera(in DetailView camera, ReadOnlySpan<DrawItem> draws)
    {
        var (detailSlots, switches) = (parts.SceneStores.Meshes.DetailSlots, parts.SceneStores.Switches);
        if (!bakes.Relight.Active)
        {
            return DrawSelection.Select(camera, draws, detailSlots, _cameraDraws, switches.WindReach, fades: switches.FadeRules);
        }

        if (_cameraSources.Length < _cameraDraws.Length)
        {
            _cameraSources = new int[_cameraDraws.Length];
            CameraChunks = new int[_cameraDraws.Length];
        }

        int cameraCount = DrawSelection.Select(camera, draws, detailSlots, _cameraDraws, switches.WindReach, _cameraSources, switches.FadeRules);
        for (int i = 0; i < cameraCount; i++)
        {
            CameraChunks[i] = bakes.Relight.DrawChunks[_cameraSources[i]];
        }

        return cameraCount;
    }
}
