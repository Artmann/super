using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// A 3D image of half-float RGBA cells, in the general layout for good, that passes write and sample: the volumetric
/// fog's volumes, the grade's table and the artist's colour tables. It's cleared to a value or filled when it's made.
/// </summary>
internal sealed unsafe class CellVolume : IDisposable
{
    private const Format CellFormat = Format.R16G16B16A16Sfloat;

    private readonly VulkanContext _context;
    private readonly Image _image;
    private readonly GpuAllocation _memory;

    public readonly ImageView View;

    /// <summary>Makes a volume this many cells across, down and deep, cleared to <paramref name="clear"/>. Waits for the GPU.</summary>
    public CellVolume(VulkanContext context, (int X, int Y, int Z) size, ClearColorValue clear)
        : this(context, size)
    {
        Clear(clear);
    }

    /// <summary>
    /// Makes a volume this many cells across, down and deep, filled with <paramref name="cells"/>: four halves a cell,
    /// across first, then down, then deep. Waits for the GPU.
    /// </summary>
    public CellVolume(VulkanContext context, (int X, int Y, int Z) size, ReadOnlySpan<Half> cells)
        : this(context, size)
    {
        int expected = size.X * size.Y * size.Z * 4;
        if (cells.Length != expected)
        {
            throw new ArgumentException($"A volume of {size.X} by {size.Y} by {size.Z} cells takes {expected} halves, not {cells.Length}.", nameof(cells));
        }

        Fill(size, cells);
    }

    private CellVolume(VulkanContext context, (int X, int Y, int Z) size)
    {
        _context = context;
        var api = context.Api;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type3D,
            Format = CellFormat,
            Extent = new Extent3D((uint)size.X, (uint)size.Y, (uint)size.Z),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        api.CreateImage(context.Device, &imageInfo, null, out _image).Check();
        api.GetImageMemoryRequirements(context.Device, _image, out var requirements);
        _memory = context.Memory.AllocateImage(requirements, MemoryPropertyFlags.DeviceLocalBit);
        api.BindImageMemory(context.Device, _image, _memory.Memory, _memory.Offset).Check();

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _image,
            ViewType = ImageViewType.Type3D,
            Format = CellFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        api.CreateImageView(context.Device, &viewInfo, null, out View).Check();
    }

    public void Dispose()
    {
        var api = _context.Api;
        api.DestroyImageView(_context.Device, View, null);
        api.DestroyImage(_context.Device, _image, null);
        _context.Memory.Free(_memory);
    }

    // Moves the new image into the general layout and clears it, ready for the passes to read and write.
    private void Clear(ClearColorValue clear)
    {
        var api = _context.Api;
        var image = _image;
        _context.SubmitAndWait(cmd =>
        {
            ToGeneral(cmd, image, PipelineStageFlags2.ClearBit);
            var value = clear;
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            api.CmdClearColorImage(cmd, image, ImageLayout.General, &value, 1, &range);
            ReadyForShaders(cmd, image, PipelineStageFlags2.ClearBit);
        });
    }

    // The same, copying the cells in from a staging buffer instead.
    private void Fill((int X, int Y, int Z) size, ReadOnlySpan<Half> cells)
    {
        var api = _context.Api;
        var image = _image;
        using var staging = GpuBuffer.CreateHostVisible(_context, (ulong)(cells.Length * sizeof(Half)), BufferUsageFlags.TransferSrcBit);
        cells.CopyTo(new Span<Half>(staging.Mapped, cells.Length));
        var buffer = staging.Handle;
        _context.SubmitAndWait(cmd =>
        {
            ToGeneral(cmd, image, PipelineStageFlags2.CopyBit);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D((uint)size.X, (uint)size.Y, (uint)size.Z),
            };
            api.CmdCopyBufferToImage(cmd, buffer, image, ImageLayout.General, 1, &region);
            ReadyForShaders(cmd, image, PipelineStageFlags2.CopyBit);
        });
    }

    private void ToGeneral(CommandBuffer cmd, Image image, PipelineStageFlags2 writer) =>
        _context.Api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.General),
            (PipelineStageFlags2.None, AccessFlags2.None),
            (writer, AccessFlags2.TransferWriteBit));

    private void ReadyForShaders(CommandBuffer cmd, Image image, PipelineStageFlags2 writer) =>
        _context.Api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit,
            ImageLayout.General, ImageLayout.General),
            (writer, AccessFlags2.TransferWriteBit),
            (PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit));
}
