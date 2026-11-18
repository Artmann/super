using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Times each render pass on the GPU. The GPU writes a timestamp after each pass, and the
/// results are read once that frame has finished, a frame or two later. Marks must come
/// in the order of the timer's pass names. Given a <see cref="GpuWorkCounter"/>, it also has
/// the shaders each pass runs counted at the same marks.
/// </summary>
internal sealed unsafe class GpuTimer : IDisposable
{
    private const double NanosecondsPerMillisecond = 1_000_000.0;

    /// <summary>The passes of a frame, in the order the renderer marks them.</summary>
    internal static readonly string[] FramePasses =
    [
        "Shadows", "Light probes", "Reflection probes", "Light clusters", "Volumetric fog", "G-buffer", "Ambient occlusion", "AO blur", "Reflections", "Lighting", "Subsurface", "Glass and particles",
        "Anti-aliasing", "Depth of field", "Bloom", "Tonemap", "Finish", "Overlay",
    ];

    private readonly string[] _passNames;
    private readonly VulkanContext _context;
    private readonly QueryPool[] _pools;
    private readonly bool[] _written;
    private readonly ulong[] _stamps;
    private readonly GpuPassTime[] _times;
    private readonly double _nanosecondsPerStamp;
    private readonly GpuWorkCounter? _counter;
    private uint _nextMark;
    // The frame slot being timed, or -1 outside a timed frame.
    private int _frame = -1;
    private bool _collected;

    /// <summary>
    /// Times the frame's passes, or <paramref name="passNames"/> of its own, such as the slice of a background bake.
    /// A <paramref name="counter"/> made for the same passes counts their shaders too, and is disposed with the timer.
    /// </summary>
    public GpuTimer(VulkanContext context, int framesInFlight, string[]? passNames = null, GpuWorkCounter? counter = null)
    {
        passNames ??= FramePasses;
        _passNames = passNames;
        _counter = counter;
        _stamps = new ulong[passNames.Length + 1];
        _times = new GpuPassTime[passNames.Length];
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
        _written[frame] = true;
        _context.Api.CmdResetQueryPool(cmd, _pools[frame], 0, (uint)_stamps.Length);
        _counter?.Begin(cmd, frame);
        _nextMark = 0;
        Mark(cmd);
    }

    /// <summary>Marks the end of a pass. Does nothing outside a timed frame, as when capturing a probe.</summary>
    public void Mark(CommandBuffer cmd)
    {
        if (_frame >= 0)
        {
            _counter?.Mark(cmd, _nextMark);
            _context.Api.CmdWriteTimestamp2(cmd, PipelineStageFlags2.AllCommandsBit, _pools[_frame], _nextMark++);
        }
    }

    /// <summary>Ends the timed frame, and the count of a pass left open.</summary>
    public void End(CommandBuffer cmd)
    {
        if (_frame >= 0)
        {
            _counter?.End(cmd, _nextMark);
        }

        _frame = -1;
    }

    /// <summary>
    /// Reads a frame's results, once. Its fence must have been waited on. False when nothing was timed in that slot
    /// since it was last read.
    /// </summary>
    public bool Collect(int frame)
    {
        if (!_written[frame])
        {
            return false;
        }

        _written[frame] = false;

        fixed (ulong* stamps = _stamps)
        {
            _context.Api.GetQueryPoolResults(
                _context.Device, _pools[frame], 0, (uint)_stamps.Length, (nuint)(_stamps.Length * sizeof(ulong)),
                stamps, sizeof(ulong), QueryResultFlags.Result64Bit).Check();
        }

        for (int i = 0; i < _times.Length; i++)
        {
            double stamps = _stamps[i + 1] - _stamps[i];
            _times[i] = new GpuPassTime(_passNames[i], stamps * _nanosecondsPerStamp / NanosecondsPerMillisecond);
        }

        _collected = true;
        _counter?.Collect(frame);
        return true;
    }

    public void Dispose()
    {
        foreach (var pool in _pools)
        {
            _context.Api.DestroyQueryPool(_context.Device, pool, null);
        }

        _counter?.Dispose();
    }
}
