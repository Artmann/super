using Rendition.Vulkan;

namespace Super.Tests;

public class RayTracingSupportTests
{
    private static readonly HashSet<string> All = [.. RayTracingSupport.Extensions];

    [Fact]
    public void Traces_when_every_extension_and_feature_is_there()
    {
        Assert.Null(RayTracingSupport.Missing(All, bufferDeviceAddress: true, accelerationStructure: true, rayQuery: true, disabled: false));
    }

    [Fact]
    public void Names_the_missing_extension()
    {
        var withoutRayQuery = All.Where(name => name != "VK_KHR_ray_query").ToHashSet();

        Assert.Equal("no VK_KHR_ray_query", RayTracingSupport.Missing(withoutRayQuery, true, true, true, false));
    }

    [Fact]
    public void Names_a_missing_feature_even_with_the_extensions()
    {
        Assert.Equal("no bufferDeviceAddress", RayTracingSupport.Missing(All, false, true, true, false));
    }

    [Fact]
    public void Names_each_missing_ray_tracing_feature()
    {
        Assert.Equal("no accelerationStructure", RayTracingSupport.Missing(All, true, false, true, false));
        Assert.Equal("no rayQuery", RayTracingSupport.Missing(All, true, true, false, false));
    }

    [Fact]
    public void Asks_for_the_ray_tracing_features_only_when_both_extensions_are_listed()
    {
        Assert.True(RayTracingSupport.CanAskForFeatures(new HashSet<string> { "VK_KHR_acceleration_structure", "VK_KHR_ray_query" }));
        Assert.False(RayTracingSupport.CanAskForFeatures(new HashSet<string> { "VK_KHR_acceleration_structure" }));
        Assert.False(RayTracingSupport.CanAskForFeatures(new HashSet<string> { "VK_KHR_ray_query" }));
        Assert.False(RayTracingSupport.CanAskForFeatures(new HashSet<string> { "VK_KHR_swapchain" }));
    }

    [Fact]
    public void Says_when_turned_off()
    {
        Assert.Equal("turned off with --no-trace", RayTracingSupport.Missing(All, true, true, true, disabled: true));
    }
}
