using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Rendition.Vulkan;

/// <summary>What the device was made with beyond what every GPU the renderer runs on has.</summary>
/// <param name="StorageImageExtendedFormats">
/// Whether compute shaders can write two-channel images such as rg16f, which the light probes' depth atlas prefers.
/// </param>
/// <param name="Multiview">Whether one draw can render to six layers at once, which the light probes' cube captures need.</param>
/// <param name="PipelineStatistics">Whether the GPU can count the shaders each pass runs, which <see cref="GpuTimer"/> does where it can.</param>
/// <param name="ConservativeRasterization">
/// Whether the device was made with VK_EXT_conservative_rasterization, so a pipeline can count a pixel as covered
/// when any part of a triangle touches it. The baker's texel setup uses it; see "Texel setup" in docs/design.md.
/// </param>
/// <param name="NoTraceReason">Why the device can't trace rays, such as "no VK_KHR_ray_query", or null when it can.</param>
internal sealed record DeviceAbilities(
    bool StorageImageExtendedFormats, bool Multiview, bool PipelineStatistics, bool ConservativeRasterization, string? NoTraceReason)
{
    /// <summary>Whether the device was made with ray queries on: the three extensions and the features they need.</summary>
    public bool CanTrace => NoTraceReason is null;
}

/// <summary>
/// Makes the logical device on a GPU: the features every frame needs, the ones FSR and the light probes use where the
/// GPU has them, and ray queries where it has all they need.
/// </summary>
internal sealed unsafe class VulkanDevice(Vk api, PhysicalDevice physical)
{
    private const string MemoryRequirements2Extension = "VK_KHR_get_memory_requirements2";
    private const string ConservativeRasterizationExtension = "VK_EXT_conservative_rasterization";

    private readonly HashSet<string> _available = ExtensionNames(api, physical);

    // What the GPU supports, copied out of the query's chain.
    private struct Supported
    {
        public PhysicalDeviceFeatures Core;
        public PhysicalDeviceVulkan11Features Vulkan11;
        public PhysicalDeviceVulkan12Features Vulkan12;
        public PhysicalDeviceVulkan13Features Vulkan13;
        public bool AccelerationStructure;
        public bool RayQuery;
        public uint MaxMultiviewViews;
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

    /// <summary>
    /// The same for Vulkan 1.2's features. The texture table is far bigger than a GPU must allow in one ordinary set,
    /// and MoltenVK allows only 16 samplers there, so it's made update-after-bind, which has limits of its own.
    /// </summary>
    internal static void RequireFeatures(in PhysicalDeviceVulkan12Features supported)
    {
        if (!supported.DescriptorBindingSampledImageUpdateAfterBind)
        {
            throw new InvalidOperationException(
                "This GPU can't hold the renderer's table of every texture (Vulkan's descriptorBindingSampledImageUpdateAfterBind).");
        }
    }

    /// <summary>Makes the device with one queue from <paramref name="queueFamily"/>, and says what it was made with.</summary>
    public Device Create(uint queueFamily, bool disableRayTracing, out DeviceAbilities abilities)
    {
        var supported = Query();
        abilities = new DeviceAbilities(
            StorageImageExtendedFormats: supported.Core.ShaderStorageImageExtendedFormats,
            Multiview: supported.Vulkan11.Multiview && supported.MaxMultiviewViews >= 6,
            PipelineStatistics: supported.Core.PipelineStatisticsQuery,
            ConservativeRasterization: _available.Contains(ConservativeRasterizationExtension),
            // Ray queries let a shader ask what a ray hits. Without all of them the device is made exactly as before,
            // and nothing that asks CanTrace traces.
            NoTraceReason: RayTracingSupport.Missing(
                _available, supported.Vulkan12.BufferDeviceAddress, supported.AccelerationStructure,
                supported.RayQuery, disableRayTracing));

        var features = Core(supported.Core, abilities);
        var features11 = Vulkan11(supported.Vulkan11, abilities);
        var features12 = Vulkan12(supported.Vulkan12, abilities);
        var features13 = Vulkan13(supported.Vulkan13);
        features12.PNext = &features11;
        features13.PNext = &features12;
        var rayQuery = new PhysicalDeviceRayQueryFeaturesKHR { SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr, RayQuery = true };
        var accelerationStructure = new PhysicalDeviceAccelerationStructureFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            PNext = &rayQuery,
            AccelerationStructure = true,
        };
        if (abilities.CanTrace)
        {
            features11.PNext = &accelerationStructure;
        }

        var extensions = Extensions(abilities);
        var portability = new PhysicalDevicePortabilitySubsetFeaturesKHR { SType = StructureType.PhysicalDevicePortabilitySubsetFeaturesKhr, PNext = &features13 };
        bool portable = Portability.TurnOn(api, physical, _available, extensions, ref portability);
        return Create(queueFamily, extensions, portable ? &portability : &features13, &features);
    }

    // FSR picks its shaders by what the GPU supports, not by what the device turned on: 16-bit maths where
    // the GPU has it, and a fixed subgroup size where it can choose one. So those are turned on wherever
    // they're supported, or FSR's pipelines would use features the device doesn't have.
    private Supported Query()
    {
        api.GetPhysicalDeviceFeatures(physical, out var core);
        RequireFeatures(core);
        var rayQuery = new PhysicalDeviceRayQueryFeaturesKHR { SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr };
        var accelerationStructure = new PhysicalDeviceAccelerationStructureFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            PNext = &rayQuery,
        };
        var supported13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };

        // The ray tracing feature structs belong to their extensions, so they're only asked about where the GPU
        // lists them. Elsewhere the query is the one it was before tracing, and the features stay false.
        if (RayTracingSupport.CanAskForFeatures(_available))
        {
            supported13.PNext = &accelerationStructure;
        }

        var supported12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &supported13 };
        var supported11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features, PNext = &supported12 };
        var supportedAll = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &supported11 };
        api.GetPhysicalDeviceFeatures2(physical, &supportedAll);
        RequireFeatures(supported12);

        var multiviewProperties = new PhysicalDeviceMultiviewProperties { SType = StructureType.PhysicalDeviceMultiviewProperties };
        var properties2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &multiviewProperties };
        api.GetPhysicalDeviceProperties2(physical, &properties2);
        return new Supported
        {
            Core = core,
            Vulkan11 = supported11,
            Vulkan12 = supported12,
            Vulkan13 = supported13,
            AccelerationStructure = accelerationStructure.AccelerationStructure,
            RayQuery = rayQuery.RayQuery,
            MaxMultiviewViews = multiviewProperties.MaxMultiviewViewCount,
        };
    }

    private static PhysicalDeviceFeatures Core(in PhysicalDeviceFeatures supported, DeviceAbilities abilities) => new()
    {
        SamplerAnisotropy = true,
        ImageCubeArray = true,
        // The decal pass blends its colour target and writes its normal target whole.
        IndependentBlend = true,
        ShaderSampledImageArrayDynamicIndexing = true,
        TextureCompressionBC = true,
        ShaderStorageImageExtendedFormats = abilities.StorageImageExtendedFormats,
        ShaderInt16 = supported.ShaderInt16,
        PipelineStatisticsQuery = abilities.PipelineStatistics,
    };

    private static PhysicalDeviceVulkan11Features Vulkan11(in PhysicalDeviceVulkan11Features supported, DeviceAbilities abilities) => new()
    {
        SType = StructureType.PhysicalDeviceVulkan11Features,
        StorageBuffer16BitAccess = supported.StorageBuffer16BitAccess,
        UniformAndStorageBuffer16BitAccess = supported.UniformAndStorageBuffer16BitAccess,
        Multiview = abilities.Multiview,
    };

    private static PhysicalDeviceVulkan12Features Vulkan12(in PhysicalDeviceVulkan12Features supported, DeviceAbilities abilities) => new()
    {
        SType = StructureType.PhysicalDeviceVulkan12Features,
        RuntimeDescriptorArray = true,
        DescriptorBindingPartiallyBound = true,
        ShaderSampledImageArrayNonUniformIndexing = true,
        DescriptorBindingUpdateUnusedWhilePending = true,
        DescriptorBindingSampledImageUpdateAfterBind = true,
        ShaderFloat16 = supported.ShaderFloat16,
        ShaderStorageBufferArrayNonUniformIndexing = supported.ShaderStorageBufferArrayNonUniformIndexing,
        BufferDeviceAddress = abilities.CanTrace,
    };

    private static PhysicalDeviceVulkan13Features Vulkan13(in PhysicalDeviceVulkan13Features supported) => new()
    {
        SType = StructureType.PhysicalDeviceVulkan13Features,
        DynamicRendering = true,
        Synchronization2 = true,
        ShaderDemoteToHelperInvocation = true,
        SubgroupSizeControl = supported.SubgroupSizeControl,
        ComputeFullSubgroups = supported.ComputeFullSubgroups,
    };

    private List<string> Extensions(DeviceAbilities abilities)
    {
        // FSR looks up vkGetBufferMemoryRequirements2KHR by its extension name, which the driver only hands out
        // when the extension is on, even though Vulkan 1.1 has the function under its core name.
        var extensions = new List<string> { KhrSwapchain.ExtensionName };
        if (_available.Contains(MemoryRequirements2Extension))
        {
            extensions.Add(MemoryRequirements2Extension);
        }

        // It has no features to turn on, only a pipeline state, so nothing that doesn't ask for it changes.
        if (abilities.ConservativeRasterization)
        {
            extensions.Add(ConservativeRasterizationExtension);
        }

        if (abilities.CanTrace)
        {
            extensions.AddRange(RayTracingSupport.Extensions);
        }

        return extensions;
    }

    private Device Create(uint queueFamily, List<string> extensions, void* next, PhysicalDeviceFeatures* features)
    {
        float priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = queueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        nint extensionNames = SilkMarshal.StringArrayToPtr(extensions);
        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = next,
            PEnabledFeatures = features,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = (byte**)extensionNames,
        };

        api.CreateDevice(physical, &createInfo, null, out Device device).Check();
        SilkMarshal.Free(extensionNames);
        return device;
    }

    private static HashSet<string> ExtensionNames(Vk api, PhysicalDevice physical)
    {
        uint count = 0;
        api.EnumerateDeviceExtensionProperties(physical, (byte*)null, &count, null).Check();
        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* first = properties)
        {
            api.EnumerateDeviceExtensionProperties(physical, (byte*)null, &count, first).Check();
        }

        var names = new HashSet<string>();
        foreach (var property in properties)
        {
            names.Add(SilkMarshal.PtrToString((nint)property.ExtensionName)!);
        }

        return names;
    }
}
