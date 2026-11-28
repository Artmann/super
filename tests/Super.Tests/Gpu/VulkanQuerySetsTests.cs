using Rendition.Gpu.Vulkan;

namespace Super.Tests.GpuLayer;

public class VulkanQuerySetsTests
{
    [Fact]
    public void Ticks_become_nanoseconds_by_the_gpus_period()
    {
        // A GPU whose clock ticks every 52.08 ns: 1000 ticks are 52,080 ns.
        Assert.Equal(52_080ul, VulkanQuerySets.Nanoseconds(1000, 52.08m));
    }

    [Fact]
    public void Ticks_past_a_doubles_exact_range_keep_every_nanosecond()
    {
        // 2^60 + 1 ticks at 1 ns each; a double would round away the last one.
        ulong ticks = (1ul << 60) + 1;

        Assert.Equal(ticks, VulkanQuerySets.Nanoseconds(ticks, 1m));
    }
}
