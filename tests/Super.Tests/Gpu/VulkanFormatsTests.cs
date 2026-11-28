using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Super.Tests.GpuLayer;

public class VulkanFormatsTests
{
    [Fact]
    public void Every_swapchain_format_the_window_can_pick_maps_to_a_gpu_format()
    {
        Assert.NotEmpty(Swapchain.Formats);
        foreach (var format in Swapchain.Formats)
        {
            Assert.Equal(format, VulkanFormats.Of(VulkanFormats.ToGpu(format)));
        }
    }

    [Fact]
    public void Every_gpu_format_maps_to_vulkan_and_back_to_itself()
    {
        foreach (var format in Enum.GetValues<GpuFormat>())
        {
            Assert.Equal(format, VulkanFormats.ToGpu(VulkanFormats.Of(format)));
        }
    }
}
