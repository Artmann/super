using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Rows <paramref name="FirstRow"/> to <paramref name="FirstRow"/> + <paramref name="Rows"/> of a single-mip texture,
/// whose bytes sit at <paramref name="Offset"/> in a staging buffer. The band that <paramref name="Opens"/> the upload
/// readies the image for copying and the one that <paramref name="Closes"/> it leaves it for shaders to sample.
/// </summary>
internal readonly record struct TextureRows(ulong Offset, int FirstRow, int Rows, bool Opens, bool Closes);

/// <summary>
/// Fills a <see cref="GpuTexture"/> from a staging buffer: every layer and mip at once, or a single-mip texture a band
/// of rows at a time.
/// </summary>
internal static unsafe class TextureUploads
{
    /// <summary>
    /// Fills a staging buffer with every layer's mips in order, laid out as
    /// <see cref="RecordUpload"/> expects. Each layer must match the texture's size and mips.
    /// </summary>
    public static GpuBuffer CreateStaging(this GpuTexture texture, ReadOnlySpan<TextureData> layers)
    {
        CheckLayers(texture, layers);
        var staging = GpuBuffer.CreateHostVisible(texture.Context, StagingSize(texture), BufferUsageFlags.TransferSrcBit);
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
    /// the image ready for fragment and compute shaders to sample, and tracing shaders where the device can trace.
    /// Allocates nothing.
    /// </summary>
    public static void RecordUpload(this GpuTexture texture, CommandBuffer cmd, GpuBuffer staging)
    {
        var api = texture.Context.Api;
        var readers = texture.Context.CanTrace
            ? PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit | RayTracing.TraceStages
            : PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit;
        api.Transition(cmd, new ImageTransition(texture.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal) { MipCount = texture.MipCount, LayerCount = texture.Layers },
            (PipelineStageFlags2.None, AccessFlags2.None),
            (PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit));

        // One region per mip per layer. Arrays with many layers go past what the stack should hold.
        int regionCount = (int)(texture.MipCount * texture.Layers);
        Span<BufferImageCopy> regions = regionCount <= 64 ? stackalloc BufferImageCopy[regionCount] : new BufferImageCopy[regionCount];
        FillRegions(texture, regions);
        fixed (BufferImageCopy* first = regions)
        {
            api.CmdCopyBufferToImage(cmd, staging.Handle, texture.Image, ImageLayout.TransferDstOptimal, (uint)regionCount, first);
        }

        api.Transition(cmd, new ImageTransition(texture.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal) { MipCount = texture.MipCount, LayerCount = texture.Layers },
            (PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit),
            (readers, AccessFlags2.ShaderSampledReadBit));
    }

    /// <summary>
    /// Records the copy of a band of rows of a single-mip, single-layer texture from <paramref name="staging"/>, so a
    /// texture can go up a band a frame. The first band readies the image for copying and the last leaves it for
    /// shaders to sample, as <see cref="RecordUpload"/> does; nothing may sample it in between. A band starts on a
    /// <see cref="GpuTexture.RowUnit"/>, and holds whole ones unless it is the last.
    /// </summary>
    public static void RecordRows(this GpuTexture texture, CommandBuffer cmd, GpuBuffer staging, in TextureRows band)
    {
        var api = texture.Context.Api;
        if (band.Opens)
        {
            api.Transition(cmd, new ImageTransition(texture.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.TransferDstOptimal),
                (PipelineStageFlags2.None, AccessFlags2.None),
                (PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit));
        }

        var region = new BufferImageCopy
        {
            BufferOffset = band.Offset,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, band.FirstRow, 0),
            ImageExtent = new Extent3D((uint)texture.Width, (uint)band.Rows, 1),
        };
        api.CmdCopyBufferToImage(cmd, staging.Handle, texture.Image, ImageLayout.TransferDstOptimal, 1, &region);

        if (band.Closes)
        {
            var readers = texture.Context.CanTrace
                ? PipelineStageFlags2.FragmentShaderBit | RayTracing.TraceStages
                : PipelineStageFlags2.FragmentShaderBit;
            api.Transition(cmd, new ImageTransition(texture.Image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal),
                (PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit),
                (readers, AccessFlags2.ShaderSampledReadBit));
        }
    }

    private static void CheckLayers(GpuTexture texture, ReadOnlySpan<TextureData> layers)
    {
        if (layers.Length != texture.Layers)
        {
            throw new ArgumentException($"The texture has {texture.Layers} layers, but {layers.Length} were given.", nameof(layers));
        }

        for (int i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            if (layer.Width != texture.Width || layer.Height != texture.Height || layer.Mips.Length != texture.MipCount)
            {
                throw new ArgumentException(
                    $"Layer {i} is {layer.Width}x{layer.Height} with {layer.Mips.Length} mips, " +
                    $"but the texture is {texture.Width}x{texture.Height} with {texture.MipCount} mips.", nameof(layers));
            }

            for (int level = 0; level < layer.Mips.Length; level++)
            {
                int expected = texture.MipSize(level);
                if (layer.Mips[level].Length != expected)
                {
                    throw new ArgumentException(
                        $"Layer {i} mip {level} holds {layer.Mips[level].Length} bytes, but " +
                        $"{Math.Max(texture.Width >> level, 1)}x{Math.Max(texture.Height >> level, 1)} {texture.Format} needs {expected}.",
                        nameof(layers));
                }
            }
        }
    }

    // One region per mip per layer, in the order CreateStaging lays them out.
    private static void FillRegions(GpuTexture texture, Span<BufferImageCopy> regions)
    {
        ulong offset = 0;
        int region = 0;
        for (uint layer = 0; layer < texture.Layers; layer++)
        {
            for (uint level = 0; level < texture.MipCount; level++)
            {
                uint width = (uint)Math.Max(texture.Width >> (int)level, 1);
                uint height = (uint)Math.Max(texture.Height >> (int)level, 1);
                regions[region++] = new BufferImageCopy
                {
                    BufferOffset = offset,
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level, layer, 1),
                    ImageExtent = new Extent3D(width, height, 1),
                };
                offset += (ulong)texture.MipSize((int)level);
            }
        }
    }

    private static ulong StagingSize(GpuTexture texture)
    {
        ulong perLayer = 0;
        for (int level = 0; level < texture.MipCount; level++)
        {
            perLayer += (ulong)texture.MipSize(level);
        }

        return perLayer * texture.Layers;
    }
}
