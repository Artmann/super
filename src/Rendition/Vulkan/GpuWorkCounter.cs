using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Counts the shaders each render pass runs on the GPU, with one pipeline statistics query a pass. A
/// <see cref="GpuTimer"/> begins and ends the queries at its marks, so the passes are the timer's. The counts are
/// exact: a pass whose work is skipped counts none, while one that still runs counts every shader, even those that
/// leave at once. Needs the device's <c>pipelineStatisticsQuery</c>; see <see cref="DeviceAbilities.PipelineStatistics"/>.
/// </summary>
internal sealed unsafe class GpuWorkCounter : IDisposable
{
    // The counters each pass's query keeps. Vulkan writes them in the order of their bits.
    private const QueryPipelineStatisticFlags Counted = QueryPipelineStatisticFlags.VertexShaderInvocationsBit
        | QueryPipelineStatisticFlags.FragmentShaderInvocationsBit | QueryPipelineStatisticFlags.ComputeShaderInvocationsBit;

    private const int CountersPerPass = 3;

    private readonly VulkanContext _context;
    private readonly string[] _passNames;
    private readonly QueryPool[] _pools;
    private readonly bool[] _written;
    private readonly ulong[] _counters;
    private readonly GpuPassWork[] _work;
    private int _frame;
    private bool _open;
    // The pass whose count is running, while one is open.
    private uint _running;
    private bool _collected;

    public GpuWorkCounter(VulkanContext context, int framesInFlight, string[] passNames)
    {
        _context = context;
        _passNames = passNames;
        _pools = new QueryPool[framesInFlight];
        _written = new bool[framesInFlight];
        _counters = new ulong[passNames.Length * CountersPerPass];
        _work = new GpuPassWork[passNames.Length];

        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.PipelineStatistics,
            QueryCount = (uint)passNames.Length,
            PipelineStatistics = Counted,
        };
        for (int i = 0; i < framesInFlight; i++)
        {
            context.Api.CreateQueryPool(context.Device, &info, null, out _pools[i]).Check();
        }
    }

    /// <summary>The shaders the newest finished frame's passes ran. Empty until one has finished.</summary>
    public ReadOnlySpan<GpuPassWork> Work => _collected ? _work : default;

    public void Begin(CommandBuffer cmd, int frame)
    {
        _frame = frame;
        _open = false;
        _context.Api.CmdResetQueryPool(cmd, _pools[frame], 0, (uint)_work.Length);
    }

    /// <summary>
    /// The timer's mark number <paramref name="mark"/>: it ends the count of the pass before it, if any, and begins the
    /// next one's. Mark 0 begins the frame, so pass i lies between marks i and i + 1. Marks go between render
    /// passes, as a query can't begin or end inside one.
    /// </summary>
    public void Mark(CommandBuffer cmd, uint mark)
    {
        if (mark > _work.Length)
        {
            return;
        }

        EndRunning(cmd);
        _open = mark < _work.Length;
        if (_open)
        {
            _context.Api.CmdBeginQuery(cmd, _pools[_frame], mark, 0);
            _running = mark;
        }
    }

    /// <summary>
    /// Ends the frame after <paramref name="marks"/> marks. Its counts are kept only if every pass was marked, as a
    /// frame cut short has passes that never ran.
    /// </summary>
    public void End(CommandBuffer cmd, uint marks)
    {
        EndRunning(cmd);
        _written[_frame] = marks == _work.Length + 1;
    }

    // Ends the count running, if any.
    private void EndRunning(CommandBuffer cmd)
    {
        if (!_open)
        {
            return;
        }

        _context.Api.CmdEndQuery(cmd, _pools[_frame], _running);

        _open = false;
    }

    /// <summary>Reads a frame's counts, once. Its fence must have been waited on.</summary>
    public void Collect(int frame)
    {
        if (!_written[frame])
        {
            return;
        }

        _written[frame] = false;
        fixed (ulong* counters = _counters)
        {
            _context.Api.GetQueryPoolResults(
                _context.Device, _pools[frame], 0, (uint)_work.Length, (nuint)(_counters.Length * sizeof(ulong)),
                counters, CountersPerPass * sizeof(ulong), QueryResultFlags.Result64Bit).Check();
        }

        for (int i = 0; i < _work.Length; i++)
        {
            int at = i * CountersPerPass;
            _work[i] = new GpuPassWork(_passNames[i], _counters[at], _counters[at + 1], _counters[at + 2]);
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
