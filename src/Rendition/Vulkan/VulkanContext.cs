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
    private int _fsrLumaHistoryNoted;

    public int ValidationErrors => _validationErrors;

    /// <summary>Whether the validation layer is on, so libraries such as FSR can check their own work too.</summary>
    public bool Validating => _debugUtils is not null;

    /// <summary>Whether compute shaders can write two-channel images such as rg16f, which the light probes' depth atlas prefers.</summary>
    public bool StorageImageExtendedFormats { get; private set; }

    /// <summary>Whether one draw can render to six layers at once, which the light probes' cube captures need.</summary>
    public bool Multiview { get; private set; }

    /// <summary>Whether the device was made with ray queries on: the three extensions and the features they need.</summary>
    public bool CanTrace { get; private set; }

    /// <summary>Why <see cref="CanTrace"/> is false, such as "no VK_KHR_ray_query", or null when it is true.</summary>
    public string? NoTraceReason { get; private set; }

    /// <summary>
    /// Whether the device was made with VK_EXT_conservative_rasterization, so a pipeline can count a pixel as covered
    /// when any part of a triangle touches it. The baker's texel setup uses it; see "Texel setup" in docs/design.md.
    /// </summary>
    public bool ConservativeRasterization { get; private set; }

    /// <summary>Owns every byte of device memory. Set once the device exists.</summary>
    public readonly GpuMemory Memory;

    /// <summary>
    /// Every pipeline there is, with <see cref="RendererOptions.LiveShaders"/>, so a changed shader can find the
    /// ones to build again. Null otherwise, and then pipelines keep nothing.
    /// </summary>
    public readonly HashSet<LivePipeline>? LivePipelines;

    public VulkanContext(RendererOptions options)
    {
        LivePipelines = options.LiveShaders ? [] : null;
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
        Device = CreateDevice(options.DisableRayTracing);
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
        Memory = new GpuMemory(this);
    }

    /// <summary>The GPU's name, kind and memory, as its driver reports them.</summary>
    public GpuInfo Gpu
    {
        get
        {
            Api.GetPhysicalDeviceProperties(PhysicalDevice, out var properties);
            Api.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memory);
            long largest = 0;
            for (int i = 0; i < memory.MemoryHeapCount; i++)
            {
                if ((memory.MemoryHeaps[i].Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                {
                    largest = Math.Max(largest, (long)memory.MemoryHeaps[i].Size);
                }
            }

            var kind = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => GpuKind.Discrete,
                PhysicalDeviceType.IntegratedGpu => GpuKind.Integrated,
                PhysicalDeviceType.VirtualGpu or PhysicalDeviceType.Cpu => GpuKind.Virtual,
                _ => GpuKind.Unknown,
            };
            string name = Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "";
            return new GpuInfo(name, kind, largest, CanTrace);
        }
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

    /// <summary>How many times <see cref="SubmitAndWait"/> has run, so tests can check a frame never waits.</summary>
    internal int SubmitAndWaitCount { get; private set; }

    /// <summary>Records and runs one-off GPU work, then waits for it. Not for use during a frame.</summary>
    public void SubmitAndWait(Action<CommandBuffer> record)
    {
        SubmitAndWaitCount++;
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
        Memory.Dispose();
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

    /// <summary>
    /// Throws when the GPU lacks a device feature the renderer can't draw without, naming it, rather than
    /// letting device creation fail with a bare error code.
    /// </summary>
    internal static void RequireFeatures(in PhysicalDeviceFeatures supported)
    {
        if (!supported.IndependentBlend)
        {
            throw new InvalidOperationException(
                "This GPU can't blend its render targets in different ways at once (Vulkan's independentBlend), which decals need.");
        }
    }

    private Device CreateDevice(bool disableRayTracing)
    {
        float priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        Api.GetPhysicalDeviceFeatures(PhysicalDevice, out var supported);
        RequireFeatures(supported);
        StorageImageExtendedFormats = supported.ShaderStorageImageExtendedFormats;

        // FSR picks its shaders by what the GPU supports, not by what the device turned on: 16-bit maths where
        // the GPU has it, and a fixed subgroup size where it can choose one. So those are turned on wherever
        // they're supported, or FSR's pipelines would use features the device doesn't have.
        var deviceExtensions = DeviceExtensionNames();
        var supportedRayQuery = new PhysicalDeviceRayQueryFeaturesKHR { SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr };
        var supportedAccelerationStructure = new PhysicalDeviceAccelerationStructureFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            PNext = &supportedRayQuery,
        };
        var supported13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };

        // The ray tracing feature structs belong to their extensions, so they're only asked about where the GPU
        // lists them. Elsewhere the query is the one it was before tracing, and the features stay false.
        if (RayTracingSupport.CanAskForFeatures(deviceExtensions))
        {
            supported13.PNext = &supportedAccelerationStructure;
        }

        var supported12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &supported13 };
        var supported11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features, PNext = &supported12 };
        var supportedAll = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &supported11 };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &supportedAll);

        var multiviewProperties = new PhysicalDeviceMultiviewProperties { SType = StructureType.PhysicalDeviceMultiviewProperties };
        var properties2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &multiviewProperties };
        Api.GetPhysicalDeviceProperties2(PhysicalDevice, &properties2);
        Multiview = supported11.Multiview && multiviewProperties.MaxMultiviewViewCount >= 6;

        var features = new PhysicalDeviceFeatures
        {
            SamplerAnisotropy = true,
            ImageCubeArray = true,
            // The decal pass blends its colour target and writes its normal target whole.
            IndependentBlend = true,
            ShaderSampledImageArrayDynamicIndexing = true,
            TextureCompressionBC = true,
            ShaderStorageImageExtendedFormats = StorageImageExtendedFormats,
            ShaderInt16 = supported.ShaderInt16,
        };
        var features11 = new PhysicalDeviceVulkan11Features
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            StorageBuffer16BitAccess = supported11.StorageBuffer16BitAccess,
            UniformAndStorageBuffer16BitAccess = supported11.UniformAndStorageBuffer16BitAccess,
            Multiview = Multiview,
        };
        var features12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = &features11,
            RuntimeDescriptorArray = true,
            DescriptorBindingPartiallyBound = true,
            ShaderSampledImageArrayNonUniformIndexing = true,
            DescriptorBindingUpdateUnusedWhilePending = true,
            ShaderFloat16 = supported12.ShaderFloat16,
            ShaderStorageBufferArrayNonUniformIndexing = supported12.ShaderStorageBufferArrayNonUniformIndexing,
        };
        var features13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            PNext = &features12,
            DynamicRendering = true,
            Synchronization2 = true,
            ShaderDemoteToHelperInvocation = true,
            SubgroupSizeControl = supported13.SubgroupSizeControl,
            ComputeFullSubgroups = supported13.ComputeFullSubgroups,
        };

        // FSR looks up vkGetBufferMemoryRequirements2KHR by its extension name, which the driver only hands out
        // when the extension is on, even though Vulkan 1.1 has the function under its core name.
        var extensions = new List<string> { KhrSwapchain.ExtensionName };
        if (deviceExtensions.Contains(MemoryRequirements2Extension))
        {
            extensions.Add(MemoryRequirements2Extension);
        }

        // It has no features to turn on, only a pipeline state, so nothing that doesn't ask for it changes.
        ConservativeRasterization = deviceExtensions.Contains(ConservativeRasterizationExtension);
        if (ConservativeRasterization)
        {
            extensions.Add(ConservativeRasterizationExtension);
        }

        // Ray queries let a shader ask what a ray hits. Without all of them the device is made exactly as before,
        // and nothing that asks CanTrace traces.
        NoTraceReason = RayTracingSupport.Missing(
            deviceExtensions, supported12.BufferDeviceAddress, supportedAccelerationStructure.AccelerationStructure,
            supportedRayQuery.RayQuery, disableRayTracing);
        CanTrace = NoTraceReason is null;
        var rayQuery = new PhysicalDeviceRayQueryFeaturesKHR { SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr, RayQuery = true };
        var accelerationStructure = new PhysicalDeviceAccelerationStructureFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            PNext = &rayQuery,
            AccelerationStructure = true,
        };
        if (CanTrace)
        {
            extensions.AddRange(RayTracingSupport.Extensions);
            features12.BufferDeviceAddress = true;
            features11.PNext = &accelerationStructure;
        }

        nint extensionNames = SilkMarshal.StringArrayToPtr(extensions);
        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &features13,
            PEnabledFeatures = &features,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = (byte**)extensionNames,
        };

        Api.CreateDevice(PhysicalDevice, &createInfo, null, out Device device).Check();
        SilkMarshal.Free(extensionNames);
        return device;
    }

    private const string MemoryRequirements2Extension = "VK_KHR_get_memory_requirements2";
    private const string ConservativeRasterizationExtension = "VK_EXT_conservative_rasterization";

    private HashSet<string> DeviceExtensionNames()
    {
        uint count = 0;
        Api.EnumerateDeviceExtensionProperties(PhysicalDevice, (byte*)null, &count, null).Check();
        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* first = properties)
        {
            Api.EnumerateDeviceExtensionProperties(PhysicalDevice, (byte*)null, &count, first).Check();
        }

        var names = new HashSet<string>();
        foreach (var property in properties)
        {
            names.Add(SilkMarshal.PtrToString((nint)property.ExtensionName)!);
        }

        return names;
    }

    private uint OnDebugMessage(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        string message = Marshal.PtrToStringUTF8((nint)data->PMessage) ?? "";
        if (IsFsrLumaHistoryFormat(message))
        {
            if (Interlocked.Exchange(ref _fsrLumaHistoryNoted, 1) == 0)
            {
                Console.Error.WriteLine(
                    "[vulkan] note: FSR's own shader declares its luma history Rgba8 while FSR makes that image RGBA16F. "
                    + "It's inside amd_fidelityfx_vk.dll, so it isn't counted.");
            }

            return Vk.False;
        }

        Interlocked.Increment(ref _validationErrors);
        Console.Error.WriteLine($"[vulkan] {severity}: {message}");
        return Vk.False;
    }

    /// <summary>
    /// The one message the prebuilt FidelityFX 1.1.4 shaders raise on every FSR frame: their storage image for
    /// luma history says Rgba8, but the SDK creates that image, which nothing of ours touches, as RGBA16F.
    /// That is a real bug in AMD's shader, undefined behaviour inside FSR, which we can't fix without rebuilding
    /// it; see "Upscaling" in docs/design.md for why we accept it. Matched on the variable and both formats, so any
    /// other format message still counts.
    /// </summary>
    internal static bool IsFsrLumaHistoryFormat(string message) =>
        message.Contains("variable \"rw_luma_history\"", StringComparison.Ordinal)
        && message.Contains("Format operand Rgba8", StringComparison.Ordinal)
        && message.Contains("VK_FORMAT_R16G16B16A16_SFLOAT", StringComparison.Ordinal);
}
