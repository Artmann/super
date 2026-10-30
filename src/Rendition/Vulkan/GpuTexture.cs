using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>A sampled image with a full mip chain, uploaded once at load time.</summary>
internal sealed unsafe class GpuTexture : IDisposable
{
    private readonly VulkanContext _context;
    private readonly Image _image;
    private readonly DeviceMemory _memory;

    public readonly ImageView View;

    public GpuTexture(VulkanContext context, TextureData data)
    {
        _context = context;
        var api = context.Api;
        var format = data.Kind == TextureKind.Color ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;
        uint mipCount = (uint)data.Mips.Length;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)data.Width, (uint)data.Height, 1),
            MipLevels = mipCount,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        api.CreateImage(context.Device, &imageInfo, null, out _image).Check();

        api.GetImageMemoryRequirements(context.Device, _image, out var requirements);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = context.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        api.AllocateMemory(context.Device, &allocInfo, null, out _memory).Check();
        api.BindImageMemory(context.Device, _image, _memory, 0).Check();

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, mipCount, 0, 1),
        };
        api.CreateImageView(context.Device, &viewInfo, null, out View).Check();

        Upload(data);
    }

    public void Dispose()
    {
        _context.Api.DestroyImageView(_context.Device, View, null);
        _context.Api.DestroyImage(_context.Device, _image, null);
        _context.Api.FreeMemory(_context.Device, _memory, null);
    }

    private void Upload(TextureData data)
    {
        ulong totalSize = 0;
        foreach (byte[] mip in data.Mips)
        {
            totalSize += (ulong)mip.Length;
        }

        using var staging = GpuBuffer.CreateHostVisible(_context, totalSize, BufferUsageFlags.TransferSrcBit);
        var regions = new BufferImageCopy[data.Mips.Length];
        ulong offset = 0;
        for (int level = 0; level < data.Mips.Length; level++)
        {
            byte[] mip = data.Mips[level];
            mip.CopyTo(new Span<byte>((byte*)staging.Mapped + offset, mip.Length));
            regions[level] = new BufferImageCopy
            {
                BufferOffset = offset,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level, 0, 1),
                ImageExtent = new Extent3D(
                    (uint)Math.Max(data.Width >> level, 1), (uint)Math.Max(data.Height >> level, 1), 1),
            };
            offset += (ulong)mip.Length;
        }

        var api = _context.Api;
        var image = _image;
        var source = staging.Handle;
        uint mipCount = (uint)data.Mips.Length;
        _context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                PipelineStageFlags2.None, AccessFlags2.None,
                PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit, mipCount);

            fixed (BufferImageCopy* regionsPtr = regions)
            {
                api.CmdCopyBufferToImage(cmd, source, image, ImageLayout.TransferDstOptimal, mipCount, regionsPtr);
            }

            api.Transition(cmd, image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit, mipCount);
        });
    }
}
