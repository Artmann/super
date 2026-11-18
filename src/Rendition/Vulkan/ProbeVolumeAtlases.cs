using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The light probes' two atlases, the light each probe sees and the distances it sees, five layers each, with the
/// staging slots the folds write tiles into and the ids buffer that names each slot's probe for the blend. Both
/// atlases stay in the general layout for good, so the blend writes them and lighting samples them.
/// </summary>
internal sealed unsafe class ProbeVolumeAtlases : IDisposable
{
    /// <summary>Texels across a tile, without its border. Must match probes.glsl and probe_fold.glsl.</summary>
    public const int IrradianceTexels = 8;
    public const int DepthTexels = 16;

    /// <summary>The atlas layer of the fixed grid. Layers 0 to 3 are the cascades.</summary>
    public const int FixedGridLayer = ProbeScheduler.FixedGrid;

    /// <summary>The atlases' layers: four cascades and the fixed grid.</summary>
    public const int Layers = FixedGridLayer + 1;

    /// <summary>
    /// One probe's two bordered tiles in the staging buffer: 10 × 10 RGBA floats and 18 × 18 RG
    /// floats. Must match StagedTile in Shaders/probe_fold.glsl.
    /// </summary>
    public const int StagedTileBytes = (IrradianceTexels + 2) * (IrradianceTexels + 2) * 16 + (DepthTexels + 2) * (DepthTexels + 2) * 8;

    private const Format IrradianceFormat = Format.R16G16B16A16Sfloat;
    private const Format DepthFormat = Format.R16G16Sfloat;

    /// <summary>Everything that reads or writes the atlases after a pass changes them: lighting, the blend and the next clear.</summary>
    public static readonly (PipelineStageFlags2 Stage, AccessFlags2 Access) Readers = (
        PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
        AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);

    private readonly VulkanContext _context;
    private readonly (Image Image, GpuAllocation Memory) _irradiance;
    private readonly (Image Image, GpuAllocation Memory) _depth;

    /// <summary>
    /// Makes atlases of <paramref name="grid"/> tiles in each layer, all cleared, with <paramref name="stagingSlots"/>
    /// staging slots. With <paramref name="storage"/> compute shaders can write them. Waits for the GPU.
    /// </summary>
    public ProbeVolumeAtlases(VulkanContext context, (int Columns, int Rows) grid, int stagingSlots, bool storage)
    {
        _context = context;
        (Columns, Rows) = grid;
        StagingSlots = stagingSlots;
        const int irradianceTile = IrradianceTexels + 2;
        const int depthTile = DepthTexels + 2;
        (_irradiance, IrradianceView) = CreateAtlas((uint)(Columns * irradianceTile), (uint)(Rows * irradianceTile), IrradianceFormat, storage);
        (_depth, DepthView) = CreateAtlas((uint)(Columns * depthTile), (uint)(Rows * depthTile), DepthFormat, storage);
        Staging = new GpuBuffer(context, (ulong)stagingSlots * StagedTileBytes,
            BufferUsageFlags.TransferSrcBit |
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
        Ids = new GpuBuffer(context, (ulong)stagingSlots * 8,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
    }

    /// <summary>Tiles across each atlas layer.</summary>
    public int Columns { get; }

    /// <summary>Tile rows in each atlas layer.</summary>
    public int Rows { get; }

    /// <summary>The most probes one blend takes.</summary>
    public int StagingSlots { get; }

    public ImageView IrradianceView { get; }

    public ImageView DepthView { get; }

    /// <summary>Folded tiles waiting to be blended into the atlases, <see cref="StagedTileBytes"/> each.</summary>
    public GpuBuffer Staging { get; }

    /// <summary>Two ints a staging slot: the layer and the slot of the probe it blends into.</summary>
    public GpuBuffer Ids { get; }

    /// <summary>
    /// Whether any cascade tile has been written since the atlases were last cleared. Until one is,
    /// surfaces skip the cascades.
    /// </summary>
    public bool CascadesWritten { get; private set; }

    /// <summary>Enough tiles for a cascade or the fixed grid, laid out near square.</summary>
    public static (int Columns, int Rows) Grid(ProbeQuality quality)
    {
        int tiles = Math.Max(ProbeCascades.Layouts(quality)[0].Total, ProbeGrid.MaxProbes);
        int columns = (int)Math.Ceiling(Math.Sqrt(tiles));
        int rows = (tiles + columns - 1) / columns;
        return (columns, rows);
    }

    /// <summary>Clears every layer of both atlases, so every probe is uncaptured again.</summary>
    public void RecordClear(CommandBuffer cmd)
    {
        var api = _context.Api;
        api.MemoryBarrier(cmd,
            (PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit),
            (PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit));
        var black = new ClearColorValue(0f, 0f, 0f, 0f);
        var cleared = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, Layers);
        api.CmdClearColorImage(cmd, _irradiance.Image, ImageLayout.General, &black, 1, &cleared);
        api.CmdClearColorImage(cmd, _depth.Image, ImageLayout.General, &black, 1, &cleared);
        api.MemoryBarrier(cmd, (PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit), Readers);
        CascadesWritten = false;
    }

    /// <summary>Writes each probe's layer and slot into <paramref name="ids"/>, two ints a probe, and notes when a cascade is written.</summary>
    public void PackIds(IReadOnlyList<ProbeId> batch, int* ids)
    {
        for (int i = 0; i < batch.Count; i++)
        {
            var id = batch[i];
            if (id.Cascade < 0 || id.Cascade >= Layers || id.Slot < 0 || id.Slot >= Columns * Rows)
            {
                throw new ArgumentOutOfRangeException(nameof(batch), id, "There is no such probe in the atlases.");
            }

            ids[i * 2] = id.Cascade;
            ids[i * 2 + 1] = id.Slot;
            if (id.Cascade < FixedGridLayer)
            {
                CascadesWritten = true;
            }
        }
    }

    public void Dispose()
    {
        var api = _context.Api;
        api.DestroyImageView(_context.Device, IrradianceView, null);
        api.DestroyImageView(_context.Device, DepthView, null);
        _context.DestroyArrayImage(_irradiance);
        _context.DestroyArrayImage(_depth);
        Staging.Dispose();
        Ids.Dispose();
    }

    // A five-layer atlas, cleared: black, and every probe uncaptured.
    private ((Image Image, GpuAllocation Memory) Image, ImageView View) CreateAtlas(uint width, uint height, Format format, bool storage)
    {
        var api = _context.Api;
        var usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit;
        if (storage)
        {
            usage |= ImageUsageFlags.StorageBit;
        }

        var made = _context.CreateArrayImage((width, height, Layers), format, usage);
        var view = _context.CreateArrayView(made.Image, (format, ImageAspectFlags.ColorBit), (0, Layers));
        var image = made.Image;
        _context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.General,
                PipelineStageFlags2.None, AccessFlags2.None, PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit,
                layerCount: Layers);
            var black = new ClearColorValue(0f, 0f, 0f, 0f);
            var cleared = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, Layers);
            api.CmdClearColorImage(cmd, image, ImageLayout.General, &black, 1, &cleared);
            api.MemoryBarrier(cmd, (PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit), Readers);
        });
        return (made, view);
    }
}
