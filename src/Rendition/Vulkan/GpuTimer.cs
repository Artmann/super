using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Times each render pass on the GPU. The GPU writes a timestamp after each pass, and the
/// results are read once that frame has finished, a frame or two later. Marks must come
/// in the order of <see cref="PassNames"/>.
/// </summary>
internal sealed unsafe class GpuTimer : IDisposable
{
    private const double NanosecondsPerMillisecond = 1_000_000.0;

    private static readonly string[] PassNames =
    [
        "Shadows", "G-buffer", "Ambient occlusion", "AO blur", "Lighting", "Glass",
        "Anti-aliasing", "Bloom", "Tonemap", "Finish", "Overlay",
    ];

    private readonly VulkanContext _context;
    private readonly QueryPool[] _pools;
    private readonly bool[] _written;
    private readonly ulong[] _stamps = new ulong[PassNames.Length + 1];
    private readonly GpuPassTime[] _times = new GpuPassTime[PassNames.Length];
    private readonly double _nanosecondsPerStamp;
    private uint _nextMark;
    private int _frame;
    private bool _active;
    private bool _collected;

    public GpuTimer(VulkanContext context, int framesInFlight)
    {
        _context = context;
        _pools = new QueryPool[framesInFlight];
        _written = new bool[framesInFlight];
        context.Api.GetPhysicalDeviceProperties(context.PhysicalDevice, out var properties);
        _nanosecondsPerStamp = properties.Limits.TimestampPeriod;
        IsSupported = properties.Limits.TimestampComputeAndGraphics;

        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = (uint)_stamps.Length,
        };
        for (int i = 0; i < framesInFlight; i++)
        {
            context.Api.CreateQueryPool(context.Device, &info, null, out _pools[i]).Check();
        }
    }

    public bool IsSupported { get; }

    /// <summary>The newest finished frame's passes. Empty until one has finished.</summary>
    public ReadOnlySpan<GpuPassTime> Times => _collected ? _times : default;

    public void Begin(CommandBuffer cmd, int frame)
    {
        _frame = frame;
        _active = true;
        _written[frame] = true;
        _context.Api.CmdResetQueryPool(cmd, _pools[frame], 0, (uint)_stamps.Length);
        _nextMark = 0;
        Mark(cmd);
    }

    /// <summary>Marks the end of a pass. Does nothing outside a timed frame, as when capturing a probe.</summary>
    public void Mark(CommandBuffer cmd)
    {
        if (_active)
        {
            _context.Api.CmdWriteTimestamp2(cmd, PipelineStageFlags2.AllCommandsBit, _pools[_frame], _nextMark++);
        }
    }

    public void End() => _active = false;

    /// <summary>Reads a frame's results. Its fence must have been waited on.</summary>
    public void Collect(int frame)
    {
        if (!_written[frame])
        {
            return;
        }

        fixed (ulong* stamps = _stamps)
        {
            _context.Api.GetQueryPoolResults(
                _context.Device, _pools[frame], 0, (uint)_stamps.Length, (nuint)(_stamps.Length * sizeof(ulong)),
                stamps, sizeof(ulong), QueryResultFlags.Result64Bit).Check();
        }

        for (int i = 0; i < _times.Length; i++)
        {
            double stamps = _stamps[i + 1] - _stamps[i];
            _times[i] = new GpuPassTime(PassNames[i], stamps * _nanosecondsPerStamp / NanosecondsPerMillisecond);
        }

        _collected = true;
    }

    public void Dispose()
    {
        foreach (var pool in _pools)
        {
            _context.Api.DestroyQueryPool(_context.Device, pool, null);
        }
    }
}
