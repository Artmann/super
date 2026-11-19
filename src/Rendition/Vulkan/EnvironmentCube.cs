using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// An Rgba16f cube map for the environment: a view that samples all its mips, one storage view per mip for compute
/// shaders to write, and the layout changes between writing a level and sampling it.
/// </summary>
internal sealed unsafe class EnvironmentCube : IDisposable
{
    private const Format CubeFormat = Format.R16G16B16A16Sfloat;

    private readonly VulkanContext _context;
    private readonly GpuAllocation _memory;
    private readonly ImageView[] _mips;

    public Image Image { get; }
    public uint Size { get; }
    public uint MipCount { get; }

    /// <summary>A cube view over every mip, for sampling.</summary>
    public ImageView Sampled { get; }

    public EnvironmentCube(VulkanContext context, uint size, uint mipCount)
    {
        _context = context;
        Size = size;
        MipCount = mipCount;
        var api = context.Api;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            Flags = ImageCreateFlags.CreateCubeCompatibleBit,
            ImageType = ImageType.Type2D,
            Format = CubeFormat,
            Extent = new Extent3D(size, size, 1),
            MipLevels = mipCount,
            ArrayLayers = 6,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit
                | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        api.CreateImage(context.Device, &imageInfo, null, out var image).Check();
        Image = image;

        api.GetImageMemoryRequirements(context.Device, image, out var requirements);
        _memory = context.Memory.AllocateImage(requirements, MemoryPropertyFlags.DeviceLocalBit);
        api.BindImageMemory(context.Device, image, _memory.Memory, _memory.Offset).Check();

        Sampled = CreateView(mipCount);
        _mips = new ImageView[mipCount];
        for (uint mip = 0; mip < mipCount; mip++)
        {
            _mips[mip] = CreateView(ImageViewType.Type2DArray, mip, 1);
        }
    }

    /// <summary>A view of one mip's six faces as an array, for a compute shader to write.</summary>
    public ImageView Storage(int mip) => _mips[mip];

    public CubeLevel Level(int mip) => new(Image, mip, Size >> mip);

    /// <summary>A cube view of the first <paramref name="mipCount"/> mips. The caller destroys it.</summary>
    public ImageView CreateView(uint mipCount) => CreateView(ImageViewType.TypeCube, 0, mipCount);

    /// <summary>Takes a level a compute shader just wrote from General to ready for any shader to sample.</summary>
    public void Ready(CommandBuffer cmd, int mip) =>
        _context.Api.Transition(cmd,
            new ImageTransition(Image, ImageAspectFlags.ColorBit, ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal) { BaseMip = (uint)mip, LayerCount = 6 },
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit));

    /// <summary>
    /// Fills every mip below the sharpest by halving the one above with linear blits, then leaves them all ready to
    /// sample. The sharpest level arrives in General, as a compute shader left it.
    /// </summary>
    public void BuildMips(CommandBuffer cmd)
    {
        var api = _context.Api;
        api.Transition(cmd,
            new ImageTransition(Image, ImageAspectFlags.ColorBit, ImageLayout.General, ImageLayout.TransferSrcOptimal) { LayerCount = 6 },
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.BlitBit, AccessFlags2.TransferReadBit));
        api.Transition(cmd,
            new ImageTransition(Image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.TransferDstOptimal)
            {
                BaseMip = 1,
                MipCount = MipCount - 1,
                LayerCount = 6,
            },
            (PipelineStageFlags2.AllCommandsBit, AccessFlags2.None),
            (PipelineStageFlags2.BlitBit, AccessFlags2.TransferWriteBit));

        int width = (int)Size;
        for (uint mip = 1; mip < MipCount; mip++)
        {
            int next = Math.Max(1, width / 2);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip - 1, 0, 6),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip, 0, 6),
            };
            blit.SrcOffsets[1] = new Offset3D(width, width, 1);
            blit.DstOffsets[1] = new Offset3D(next, next, 1);
            api.CmdBlitImage(cmd, Image, ImageLayout.TransferSrcOptimal, Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);
            api.Transition(cmd,
                new ImageTransition(Image, ImageAspectFlags.ColorBit, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal) { BaseMip = mip, LayerCount = 6 },
                (PipelineStageFlags2.BlitBit, AccessFlags2.TransferWriteBit),
                (PipelineStageFlags2.BlitBit, AccessFlags2.TransferReadBit));
            width = next;
        }

        api.Transition(cmd,
            new ImageTransition(Image, ImageAspectFlags.ColorBit, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal) { MipCount = MipCount, LayerCount = 6 },
            (PipelineStageFlags2.BlitBit, AccessFlags2.TransferWriteBit),
            (PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit));
    }

    public void Dispose()
    {
        var api = _context.Api;
        foreach (var view in _mips)
        {
            api.DestroyImageView(_context.Device, view, null);
        }

        api.DestroyImageView(_context.Device, Sampled, null);
        api.DestroyImage(_context.Device, Image, null);
        _context.Memory.Free(_memory);
    }

    private ImageView CreateView(ImageViewType type, uint baseMip, uint mipCount)
    {
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = type,
            Format = CubeFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, baseMip, mipCount, 0, 6),
        };
        _context.Api.CreateImageView(_context.Device, &viewInfo, null, out var view).Check();
        return view;
    }
}
