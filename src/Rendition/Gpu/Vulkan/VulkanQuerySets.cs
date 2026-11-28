using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>
/// The layer's query sets on Vulkan: a pool of timestamp queries each. A query is reset just before it is written, so a
/// list needs no reset of its own, as in WebGPU. Which queries have been written is kept here, since Vulkan can't read a
/// query that was never reset.
/// </summary>
internal sealed unsafe class VulkanQuerySets(VulkanContext context) : IDisposable
{
    private readonly IdTable<QuerySet> _sets = new();
    private readonly (bool Supported, decimal Period) _stamps = Stamps(context);

    private sealed class QuerySet(QueryPool pool, int count)
    {
        public QueryPool Pool { get; } = pool;
        public bool[] Written { get; } = new bool[count];
        public ulong[] Ticks { get; } = new ulong[count];
    }

    private static (bool, decimal) Stamps(VulkanContext context)
    {
        context.Api.GetPhysicalDeviceProperties(context.PhysicalDevice, out var properties);
        return (properties.Limits.TimestampComputeAndGraphics, (decimal)properties.Limits.TimestampPeriod);
    }

    /// <inheritdoc cref="GpuDevice.CreateQuerySet"/>
    public QuerySetId Create(int count)
    {
        if (!_stamps.Supported)
        {
            return default;
        }

        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = (uint)count,
        };
        context.Api.CreateQueryPool(context.Device, &info, null, out var pool).Check();
        return new QuerySetId(_sets.Add(new QuerySet(pool, count)));
    }

    /// <summary>Resets query <paramref name="index"/> and writes the time into it once the work before has run.</summary>
    public void Write(CommandBuffer cmd, QuerySetId id, int index)
    {
        var set = _sets[id.Value];
        context.Api.CmdResetQueryPool(cmd, set.Pool, (uint)index, 1);
        context.Api.CmdWriteTimestamp2(cmd, PipelineStageFlags2.AllCommandsBit, set.Pool, (uint)index);
        set.Written[index] = true;
    }

    /// <inheritdoc cref="GpuDevice.ReadQuerySet"/>
    public bool Read(QuerySetId id, Span<ulong> nanoseconds)
    {
        var set = _sets[id.Value];
        int count = nanoseconds.Length;
        if (set.Written.AsSpan(0, count).Contains(false))
        {
            return false;
        }

        Result result;
        fixed (ulong* ticks = set.Ticks)
        {
            result = context.Api.GetQueryPoolResults(
                context.Device, set.Pool, 0, (uint)count, (nuint)(count * sizeof(ulong)), ticks, sizeof(ulong),
                QueryResultFlags.Result64Bit);
        }

        if (result == Result.NotReady)
        {
            return false;
        }

        result.Check();
        // In decimal, since ticks since the GPU started don't fit a double's 53 bits exactly.
        for (int i = 0; i < count; i++)
        {
            nanoseconds[i] = (ulong)(set.Ticks[i] * _stamps.Period);
        }

        return true;
    }

    public void Destroy(QuerySetId id)
    {
        var set = _sets[id.Value];
        _sets.Remove(id.Value);
        context.Api.DestroyQueryPool(context.Device, set.Pool, null);
    }

    public void Dispose()
    {
        foreach (var set in _sets.Items)
        {
            context.Api.DestroyQueryPool(context.Device, set.Pool, null);
        }
    }
}
