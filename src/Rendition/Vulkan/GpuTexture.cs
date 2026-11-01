using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// A sampled image with a full mip chain and one or more layers. Its bytes arrive through
/// a staging buffer, either at once with <see cref="Upload"/> or on a later command buffer
/// through <see cref="CreateStaging"/> and <see cref="RecordUpload"/>.
/// </summary>
internal sealed unsafe class GpuTexture : IDisposable
{
    private const int BytesPerTexel = 4;

    private readonly VulkanContext _context;
    private readonly Image _image;
    private readonly GpuAllocation _allocation;

    public readonly ImageView View;
    public readonly int Width;
    public readonly int Height;
    public readonly uint MipCount;
    public readonly uint Layers;

    /// <summary>A single 2D texture, filled right away. Waits for the GPU.</summary>
    public GpuTexture(VulkanContext context, TextureData data)
        : this(context, data.Width, data.Height, data.Kind, (uint)data.Mips.Length, 1)
    {
        Upload([data]);
    }

    /// <summary>A 2D array of same-sized layers, filled right away. Waits for the GPU.</summary>
    public GpuTexture(VulkanContext context, ReadOnlySpan<TextureData> layers)
        : this(context, layers[0].Width, layers[0].Height, layers[0].Kind, (uint)layers[0].Mips.Length, (uint)layers.Length, true)
    {
        Upload(layers);
    }

    /// <summary>
    /// An empty texture. Fill it with <see cref="RecordUpload"/>. An array is sampled as
    /// one even with a single layer, which the view type has to say.
    /// </summary>
    public GpuTexture(VulkanContext context, int width, int height, TextureKind kind, uint mipCount, uint layers, bool array = false)
    {
        _context = context;
        Width = width;
        Height = height;
        MipCount = mipCount;
        Layers = layers;
        var api = context.Api;
        var format = kind == TextureKind.Color ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = mipCount,
            ArrayLayers = layers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        api.CreateImage(context.Device, &imageInfo, null, out _image).Check();

        api.GetImageMemoryRequirements(context.Device, _image, out var requirements);
        _allocation = context.Memory.AllocateImage(requirements, MemoryPropertyFlags.DeviceLocalBit);
        api.BindImageMemory(context.Device, _image, _allocation.Memory, _allocation.Offset).Check();

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _image,
            ViewType = array || layers > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, mipCount, 0, layers),
        };
        api.CreateImageView(context.Device, &viewInfo, null, out View).Check();
    }

    public void Dispose()
    {
        _context.Api.DestroyImageView(_context.Device, View, null);
        _context.Api.DestroyImage(_context.Device, _image, null);
        _context.Memory.Free(_allocation);
    }

    /// <summary>
    /// Fills a staging buffer with every layer's mips in order, laid out as
    /// <see cref="RecordUpload"/> expects. Each layer must match this texture's size and mips.
    /// </summary>
    public GpuBuffer CreateStaging(ReadOnlySpan<TextureData> layers)
    {
        var staging = GpuBuffer.CreateHostVisible(_context, StagingSize(), BufferUsageFlags.TransferSrcBit);
        ulong offset = 0;
        foreach (var layer in layers)
        {
            foreach (byte[] mip in layer.Mips)
            {
                mip.CopyTo(new Span<byte>((byte*)staging.Mapped + offset, mip.Length));
                offset += (ulong)mip.Length;
            }
        }

        return staging;
    }

    /// <summary>
    /// Records the copy from a staging buffer made by <see cref="CreateStaging"/>, leaving
    /// the image ready for fragment shaders to sample. Allocates nothing.
    /// </summary>
    public void RecordUpload(CommandBuffer cmd, GpuBuffer staging)
    {
        var api = _context.Api;
        api.Transition(cmd, _image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
            PipelineStageFlags2.None, AccessFlags2.None,
            PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit, MipCount, 0, 0, Layers);

        int regionCount = (int)(MipCount * Layers);
        var regions = stackalloc BufferImageCopy[regionCount];
        ulong offset = 0;
        int region = 0;
        for (uint layer = 0; layer < Layers; layer++)
        {
            for (uint level = 0; level < MipCount; level++)
            {
                uint width = (uint)Math.Max(Width >> (int)level, 1);
                uint height = (uint)Math.Max(Height >> (int)level, 1);
                regions[region++] = new BufferImageCopy
                {
                    BufferOffset = offset,
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level, layer, 1),
                    ImageExtent = new Extent3D(width, height, 1),
                };
                offset += width * height * BytesPerTexel;
            }
        }

        api.CmdCopyBufferToImage(cmd, staging.Handle, _image, ImageLayout.TransferDstOptimal, (uint)regionCount, regions);

        api.Transition(cmd, _image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit, MipCount, 0, 0, Layers);
    }

    private ulong StagingSize()
    {
        ulong perLayer = 0;
        for (int level = 0; level < MipCount; level++)
        {
            perLayer += (ulong)(Math.Max(Width >> level, 1) * Math.Max(Height >> level, 1) * BytesPerTexel);
        }

        return perLayer * Layers;
    }

    private void Upload(ReadOnlySpan<TextureData> layers)
    {
        using var staging = CreateStaging(layers);
        _context.SubmitAndWait(cmd => RecordUpload(cmd, staging));
    }
}
