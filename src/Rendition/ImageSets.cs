using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>Writes the sets of sampled images the fullscreen passes read.</summary>
internal static unsafe class ImageSets
{
    /// <summary>Points a set's bindings, from 0 in order, at <paramref name="images"/>.</summary>
    public static void Write(VulkanContext context, DescriptorSet set, ReadOnlySpan<DescriptorImageInfo> images)
    {
        var writes = stackalloc WriteDescriptorSet[images.Length];
        fixed (DescriptorImageInfo* imagesPtr = images)
        {
            for (int i = 0; i < images.Length; i++)
            {
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = (uint)i,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = &imagesPtr[i],
                };
            }

            context.Api.UpdateDescriptorSets(context.Device, (uint)images.Length, writes, 0, null);
        }
    }
}
