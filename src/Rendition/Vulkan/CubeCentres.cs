using System.Numerics;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Rendition.Vulkan;

/// <summary>One level of an Rgba16f cube map, in ShaderReadOnlyOptimal: its image, which mip and that mip's size.</summary>
internal readonly record struct CubeLevel(Image Image, int Mip, uint Size);

/// <summary>
/// A cube level's average, from the middles of its six faces: the 2 x 2 texels round each middle, which is what a
/// linear lookup straight at it blends. <see cref="Record"/> copies them into a buffer the CPU can read, in the same
/// command buffer as the work that drew the level, and <see cref="Mean"/> averages what it copied.
/// </summary>
internal static unsafe class CubeCentres
{
    /// <summary>The halves one level's copy takes: 6 faces of 2 x 2 texels of 4 channels.</summary>
    public const int Halves = 6 * 2 * 2 * 4;

    public const ulong Bytes = Halves * sizeof(ushort);

    /// <summary>
    /// Copies the level's face middles to <paramref name="buffer"/> at <paramref name="offset"/>, after the compute
    /// work that drew it, and leaves it ready for any shader to sample.
    /// </summary>
    public static void Record(Vk api, CommandBuffer cmd, CubeLevel level, Buffer buffer, ulong offset)
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
            PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit,
            baseMip: (uint)level.Mip, layerCount: 6);
    }

    /// <summary>The mean colour of one level's copied texels.</summary>
    public static Vector3 Mean(ReadOnlySpan<Half> rgba)
    {
        var sum = Vector3.Zero;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            sum += new Vector3((float)rgba[i], (float)rgba[i + 1], (float)rgba[i + 2]);
        }

        return sum / (rgba.Length / 4);
    }
}
