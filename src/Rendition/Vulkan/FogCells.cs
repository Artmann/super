using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// One of the volumetric fog's volumes: a 3D image its passes write and sample, in the general layout for good,
/// cleared to a value when it's made.
/// </summary>
internal sealed unsafe class FogCells : IDisposable
{
    private const Format CellFormat = Format.R16G16B16A16Sfloat;

    private readonly VulkanContext _context;
    private readonly Image _image;
    private readonly GpuAllocation _memory;

    public readonly ImageView View;

    /// <summary>Makes a volume this many cells across, down and deep, cleared to <paramref name="clear"/>. Waits for the GPU.</summary>
    public FogCells(VulkanContext context, (int X, int Y, int Z) size, ClearColorValue clear)
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
        Clear(clear);
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
            api.Transition(cmd, image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.General,
                PipelineStageFlags2.None, AccessFlags2.None,
                PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit);
            var value = clear;
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            api.CmdClearColorImage(cmd, image, ImageLayout.General, &value, 1, &range);
            api.Transition(cmd, image, ImageAspectFlags.ColorBit,
                ImageLayout.General, ImageLayout.General,
                PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit);
        });
    }
}
