using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// A sampled image with a full mip chain and one or more layers. Its bytes arrive through
/// a staging buffer, either at once with <see cref="Upload"/> or on a later command buffer
/// through <see cref="CreateStaging"/> and <see cref="RecordUpload"/>.
/// </summary>
internal sealed unsafe class GpuTexture : IDisposable
{
    private readonly TextureFormat _format;

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
        : this(context, data.Width, data.Height, data.Kind, (uint)data.Mips.Length, 1, format: data.Format)
    {
        Upload([data]);
    }

    /// <summary>A 2D array of same-sized layers, filled right away. Waits for the GPU.</summary>
    public GpuTexture(VulkanContext context, ReadOnlySpan<TextureData> layers)
        : this(context, layers[0].Width, layers[0].Height, layers[0].Kind, (uint)layers[0].Mips.Length, (uint)layers.Length, true, layers[0].Format)
    {
        Upload(layers);
    }

    /// <summary>
    /// An empty texture. Fill it with <see cref="RecordUpload"/>. An array is sampled as
    /// one even with a single layer, which the view type has to say.
    /// </summary>
    public GpuTexture(
        VulkanContext context, int width, int height, TextureKind kind, uint mipCount, uint layers, bool array = false,
        TextureFormat format = TextureFormat.Rgba8)
    {
        _context = context;
        Width = width;
        Height = height;
        MipCount = mipCount;
        Layers = layers;
        _format = format;
        var api = context.Api;
        var vulkanFormat = VulkanFormat(format, kind);

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = vulkanFormat,
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
            Format = vulkanFormat,
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
        if (layers.Length != Layers)
        {
            throw new ArgumentException($"The texture has {Layers} layers, but {layers.Length} were given.", nameof(layers));
        }

        for (int i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            if (layer.Width != Width || layer.Height != Height || layer.Mips.Length != MipCount)
            {
                throw new ArgumentException(
                    $"Layer {i} is {layer.Width}x{layer.Height} with {layer.Mips.Length} mips, " +
                    $"but the texture is {Width}x{Height} with {MipCount} mips.", nameof(layers));
            }

            for (int level = 0; level < layer.Mips.Length; level++)
            {
                int expected = MipSize(level);
                if (layer.Mips[level].Length != expected)
                {
                    throw new ArgumentException(
                        $"Layer {i} mip {level} holds {layer.Mips[level].Length} bytes, but " +
                        $"{Math.Max(Width >> level, 1)}x{Math.Max(Height >> level, 1)} {_format} needs {expected}.", nameof(layers));
                }
            }
        }

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
    /// the image ready for fragment shaders to sample, and tracing shaders where the device can trace. Allocates nothing.
    /// </summary>
    public void RecordUpload(CommandBuffer cmd, GpuBuffer staging)
    {
        var api = _context.Api;
        var readers = _context.CanTrace
            ? PipelineStageFlags2.FragmentShaderBit | RayTracing.TraceStages
            : PipelineStageFlags2.FragmentShaderBit;
        api.Transition(cmd, _image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
            PipelineStageFlags2.None, AccessFlags2.None,
            PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit, MipCount, 0, 0, Layers);

        // One region per mip per layer. Arrays with many layers go past what the stack should hold.
        int regionCount = (int)(MipCount * Layers);
        Span<BufferImageCopy> regions = regionCount <= 64 ? stackalloc BufferImageCopy[regionCount] : new BufferImageCopy[regionCount];
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
                offset += (ulong)MipSize((int)level);
            }
        }

        fixed (BufferImageCopy* first = regions)
        {
            api.CmdCopyBufferToImage(cmd, staging.Handle, _image, ImageLayout.TransferDstOptimal, (uint)regionCount, first);
        }

        api.Transition(cmd, _image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
            readers, AccessFlags2.ShaderSampledReadBit, MipCount, 0, 0, Layers);
    }

    /// <summary>
    /// How many rows a band of a single-mip texture must hold, so each band but the last starts and ends on a block:
    /// 4 for a block-compressed format, 1 otherwise.
    /// </summary>
    public int RowUnit => _format is TextureFormat.Rgba8 or TextureFormat.Rgba16f ? 1 : 4;

    /// <summary>The bytes of the first mip's rows from the top down to <paramref name="rows"/>, a whole number of <see cref="RowUnit"/>s or the full height.</summary>
    public int RowsSize(int rows) => TextureData.LevelSize(_format, Width, rows);

    /// <summary>
    /// Records the copy of rows <paramref name="firstRow"/> on of a single-mip, single-layer texture from
    /// <paramref name="staging"/> at <paramref name="offset"/>, so a texture can go up a band a frame. The first band
    /// readies the image for copying and the last leaves it for shaders to sample, as <see cref="RecordUpload"/> does;
    /// nothing may sample it in between. A band starts on a <see cref="RowUnit"/>, and holds whole ones unless it is the last.
    /// </summary>
    public void RecordRows(CommandBuffer cmd, GpuBuffer staging, ulong offset, int firstRow, int rows, bool first, bool last)
    {
        var api = _context.Api;
        if (first)
        {
            api.Transition(cmd, _image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                PipelineStageFlags2.None, AccessFlags2.None,
                PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit, 1, 0, 0, 1);
        }

        var region = new BufferImageCopy
        {
            BufferOffset = offset,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, firstRow, 0),
            ImageExtent = new Extent3D((uint)Width, (uint)rows, 1),
        };
        api.CmdCopyBufferToImage(cmd, staging.Handle, _image, ImageLayout.TransferDstOptimal, 1, &region);

        if (last)
        {
            var readers = _context.CanTrace
                ? PipelineStageFlags2.FragmentShaderBit | RayTracing.TraceStages
                : PipelineStageFlags2.FragmentShaderBit;
            api.Transition(cmd, _image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
                readers, AccessFlags2.ShaderSampledReadBit, 1, 0, 0, 1);
        }
    }

    private int MipSize(int level) => TextureData.LevelSize(_format, Math.Max(Width >> level, 1), Math.Max(Height >> level, 1));

    private static Format VulkanFormat(TextureFormat format, TextureKind kind) => format switch
    {
        TextureFormat.Bc7 => kind == TextureKind.Color ? Format.BC7SrgbBlock : Format.BC7UnormBlock,
        TextureFormat.Bc5 => Format.BC5UnormBlock,
        TextureFormat.Bc4 => Format.BC4UnormBlock,
        TextureFormat.Bc6h => Format.BC6HUfloatBlock,
        TextureFormat.Rgba16f => Format.R16G16B16A16Sfloat,
        _ => kind == TextureKind.Color ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm,
    };

    private ulong StagingSize()
    {
        ulong perLayer = 0;
        for (int level = 0; level < MipCount; level++)
        {
            perLayer += (ulong)MipSize(level);
        }

        return perLayer * Layers;
    }

    private void Upload(ReadOnlySpan<TextureData> layers)
    {
        using var staging = CreateStaging(layers);
        _context.SubmitAndWait(cmd => RecordUpload(cmd, staging));
    }
}
