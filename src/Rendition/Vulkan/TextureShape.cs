using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The size, mips, layers and format of a <see cref="GpuTexture"/>. An array is sampled as one even with a single
/// layer, which the view type has to say. A cube has six layers.
/// </summary>
internal readonly record struct TextureShape(
    int Width, int Height, TextureKind Kind, uint MipCount, uint Layers = 1, TextureFormat Format = TextureFormat.Rgba8)
{
    /// <summary>Whether shaders sample it as an array, which is how one with more than one layer is always sampled.</summary>
    public bool Array { get; init; }

    /// <summary>Whether it is a cube of six square faces.</summary>
    public bool Cube { get; init; }

    /// <summary>The Vulkan format its bytes are in: sRGB for colour, linear otherwise.</summary>
    public Silk.NET.Vulkan.Format VulkanFormat => Format switch
    {
        TextureFormat.Bc7 => Kind == TextureKind.Color ? Silk.NET.Vulkan.Format.BC7SrgbBlock : Silk.NET.Vulkan.Format.BC7UnormBlock,
        TextureFormat.Bc5 => Silk.NET.Vulkan.Format.BC5UnormBlock,
        TextureFormat.Bc4 => Silk.NET.Vulkan.Format.BC4UnormBlock,
        TextureFormat.Bc6h => Silk.NET.Vulkan.Format.BC6HUfloatBlock,
        TextureFormat.Rgba16f => Silk.NET.Vulkan.Format.R16G16B16A16Sfloat,
        _ => Kind == TextureKind.Color ? Silk.NET.Vulkan.Format.R8G8B8A8Srgb : Silk.NET.Vulkan.Format.R8G8B8A8Unorm,
    };

    /// <summary>How its view is sampled: a cube, an array or a single 2D image.</summary>
    public ImageViewType ViewType => Cube ? ImageViewType.TypeCube : Array || Layers > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D;

    /// <summary>A sampled image of this shape that copies fill.</summary>
    public ImageCreateInfo CreateInfo => new()
    {
        SType = StructureType.ImageCreateInfo,
        Flags = Cube ? ImageCreateFlags.CreateCubeCompatibleBit : ImageCreateFlags.None,
        ImageType = ImageType.Type2D,
        Format = VulkanFormat,
        Extent = new Extent3D((uint)Width, (uint)Height, 1),
        MipLevels = MipCount,
        ArrayLayers = Layers,
        Samples = SampleCountFlags.Count1Bit,
        Tiling = ImageTiling.Optimal,
        Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
        SharingMode = SharingMode.Exclusive,
        InitialLayout = ImageLayout.Undefined,
    };
}
