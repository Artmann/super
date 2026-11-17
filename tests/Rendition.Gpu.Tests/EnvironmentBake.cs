using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition.Gpu.Tests;

internal static class EnvironmentBake
{
    /// <summary>Renders the studio, or a clear sky over its floor, into the cube maps, and waits for it.</summary>
    public static void Bake(this EnvironmentMap environment, VulkanContext context, EnvironmentDescription description, SkySun sky)
    {
        environment.SetDescription(description);
        context.SubmitAndWait(cmd => environment.Record(cmd, sky));
    }
}
