using System.Numerics;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Rendition.Vulkan;

/// <summary>One level of an Rgba16f cube map, in ShaderReadOnlyOptimal: its image, which mip and that mip's size.</summary>
internal readonly record struct CubeLevel(Image Image, int Mip, uint Size);

/// <summary>
/// A cube level's average, from the middles of its six faces: the 2 x 2 texels round each middle, which is what a
/// linear lookup straight at it blends. Read back to the CPU, so call it while loading or baking, not every frame.
/// </summary>
internal static unsafe class CubeCentres
{
    private const int Texels = 6 * 2 * 2;
    private const int Floats = Texels * 4;
    private const ulong Bytes = Floats * sizeof(ushort);

    /// <summary>Averages two levels in one trip to the GPU.</summary>
    public static (Vector3 First, Vector3 Second) Average(VulkanContext context, CubeLevel first, CubeLevel second)
    {
        using var buffer = GpuBuffer.CreateHostVisible(context, 2 * Bytes, BufferUsageFlags.TransferDstBit);
        var api = context.Api;
        var handle = buffer.Handle;
        context.SubmitAndWait(cmd =>
        {
            Copy(api, cmd, first, handle, 0);
            Copy(api, cmd, second, handle, Bytes);
        });

        var halves = new ReadOnlySpan<Half>(buffer.Mapped, 2 * Floats);
        return (Mean(halves[..Floats]), Mean(halves[Floats..]));
    }

    private static void Copy(Vk api, CommandBuffer cmd, CubeLevel level, Buffer buffer, ulong offset)
    {
        api.Transition(cmd, level.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit,
            baseMip: (uint)level.Mip, layerCount: 6);

        int corner = (int)level.Size / 2 - 1;
        var region = new BufferImageCopy
        {
            BufferOffset = offset,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level.Mip, 0, 6),
            ImageOffset = new Offset3D(corner, corner, 0),
            ImageExtent = new Extent3D(2, 2, 1),
        };
        api.CmdCopyImageToBuffer(cmd, level.Image, ImageLayout.TransferSrcOptimal, buffer, 1, &region);

        api.Transition(cmd, level.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.CopyBit, AccessFlags2.None,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
            baseMip: (uint)level.Mip, layerCount: 6);
    }

    private static Vector3 Mean(ReadOnlySpan<Half> rgba)
    {
        var sum = Vector3.Zero;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            sum += new Vector3((float)rgba[i], (float)rgba[i + 1], (float)rgba[i + 2]);
        }

        return sum / (rgba.Length / 4);
    }
}
