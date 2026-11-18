using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// A top-level structure sized for up to <see cref="Capacity"/> instances, the host-visible buffer its instances are
/// written into, and the host-visible geometry table those instances index.
/// </summary>
internal sealed unsafe class TopLevelTarget : IDisposable
{
    private readonly StructureBuilder _builder;
    private readonly GpuBuffer _buffer;
    private int _tableCapacity;

    public readonly int Capacity;
    public readonly ulong ScratchSize;
    public readonly GpuBuffer Instances;
    public readonly AccelerationStructureKHR Handle;
    public readonly ulong Address;

    /// <summary>Hands this target back to the pool. Made the first time the target goes out, then reused.</summary>
    public IDisposable? Recycler;

    public TopLevelTarget(StructureBuilder builder, int capacity, ulong size, ulong scratchSize)
    {
        _builder = builder;
        var context = builder.Context;
        Capacity = capacity;
        ScratchSize = scratchSize;
        Instances = GpuBuffer.CreateHostVisible(
            context, (ulong)(capacity * sizeof(AccelerationStructureInstanceKHR)), GpuBuffer.TraceInput(context));
        if (Instances.Address % 16 != 0)
        {
            throw new InvalidOperationException($"Instance data must be 16-byte aligned, but sits at {Instances.Address:X}.");
        }

        _buffer = new GpuBuffer(context, size,
            BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.DeviceLocalBit);
        (Handle, Address) = builder.CreateStructure(_buffer, size, AccelerationStructureTypeKHR.TopLevelKhr);
    }

    /// <summary>The geometry table, made by the first <see cref="WriteTable"/> call and grown by later ones.</summary>
    public GpuBuffer? GeometryTable { get; private set; }

    /// <summary>Writes the parts into the geometry table, finding each one's mesh with <paramref name="meshes"/>.</summary>
    public void WriteTable(List<TraceGeometry> parts, Func<MeshHandle, Mesh> meshes)
    {
        var table = Table(parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            var (meshHandle, part, tint) = parts[i];
            var mesh = meshes(meshHandle);
            table[i] = new GpuTraceGeometry
            {
                Vertices = mesh.Vertices.Address,
                Indices = mesh.Indices.Address,
                FirstIndex = part.FirstIndex,
                Material = (uint)part.Material.Index,
                TintRG = TraceEncoding.Half2(tint.X, tint.Y),
                TintB = TraceEncoding.Half2(tint.Z, 0f),
            };
        }
    }

    /// <summary>Records the build of the structure over the first <paramref name="count"/> instances in its instance buffer.</summary>
    public void Record(CommandBuffer cmd, int count)
    {
        var geometry = TraceEncoding.InstancesGeometry(Instances.Address);
        var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = (uint)count };
        var rangePointer = &range;
        var info = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1,
            PGeometries = &geometry,
            DstAccelerationStructure = Handle,
            ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = _builder.Scratch(ScratchSize) },
        };
        _builder.Api.CmdBuildAccelerationStructures(cmd, 1, &info, &rangePointer);
        _builder.WaitForBuild(cmd);
    }

    public void Dispose()
    {
        _builder.Api.DestroyAccelerationStructure(_builder.Context.Device, Handle, null);
        _buffer.Dispose();
        Instances.Dispose();
        GeometryTable?.Dispose();
    }

    // Room for `count` entries in the geometry table. A target is only handed out once nothing reads it, so a table
    // that's too small can go at once.
    private Span<GpuTraceGeometry> Table(int count)
    {
        if (GeometryTable is null || _tableCapacity < count)
        {
            GeometryTable?.Dispose();
            _tableCapacity = (int)Math.Max(16u, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)count));
            GeometryTable = GpuBuffer.CreateHostVisible(
                _builder.Context, (ulong)(_tableCapacity * sizeof(GpuTraceGeometry)), BufferUsageFlags.StorageBufferBit);
        }

        return new Span<GpuTraceGeometry>(GeometryTable.Mapped, count);
    }
}

/// <summary>
/// Top-level structures and their instance buffers that no recorded work reads any more, ready to be built into
/// again. One goes out per build and comes back once the work recorded with it is done, so a frame still in flight
/// never sees its structure or instances overwritten, however many builds a frame makes.
/// </summary>
internal sealed unsafe class TopLevelPool(StructureBuilder builder, Action<IDisposable> releaseAfterRecorded) : IDisposable
{
    /// <summary>Hands a top-level target back to the pool once the work recorded with it is done.</summary>
    private sealed class Recycle(TopLevelPool owner, TopLevelTarget target) : IDisposable
    {
        public void Dispose() => owner.Return(target);
    }

    private readonly List<TopLevelTarget> _free = [];
    private bool _disposed;

    /// <summary>How many top-level structures have been made to build into, rather than reused. For tests.</summary>
    public int StructuresMade { get; private set; }

    /// <summary>A free target with room for this many instances, made when none is big enough.</summary>
    public TopLevelTarget Rent(int count)
    {
        for (int i = 0; i < _free.Count; i++)
        {
            if (_free[i].Capacity >= count)
            {
                var found = _free[i];
                _free.RemoveAt(i);
                return found;
            }
        }

        // Every free one is too small and nothing reads them any more, so they go now.
        foreach (var small in _free)
        {
            small.Dispose();
        }

        _free.Clear();
        int capacity = (int)Math.Max(16u, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)count));
        var geometry = TraceEncoding.InstancesGeometry(0);
        var info = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1,
            PGeometries = &geometry,
        };
        uint maxInstances = (uint)capacity;
        var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
        builder.Api.GetAccelerationStructureBuildSizes(
            builder.Context.Device, AccelerationStructureBuildTypeKHR.DeviceKhr, &info, &maxInstances, &sizes);
        StructuresMade++;
        return new TopLevelTarget(builder, capacity, sizes.AccelerationStructureSize, sizes.BuildScratchSize);
    }

    /// <summary>
    /// Lets a target recorded into go back to the pool: once the work recorded so far is done, or, when it is
    /// <paramref name="held"/>, once the release this returns is disposed.
    /// </summary>
    public IDisposable? HandOut(TopLevelTarget target, bool held)
    {
        target.Recycler ??= new Recycle(this, target);
        if (held)
        {
            return target.Recycler;
        }

        releaseAfterRecorded(target.Recycler);
        return null;
    }

    /// <summary>Takes back a target nothing reads any more, or frees it once the pool is gone.</summary>
    public void Return(TopLevelTarget target)
    {
        if (_disposed)
        {
            target.Dispose();
            return;
        }

        _free.Add(target);
    }

    public void Dispose()
    {
        foreach (var target in _free)
        {
            target.Dispose();
        }

        _free.Clear();
        _disposed = true;
    }
}
