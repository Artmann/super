using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>The sizes and formats the <see cref="Baker"/>'s parts share, and how they copy images back.</summary>
internal static unsafe class BakeGpu
{
    /// <summary>Floats a texel has in <see cref="Baker.ReadTexels"/>: position xyz and the covering rectangle's number from 1 (0 where uncovered), geometric normal xyz, shading normal xyz, valid.</summary>
    public const int TexelFloats = 11;

    /// <summary>Rays sent from each texel to look for the backs of walls.</summary>
    public const int ProbeRays = 16;

    /// <summary>How far each of those rays reaches, in metres.</summary>
    public const float ProbeRayLength = 0.05f;

    /// <summary>The share of a texel's rays that may hit backs before it is invalid: more than half.</summary>
    public const float InvalidShare = 0.5f;

    /// <summary>How far along the normal the rays start, in metres, so they don't hit the texel's own surface.</summary>
    public const float ProbeLift = 0.001f;

    /// <summary>The side of the multisampled tile drawn at a time without conservative rasterization, in texels.</summary>
    public const int TileSize = 512;

    /// <summary>How far along the normal paths and shadow rays start, in metres, so they don't hit their own surface.</summary>
    public const float TraceLift = 0.001f;

    /// <summary>Texels traced round the rectangles on a page, so the edge texels conservative drawing adds get light too.</summary>
    public const int TraceMargin = 2;

    /// <summary>Probes traced side by side in one pass, each a row of its six faces.</summary>
    public const int ProbesPerBatch = 64;

    /// <summary>A chunk's transfer sums: the sky's for red, green and blue, the sun's, and the sun's colour.</summary>
    public const int TransferSums = 5;

    /// <summary>The floats of transfer each texel of <see cref="TracedPage.Transfer"/> holds, four a sum.</summary>
    public const int TransferFloats = TransferSums * 4;

    /// <summary>The side of the compute shaders' work groups.</summary>
    public const uint GroupSize = 8;

    public const Format PositionFormat = Format.R32G32B32A32Sfloat;
    public const Format SumFormat = Format.R32G32B32A32Sfloat;
    public const Format NormalFormat = Format.R16G16B16A16Sfloat;
    public const Format ValidityFormat = Format.R8Unorm;

    /// <summary>A compute shader's set layout bindings, one descriptor of each type in turn, from binding 0.</summary>
    public static DescriptorSetLayoutBinding[] Bindings(params ReadOnlySpan<DescriptorType> types)
    {
        var bindings = new DescriptorSetLayoutBinding[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding((uint)i, types[i], 1, ShaderStageFlags.ComputeBit);
        }

        return bindings;
    }

    /// <summary>A copy of a region of an image into a buffer at <paramref name="offset"/>.</summary>
    public static BufferImageCopy Region(ulong offset, (int X, int Y, int Width, int Height) region) => new()
    {
        BufferOffset = offset,
        ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        ImageOffset = new Offset3D(region.X, region.Y, 0),
        ImageExtent = new Extent3D((uint)region.Width, (uint)region.Height, 1),
    };

    /// <summary>Records a copy out of an image in the general layout into a buffer.</summary>
    public static void Copy(Vk api, CommandBuffer cmd, RenderTarget target, GpuBuffer buffer, BufferImageCopy region) =>
        api.CmdCopyImageToBuffer(cmd, target.Image, ImageLayout.General, buffer.Handle, 1, &region);

    /// <summary>The offset rounded up to 16 bytes, where each image's part of a readback starts.</summary>
    public static ulong Align(ulong offset) => (offset + 15) & ~15ul;
}
