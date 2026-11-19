using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// An image's layout change for <see cref="VulkanExtensions.Transition"/>: which image and aspect, from which layout
/// to which, and which mips and layers. One mip and one layer from the first unless set.
/// </summary>
internal readonly record struct ImageTransition(Image Image, ImageAspectFlags Aspect, ImageLayout From, ImageLayout To)
{
    /// <summary>The first mip changed.</summary>
    public uint BaseMip { get; init; }

    /// <summary>How many mips change, from <see cref="BaseMip"/>.</summary>
    public uint MipCount { get; init; } = 1;

    /// <summary>The first layer changed.</summary>
    public uint BaseLayer { get; init; }

    /// <summary>How many layers change, from <see cref="BaseLayer"/>.</summary>
    public uint LayerCount { get; init; } = 1;
}
