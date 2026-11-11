using Silk.NET.Vulkan.Extensions.KHR;

namespace Rendition.Vulkan;

/// <summary>
/// Decides whether the device can trace rays. Ray queries let any shader ask what a ray hits, so they need
/// three extensions and three features, and a GPU without all of them draws without tracing instead.
/// </summary>
internal static class RayTracingSupport
{
    // Silk.NET 2.23 has no KhrRayQuery class, because the extension adds no functions, so its name is spelled out.
    private const string RayQueryExtension = "VK_KHR_ray_query";

    /// <summary>The device extensions ray queries need.</summary>
    public static readonly string[] Extensions =
    [
        KhrAccelerationStructure.ExtensionName,
        RayQueryExtension,
        KhrDeferredHostOperations.ExtensionName,
    ];

    /// <summary>
    /// Whether the GPU's feature query may carry the acceleration structure and ray query feature structs. They
    /// belong to their extensions, so a GPU that doesn't list both is asked exactly what it was asked before.
    /// </summary>
    public static bool CanAskForFeatures(IReadOnlySet<string> deviceExtensions)
    {
        return deviceExtensions.Contains(KhrAccelerationStructure.ExtensionName) && deviceExtensions.Contains(RayQueryExtension);
    }

    /// <summary>
    /// Null when rays can be traced, otherwise a short reason such as "no VK_KHR_ray_query", for the errors of
    /// things that need tracing and for anyone wondering why it is off.
    /// </summary>
    public static string? Missing(
        IReadOnlySet<string> deviceExtensions, bool bufferDeviceAddress, bool accelerationStructure, bool rayQuery, bool disabled)
    {
        if (disabled)
        {
            return "turned off with --no-trace";
        }

        foreach (string extension in Extensions)
        {
            if (!deviceExtensions.Contains(extension))
            {
                return $"no {extension}";
            }
        }

        if (!bufferDeviceAddress)
        {
            return "no bufferDeviceAddress";
        }

        if (!accelerationStructure)
        {
            return "no accelerationStructure";
        }

        return rayQuery ? null : "no rayQuery";
    }
}
