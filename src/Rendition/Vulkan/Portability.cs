using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// What a portability driver such as MoltenVK, on macOS, needs before the renderer can use it. Elsewhere neither
/// extension is listed, and the instance and device are made as they would be without this.
/// </summary>
internal static unsafe class Portability
{
    private const string EnumerationExtension = "VK_KHR_portability_enumeration";
    private const string SubsetExtension = "VK_KHR_portability_subset";

    /// <summary>
    /// A portability driver isn't fully conformant, so the loader only lists it to an instance that asks for portability
    /// drivers. Adds the extension to <paramref name="extensions"/> where the loader has it, and returns the flags the
    /// instance needs with it.
    /// </summary>
    public static InstanceCreateFlags AddInstanceExtension(Vk api, List<string> extensions)
    {
        uint count = 0;
        api.EnumerateInstanceExtensionProperties((byte*)null, &count, null).Check();
        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* first = properties)
        {
            api.EnumerateInstanceExtensionProperties((byte*)null, &count, first).Check();
            for (int i = 0; i < count; i++)
            {
                if (SilkMarshal.PtrToString((nint)first[i].ExtensionName) == EnumerationExtension)
                {
                    extensions.Add(EnumerationExtension);
                    return InstanceCreateFlags.EnumeratePortabilityBitKhr;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// The spec says a device must turn the subset extension on where it's listed. Then this adds it to
    /// <paramref name="extensions"/>, fills <paramref name="features"/> with what the driver can do of what Vulkan
    /// otherwise always allows, such as comparison samplers in a descriptor set, so all of it is turned on, and returns
    /// true. The features' own next pointer is kept, so the caller chains them in front of its other features.
    /// </summary>
    public static bool TurnOn(
        Vk api, PhysicalDevice device, HashSet<string> listed, List<string> extensions,
        ref PhysicalDevicePortabilitySubsetFeaturesKHR features)
    {
        if (!listed.Contains(SubsetExtension))
        {
            return false;
        }

        extensions.Add(SubsetExtension);
        void* next = features.PNext;
        features.PNext = null;
        fixed (PhysicalDevicePortabilitySubsetFeaturesKHR* pointer = &features)
        {
            var query = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = pointer };
            api.GetPhysicalDeviceFeatures2(device, &query);
        }

        features.PNext = next;
        return true;
    }
}
