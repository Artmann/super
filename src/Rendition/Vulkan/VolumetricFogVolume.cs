using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The volumetric fog's grid on the GPU and the two passes that fill it. volumetric_inject.comp
/// lights each cell into one of two volumes, reading the other, last frame's, as history, and
/// volumetric_integrate.comp sums each column into <see cref="Result"/>, which the lighting,
/// glass and particle passes look up. All three volumes, a <see cref="FogGrid"/>, stay in the general layout. They are
/// made the first time a frame draws the fog, at the scene's size, so a game without it spends
/// no memory on them, and freed by <see cref="Release"/> when the fog is switched off. Without
/// them <see cref="Result"/> is a single texel that lets everything through. The inject pass also reads the frame's
/// fog volumes from a small buffer per descriptor set. See "Volumetric fog" in docs/design.md.
/// </summary>
internal sealed unsafe class VolumetricFogVolume : IDisposable
{
    // How many cells across and down each invocation of the inject pass lights. Must match BLOCK in
    // volumetric_inject.comp.
    private const int InjectBlock = 2;

    private readonly VulkanContext _context;
    private readonly FogInjectSets _sets;
    private readonly FogVolumeBuffers _volumes;
    private readonly PipelineLayout _pipelineLayout;
    private readonly LivePipeline _inject;
    private readonly LivePipeline _integrate;
    private readonly FogGrid _grid;

    // Which of the two injected volumes this frame writes; the other holds last frame's.
    private int _current;

    /// <summary>The cells across, down and deep of the volumes made last; meaningless without <see cref="HasTargets"/>.</summary>
    public (int X, int Y, int Z) Size => _grid.Size;

    /// <summary>Whether last frame drew into the volume, so its cells can be reprojected.</summary>
    public bool HistoryReady { get; private set; }

    /// <summary>Whether the volumes have been made.</summary>
    public bool HasTargets => _grid.HasTargets;

    /// <summary>The summed volume, or until it's made a texel that lets everything through.</summary>
    public ImageView Result => _grid.Result;

    public VolumetricFogVolume(VulkanContext context, DescriptorSetLayout frameSetLayout, Sampler linearSampler)
    {
        _context = context;
        _sets = new FogInjectSets(context, linearSampler, FogVolumeBuffers.Binding);
        _volumes = new FogVolumeBuffers(context, _sets.Sets);
        _pipelineLayout = context.CreatePipelineLayout([frameSetLayout, _sets.Layout]);
        _inject = PipelineFactory.CreateCompute(context, "volumetric_inject.comp", _pipelineLayout);
        _integrate = PipelineFactory.CreateCompute(context, "volumetric_integrate.comp", _pipelineLayout);
        _grid = new FogGrid(context);
    }

    /// <summary>
    /// Points the inject pass at the sun's shadow cascades, the sky the fog takes its colour
    /// from where no light probe can tell, and the live light probes' two atlases, which the
    /// shader's probes.glsl reads at bindings 11 and 12. Only while no frame in flight uses them.
    /// </summary>
    public void SetSources(
        in DescriptorImageInfo sunShadow, in DescriptorImageInfo sky,
        in DescriptorImageInfo probeIrradiance, in DescriptorImageInfo probeDepth) =>
        _sets.SetSources(sunShadow, sky, probeIrradiance, probeDepth);

    /// <summary>
    /// Makes the volumes for a scene of this many pixels, after render scale, cut into cells this many
    /// pixels across and this many slices deep, replacing any
    /// there were, and starts the history again. Only while no frame in flight uses them.
    /// </summary>
    public void SetSize(int sceneWidth, int sceneHeight, int cellPixels, int slices)
    {
        var size = VolumetricFog.GridSize(sceneWidth, sceneHeight, cellPixels, slices);
        if (HasTargets && size == _grid.Size)
        {
            return;
        }

        _grid.Make(size);
        HistoryReady = false;
        var injected = _grid.Injected!;
        _sets.PointAt(injected[0].View, injected[1].View, _grid.Result);
    }

    /// <summary>
    /// Writes the fog volumes the next <see cref="Record"/> draws, at most <see cref="FogVolumeBatch.MaxVolumes"/>.
    /// The shaders read as many as the frame block's volumetricGrid.z says.
    /// </summary>
    public void WriteFogVolumes(ReadOnlySpan<FogVolumeDraw> fogVolumes) => _volumes.Write(_current, fogVolumes);

    /// <summary>A frame that doesn't draw the fog leaves nothing for the next to reproject.</summary>
    public void Skip() => HistoryReady = false;

    /// <summary>
    /// Frees the volumes, so <see cref="Result"/> is the stand-in texel again until
    /// <see cref="SetSize"/> makes them anew, with no history. Only while no frame in flight uses
    /// them, and the frame sets must be pointed at <see cref="Result"/> again.
    /// </summary>
    public void Release()
    {
        _grid.Release();
        HistoryReady = false;
    }

    /// <summary>
    /// Lights the cells and sums the columns. Must come after the sun's shadow maps, the light
    /// atlas and the cluster lists are ready for compute to read, and before anything looks the
    /// fog up. Fragment shaders may sample <see cref="Result"/> once it's done.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet)
    {
        if (!HasTargets)
        {
            throw new InvalidOperationException("The volumetric fog's volumes are made before it is drawn.");
        }

        var api = _context.Api;

        // Last frame's passes read the history and the sum, and wrote what is history now.
        api.BeginHistoryCompute(cmd, _pipelineLayout, frameSet, _sets.Sets[_current]);
        var size = _grid.Size;

        // Each of the inject pass's invocations lights a block of InjectBlock by InjectBlock cells.
        const int injectGroup = 8 * InjectBlock;
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _inject);
        api.CmdDispatch(cmd, (uint)(size.X + injectGroup - 1) / injectGroup, (uint)(size.Y + injectGroup - 1) / injectGroup, (uint)size.Z);

        api.MemoryBarrier(cmd,
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit));

        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _integrate);
        api.CmdDispatch(cmd, (uint)(size.X + 7) / 8, (uint)(size.Y + 7) / 8, 1);

        api.MemoryBarrier(cmd,
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));

        _current = 1 - _current;
        HistoryReady = true;
    }

    public void Dispose()
    {
        _grid.Dispose();
        _volumes.Dispose();
        _inject.Destroy();
        _integrate.Destroy();
        _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        _sets.Dispose();
    }
}
