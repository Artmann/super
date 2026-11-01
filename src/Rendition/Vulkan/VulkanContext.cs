using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Rendition.Vulkan;

/// <summary>Owns the Vulkan instance, device and queue that everything else builds on.</summary>
internal sealed unsafe class VulkanContext : IDisposable
{
    private const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    public readonly Vk Api = Vk.GetApi();
    public readonly Instance Instance;
    public readonly PhysicalDevice PhysicalDevice;
    public readonly Device Device;
    public readonly Queue Queue;
    public readonly uint QueueFamily;
    public readonly SurfaceKHR Surface;
    public readonly KhrSurface SurfaceApi;
    public readonly KhrSwapchain SwapchainApi;

    private readonly ExtDebugUtils? _debugUtils;
    private readonly DebugUtilsMessengerEXT _messenger;
    private readonly DebugUtilsMessengerCallbackFunctionEXT? _callback;
    private readonly CommandPool _uploadPool;
    private int _validationErrors;

    public int ValidationErrors => _validationErrors;

    public VulkanContext(RendererOptions options)
    {
        bool validation = options.EnableValidation && IsValidationLayerAvailable();
        if (options.EnableValidation && !validation)
        {
            throw new InvalidOperationException(
                $"{ValidationLayer} was requested but is not installed. Install the Vulkan SDK.");
        }

        Instance = CreateInstance(options, validation);

        if (validation)
        {
            _callback = OnDebugMessage;
            Api.TryGetInstanceExtension(Instance, out _debugUtils);
            var info = new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                    | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                    | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                    | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(_callback),
            };
            _debugUtils!.CreateDebugUtilsMessenger(Instance, &info, null, out _messenger).Check();
        }

        if (!Api.TryGetInstanceExtension(Instance, out SurfaceApi!))
        {
            throw new InvalidOperationException("VK_KHR_surface is not available.");
        }

        Surface = new SurfaceKHR(options.CreateSurface(Instance.Handle));
        (PhysicalDevice, QueueFamily) = PickPhysicalDevice();
        Device = CreateDevice();
        Api.GetDeviceQueue(Device, QueueFamily, 0, out Queue);

        if (!Api.TryGetDeviceExtension(Instance, Device, out SwapchainApi!))
        {
            throw new InvalidOperationException("VK_KHR_swapchain is not available.");
        }

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit,
            QueueFamilyIndex = QueueFamily,
        };
        Api.CreateCommandPool(Device, &poolInfo, null, out _uploadPool).Check();
    }

    public uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
    {
        Api.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memory);
        for (int i = 0; i < memory.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << i)) != 0 && (memory.MemoryTypes[i].PropertyFlags & required) == required)
            {
                return (uint)i;
            }
        }

        throw new InvalidOperationException($"No GPU memory type supports {required}.");
    }

    /// <summary>Records and runs one-off GPU work, then waits for it. Not for use during a frame.</summary>
    public void SubmitAndWait(Action<CommandBuffer> record)
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _uploadPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        Api.AllocateCommandBuffers(Device, &allocInfo, out CommandBuffer cmd).Check();

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Api.BeginCommandBuffer(cmd, &beginInfo).Check();
        record(cmd);
        Api.EndCommandBuffer(cmd).Check();

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
        };
        Api.QueueSubmit(Queue, 1, &submit, default).Check();
        Api.QueueWaitIdle(Queue).Check();
        Api.FreeCommandBuffers(Device, _uploadPool, 1, &cmd);
    }

    public void Dispose()
    {
        Api.DestroyCommandPool(Device, _uploadPool, null);
        Api.DestroyDevice(Device, null);
        SurfaceApi.DestroySurface(Instance, Surface, null);
        _debugUtils?.DestroyDebugUtilsMessenger(Instance, _messenger, null);
        Api.DestroyInstance(Instance, null);
        Api.Dispose();
    }

    private bool IsValidationLayerAvailable()
    {
        uint count = 0;
        Api.EnumerateInstanceLayerProperties(&count, null).Check();
        var layers = new LayerProperties[count];
        fixed (LayerProperties* ptr = layers)
        {
            Api.EnumerateInstanceLayerProperties(&count, ptr).Check();
            for (int i = 0; i < count; i++)
            {
                if (SilkMarshal.PtrToString((nint)ptr[i].LayerName) == ValidationLayer)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private Instance CreateInstance(RendererOptions options, bool validation)
    {
        var extensions = new List<string>(options.InstanceExtensions);
        if (validation)
        {
            extensions.Add(ExtDebugUtils.ExtensionName);
        }

        nint appName = SilkMarshal.StringToPtr("Rendition");
        nint extensionNames = SilkMarshal.StringArrayToPtr(extensions);
        nint layerNames = SilkMarshal.StringArrayToPtr([ValidationLayer]);

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
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = (byte**)extensionNames,
            EnabledLayerCount = validation ? 1u : 0u,
            PpEnabledLayerNames = (byte**)layerNames,
        };

        Api.CreateInstance(&createInfo, null, out Instance instance).Check();

        SilkMarshal.Free(layerNames);
        SilkMarshal.Free(extensionNames);
        SilkMarshal.Free(appName);
        return instance;
    }

    private (PhysicalDevice, uint) PickPhysicalDevice()
    {
        uint count = 0;
        Api.EnumeratePhysicalDevices(Instance, &count, null).Check();
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* ptr = devices)
        {
            Api.EnumeratePhysicalDevices(Instance, &count, ptr).Check();
        }

        PhysicalDevice? best = null;
        uint bestFamily = 0;
        foreach (var device in devices)
        {
            Api.GetPhysicalDeviceProperties(device, out var properties);
            if (properties.ApiVersion < Vk.Version13 || FindQueueFamily(device) is not { } family)
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

    private uint? FindQueueFamily(PhysicalDevice device)
    {
        uint count = 0;
        Api.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* ptr = families)
        {
            Api.GetPhysicalDeviceQueueFamilyProperties(device, &count, ptr);
        }

        for (uint i = 0; i < count; i++)
        {
            SurfaceApi.GetPhysicalDeviceSurfaceSupport(device, i, Surface, out var canPresent).Check();
            if (canPresent && (families[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
            {
                return i;
            }
        }

        return null;
    }

    private Device CreateDevice()
    {
        float priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        var features = new PhysicalDeviceFeatures
        {
            SamplerAnisotropy = true,
            ImageCubeArray = true,
            ShaderSampledImageArrayDynamicIndexing = true,
        };
        var features12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            RuntimeDescriptorArray = true,
            DescriptorBindingPartiallyBound = true,
            ShaderSampledImageArrayNonUniformIndexing = true,
        };
        var features13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            PNext = &features12,
            DynamicRendering = true,
            Synchronization2 = true,
            ShaderDemoteToHelperInvocation = true,
        };
        nint extensionNames = SilkMarshal.StringArrayToPtr([KhrSwapchain.ExtensionName]);
        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &features13,
            PEnabledFeatures = &features,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            EnabledExtensionCount = 1,
            PpEnabledExtensionNames = (byte**)extensionNames,
        };

        Api.CreateDevice(PhysicalDevice, &createInfo, null, out Device device).Check();
        SilkMarshal.Free(extensionNames);
        return device;
    }

    private uint OnDebugMessage(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        Interlocked.Increment(ref _validationErrors);
        Console.Error.WriteLine($"[vulkan] {severity}: {Marshal.PtrToStringUTF8((nint)data->PMessage)}");
        return Vk.False;
    }
}
