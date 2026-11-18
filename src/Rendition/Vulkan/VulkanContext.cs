using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Rendition.Vulkan;

/// <summary>Owns the Vulkan instance, device and queue that everything else builds on.</summary>
internal sealed unsafe class VulkanContext : IDisposable
{
    public readonly Vk Api = VulkanLibrary.CreateApi();
    public readonly Instance Instance;
    public readonly PhysicalDevice PhysicalDevice;
    public readonly Device Device;
    public readonly Queue Queue;
    public readonly uint QueueFamily;
    public readonly SurfaceKHR Surface;
    public readonly KhrSurface SurfaceApi;
    public readonly KhrSwapchain SwapchainApi;

    /// <summary>The validation layer's messages while it is on, so libraries such as FSR can check their own work too; null when it's off.</summary>
    public readonly VulkanValidation? Validation;

    /// <summary>The one-off work the CPU waits for, and how often it has waited.</summary>
    public readonly GpuWaits Waits;

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
        bool validation = options.EnableValidation && VulkanValidation.IsLayerAvailable(Api);
        if (options.EnableValidation && !validation)
        {
            throw new InvalidOperationException(
                VulkanLibrary.NoLayersReason is { } reason
                    ? $"{VulkanValidation.Layer} was requested, but {reason}"
                    : $"{VulkanValidation.Layer} was requested but is not installed. Install the Vulkan SDK.");
        }

        Instance = VulkanStartup.CreateInstance(Api, options, validation);
        Validation = validation ? new VulkanValidation(Api, Instance) : null;

        if (!Api.TryGetInstanceExtension(Instance, out SurfaceApi!))
        {
            throw new InvalidOperationException("VK_KHR_surface is not available.");
        }

        Surface = new SurfaceKHR(options.CreateSurface(Instance.Handle));
        (PhysicalDevice, QueueFamily) = VulkanStartup.PickPhysicalDevice(Api, Instance, SurfaceApi, Surface);
        Device = new VulkanDevice(Api, PhysicalDevice).Create(QueueFamily, options.DisableRayTracing, out var abilities);
        Abilities = abilities;
        Api.GetDeviceQueue(Device, QueueFamily, 0, out Queue);

        if (!Api.TryGetDeviceExtension(Instance, Device, out SwapchainApi!))
        {
            throw new InvalidOperationException("VK_KHR_swapchain is not available.");
        }

        Waits = new GpuWaits(Api, Device, Queue, QueueFamily);
        Memory = new GpuMemory(Api, PhysicalDevice, Device, CanTrace);
    }

    /// <summary>What the device was made with beyond what every GPU the renderer runs on has.</summary>
    public DeviceAbilities Abilities { get; }

    /// <summary>Whether the device was made with ray queries on: the three extensions and the features they need.</summary>
    public bool CanTrace => Abilities.CanTrace;

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

    /// <summary>Waits until the GPU has finished everything it was given. Not for use during a frame.</summary>
    public void WaitIdle() => Waits.WaitIdle();

    /// <summary>Records and runs one-off GPU work, then waits for it. Not for use during a frame.</summary>
    public void SubmitAndWait(Action<CommandBuffer> record) => Waits.SubmitAndWait(record);

    public void Dispose()
    {
        Memory.Dispose();
        Waits.Dispose();
        Api.DestroyDevice(Device, null);
        SurfaceApi.DestroySurface(Instance, Surface, null);
        Validation?.Dispose();
        Api.DestroyInstance(Instance, null);
        Api.Dispose();
    }
}
