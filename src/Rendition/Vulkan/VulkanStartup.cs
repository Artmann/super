using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Rendition.Vulkan;

/// <summary>The first steps of starting Vulkan: the instance, then the GPU and the queue family to draw and present with.</summary>
internal static unsafe class VulkanStartup
{
    public static Instance CreateInstance(Vk api, RendererOptions options, bool validation)
    {
        var extensions = new List<string>(WindowSurfaces.Extensions(options.Surface));
        if (validation)
        {
            extensions.Add(ExtDebugUtils.ExtensionName);
        }

        var flags = Portability.AddInstanceExtension(api, extensions);
        nint appName = SilkMarshal.StringToPtr("Rendition");
        nint extensionNames = SilkMarshal.StringArrayToPtr(extensions);
        nint layerNames = SilkMarshal.StringArrayToPtr([VulkanValidation.Layer]);

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)appName,
            PEngineName = (byte*)appName,
            ApiVersion = Vk.Version13,
        };
        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            Flags = flags,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = (byte**)extensionNames,
            EnabledLayerCount = validation ? 1u : 0u,
            PpEnabledLayerNames = (byte**)layerNames,
        };

        api.CreateInstance(&createInfo, null, out Instance instance).Check();

        SilkMarshal.Free(layerNames);
        SilkMarshal.Free(extensionNames);
        SilkMarshal.Free(appName);
        return instance;
    }

    /// <summary>
    /// A GPU with Vulkan 1.3 and a queue family that can draw and present to <paramref name="surface"/>, preferring a
    /// discrete one, and that family.
    /// </summary>
    public static (PhysicalDevice, uint) PickPhysicalDevice(Vk api, Instance instance, KhrSurface surfaceApi, SurfaceKHR surface)
    {
        uint count = 0;
        api.EnumeratePhysicalDevices(instance, &count, null).Check();
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* ptr = devices)
        {
            api.EnumeratePhysicalDevices(instance, &count, ptr).Check();
        }

        PhysicalDevice? best = null;
        uint bestFamily = 0;
        foreach (var device in devices)
        {
            api.GetPhysicalDeviceProperties(device, out var properties);
            if (properties.ApiVersion < Vk.Version13 || FindQueueFamily(api, device, surfaceApi, surface) is not { } family)
            {
                continue;
            }

            if (best is null || properties.DeviceType == PhysicalDeviceType.DiscreteGpu)
            {
                best = device;
                bestFamily = family;
            }
        }

        return best is { } found
            ? (found, bestFamily)
            : throw new InvalidOperationException("No GPU supports Vulkan 1.3 with presentation.");
    }

    private static uint? FindQueueFamily(Vk api, PhysicalDevice device, KhrSurface surfaceApi, SurfaceKHR surface)
    {
        uint count = 0;
        api.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* ptr = families)
        {
            api.GetPhysicalDeviceQueueFamilyProperties(device, &count, ptr);
        }

        for (uint i = 0; i < count; i++)
        {
            surfaceApi.GetPhysicalDeviceSurfaceSupport(device, i, surface, out var canPresent).Check();
            if (canPresent && (families[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
            {
                return i;
            }
        }

        return null;
    }
}
