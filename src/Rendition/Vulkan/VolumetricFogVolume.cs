using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
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

    private readonly VulkanGpu _gpu;
    private readonly FogInjectSets _sets;
    private readonly FogVolumeBuffers _volumes;
    private readonly (ComputePass Inject, ComputePass Integrate) _passes;
    private readonly CommandList _commands = new();
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

    /// <summary>
    /// The passes run on the GPU layer and bind their own set through it. That set is still made outside the layer,
    /// as the sun's shadow is read through a comparing sampler and the sky through one with mips, which the layer
    /// can't make yet; the passes borrow its layout.
    /// </summary>
    public VolumetricFogVolume((VulkanContext Context, VulkanGpu Gpu) device, DescriptorSetLayout frameSetLayout, Sampler linearSampler)
    {
        var (context, gpu) = device;
        _gpu = gpu;
        _sets = new FogInjectSets(context, linearSampler, FogVolumeBuffers.Binding);
        _volumes = new FogVolumeBuffers(context, _sets.Sets);
        _passes = (
            new ComputePass(gpu, "volumetric_inject.comp", [frameSetLayout, _sets.Layout]),
            new ComputePass(gpu, "volumetric_integrate.comp", [frameSetLayout, _sets.Layout]));
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

        // The layer's barriers keep this frame's writes after last frame's reads of the history and the sum, the
        // integrate after the inject, and the fog's readers after the integrate.
        var size = _grid.Size;
        ReadOnlySpan<BindGroupId> groups = [_gpu.Adopt(frameSet), _gpu.Adopt(_sets.Sets[_current])];
        _commands.Reset();

        // Each of the inject pass's invocations lights a block of InjectBlock by InjectBlock cells.
        const int injectGroup = 8 * InjectBlock;
        _passes.Inject.Record(_commands, groups, [], ((size.X + injectGroup - 1) / injectGroup, (size.Y + injectGroup - 1) / injectGroup, size.Z));
        _passes.Integrate.Record(_commands, groups, [], ((size.X + 7) / 8, (size.Y + 7) / 8, 1));
        _gpu.Play(_commands, cmd);

        _current = 1 - _current;
        HistoryReady = true;
    }

    public void Dispose()
    {
        _grid.Dispose();
        _volumes.Dispose();
        _passes.Inject.Dispose();
        _passes.Integrate.Dispose();
        _sets.Dispose();
    }
}
