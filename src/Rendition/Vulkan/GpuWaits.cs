using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The times the CPU stops for the GPU: one-off work it records, runs and waits for, and waits for the whole GPU to
/// finish. Counts both, so tests can check a frame never waits.
/// </summary>
internal sealed unsafe class GpuWaits : IDisposable
{
    private readonly Vk _api;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly CommandPool _pool;

    public GpuWaits(Vk api, Device device, Queue queue, uint queueFamily)
    {
        _api = api;
        _device = device;
        _queue = queue;
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit,
            QueueFamilyIndex = queueFamily,
        };
        api.CreateCommandPool(device, &poolInfo, null, out _pool).Check();
    }

    /// <summary>How many times <see cref="SubmitAndWait"/> has run, so tests can check a frame never waits.</summary>
    public int SubmitAndWaitCount { get; private set; }

    /// <summary>How many times <see cref="WaitIdle"/> has run, so a profile can tell a frame that stopped for the GPU.</summary>
    public int WaitIdleCount { get; private set; }

    /// <summary>Waits until the GPU has finished everything it was given.</summary>
    public void WaitIdle()
    {
        WaitIdleCount++;
        _api.DeviceWaitIdle(_device).Check();
    }

    /// <summary>Records and runs one-off GPU work, then waits for it.</summary>
    public void SubmitAndWait(Action<CommandBuffer> record)
    {
        SubmitAndWaitCount++;
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        _api.AllocateCommandBuffers(_device, &allocInfo, out CommandBuffer cmd).Check();

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        _api.BeginCommandBuffer(cmd, &beginInfo).Check();
        record(cmd);
        _api.EndCommandBuffer(cmd).Check();

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
        };
        _api.QueueSubmit(_queue, 1, &submit, default).Check();
        _api.QueueWaitIdle(_queue).Check();
        _api.FreeCommandBuffers(_device, _pool, 1, &cmd);
    }

    public void Dispose() => _api.DestroyCommandPool(_device, _pool, null);
}
