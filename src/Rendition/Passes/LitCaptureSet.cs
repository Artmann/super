using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>The four textures a lit capture's set 2 reads. See <see cref="LitCaptureSet"/>.</summary>
internal readonly record struct LitCaptureTextures(
    DescriptorImageInfo Irradiance, DescriptorImageInfo Depth, DescriptorImageInfo SunShadow, DescriptorImageInfo Environment);

/// <summary>
/// The set 2 of a capture that lights what it draws, as <see cref="ReflectionCapturePass"/> and the planar reflection
/// do: the light probes' atlases and the sun shadow map at <see cref="ProbeCapturePass"/>'s bindings, and the
/// environment's irradiance.
/// </summary>
internal static unsafe class LitCaptureSet
{
    private const uint EnvironmentBinding = 15;

    public static readonly DescriptorSetLayoutBinding[] Bindings =
        [.. ProbeCapturePass.AtlasBindings, new(EnvironmentBinding, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit)];

    /// <summary>Points a set laid out by <see cref="Bindings"/> at its four textures.</summary>
    public static void Write(VulkanContext context, DescriptorSet set, LitCaptureTextures textures)
    {
        var (irradiance, depth, sunShadow, environment) = textures;
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.ImageWrite(set, ProbeCapturePass.IrradianceBinding, DescriptorType.CombinedImageSampler, &irradiance),
            Descriptors.ImageWrite(set, ProbeCapturePass.DepthBinding, DescriptorType.CombinedImageSampler, &depth),
            Descriptors.ImageWrite(set, ProbeCapturePass.SunShadowBinding, DescriptorType.CombinedImageSampler, &sunShadow),
            Descriptors.ImageWrite(set, EnvironmentBinding, DescriptorType.CombinedImageSampler, &environment),
        };
        context.Api.UpdateDescriptorSets(context.Device, 4, writes, 0, null);
    }
}
