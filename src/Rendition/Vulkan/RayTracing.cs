using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Rendition.Vulkan;

/// <summary>
/// A built top-level structure: its handle, the address a shader traces it by, and the geometry table its instances
/// index, <see cref="GeometryCount"/> entries, one per part. Valid only in the command buffer it was recorded into;
/// see <see cref="RayTracing.TopLevel"/>.
/// </summary>
internal readonly unsafe record struct TopLevelStructure(
    AccelerationStructureKHR Handle, ulong Address, GpuBuffer GeometryTable, int GeometryCount)
{
    /// <summary>The geometry table as the GPU will read it. For tests, before the work it was built for is done.</summary>
    public ReadOnlySpan<GpuTraceGeometry> ReadGeometries() =>
        new(GeometryTable.Mapped, GeometryCount);
}

/// <summary>
/// One entry of the geometry table, for one part: where its mesh's vertices and indices are, where its triangles start
/// among the indices, its material, and its draw's tint. A hit's entry is its instance's custom index plus its geometry
/// index. Must match TraceGeometry in Shaders/trace.glsl, laid out as std430: 32 bytes, with the addresses read as
/// uvec2 and the tint as three half floats, red and green in one word and blue in the low half of the next.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuTraceGeometry
{
    public ulong Vertices;
    public ulong Indices;
    public uint FirstIndex;
    public uint Material;
    public uint TintRG;
    public uint TintB;
}

/// <summary>
/// The acceleration structures rays are traced through. A mesh's bottom-level structure is built the first time
/// something traces it, into whatever command buffer asks, and kept until the mesh is destroyed. Exists only where
/// the device can trace. See "Ray tracing" in docs/design.md.
/// </summary>
internal sealed unsafe class RayTracing : IDisposable
{
    /// <summary>
    /// The stages that trace rays and read hit data: mesh bytes through buffer addresses and textures through the
    /// resource table. Uploads and <see cref="ReadyToTrace"/> make their writes visible to these stages.
    /// </summary>
    public const PipelineStageFlags2 TraceStages = PipelineStageFlags2.ComputeShaderBit;

    private readonly VulkanContext _context;
    private readonly KhrAccelerationStructure _api;
    private readonly ResourceTable _resources;
    private readonly Func<MeshHandle, Mesh> _meshes;
    private readonly Func<GpuBuffer, bool> _uploadPending;
    private readonly Action<IDisposable> _releaseAfterSubmitted;
    private readonly Action<IDisposable> _releaseAfterRecorded;
    private readonly ulong _scratchAlignment;

    // Every built structure, by mesh. A mesh rarely has more than one, so a list searched by part list is enough.
    private readonly Dictionary<MeshHandle, List<BottomLevelStructure>> _bottomLevels = [];
    private int _bottomLevelCount;

    // Shared by every build. Each build waits for the one before it, so one buffer serves them all; it grows when a
    // build needs more.
    private GpuBuffer? _scratch;

    // Top-level structures and their instance buffers that no recorded work reads any more, ready to be built into
    // again. One goes out per TopLevel call and comes back once the work recorded with it is done, so a frame still in
    // flight never sees its structure or instances overwritten, however many builds a frame makes.
    private readonly List<TopLevelTarget> _freeTopLevels = [];

    // The parts of the build being made, before they're copied into its geometry table. Reused by every build.
    private readonly List<TraceGeometry> _packed = [];

    // The bottom-level structure of each instance of the build being made. Reused by every build.
    private ulong[] _bottomLevelAddresses = [];
    private int _topLevelBuilds;
    private int _topLevelStructuresMade;
    private int _bottomLevelsReleased;
    private bool _disposed;

    /// <summary>A see-through instance has this bit of its mask clear, so a ray can skip it with a cull mask.</summary>
    public const byte SeeThroughMaskBit = 0x02;

    /// <summary>The largest custom index an instance can have, and so the last geometry it can start at: 24 bits.</summary>
    private const uint MaxCustomIndex = (1u << 24) - 1;

    /// <param name="meshes">The live mesh behind a handle.</param>
    /// <param name="uploadPending">Whether a buffer's bytes are still waiting in the upload queue.</param>
    /// <param name="releaseAfterSubmitted">
    /// Frees something once every frame submitted so far is done with it, for a destroyed mesh's structures.
    /// </param>
    /// <param name="releaseAfterRecorded">
    /// Frees something once the work recorded so far, including the command buffer being recorded now, is done with
    /// it, for a scratch buffer that has been outgrown and for a top-level structure going back to the pool. That
    /// assumes the command buffer is the frame being recorded or is waited on before the next frame.
    /// </param>
    public RayTracing(
        VulkanContext context,
        ResourceTable resources,
        Func<MeshHandle, Mesh> meshes,
        Func<GpuBuffer, bool> uploadPending,
        Action<IDisposable> releaseAfterSubmitted,
        Action<IDisposable> releaseAfterRecorded)
    {
        if (!context.CanTrace)
        {
            throw new InvalidOperationException("Ray tracing needs a device that can trace.");
        }

        _context = context;
        _resources = resources;
        _meshes = meshes;
        _uploadPending = uploadPending;
        _releaseAfterSubmitted = releaseAfterSubmitted;
        _releaseAfterRecorded = releaseAfterRecorded;
        if (!context.Api.TryGetDeviceExtension(context.Instance, context.Device, out _api))
        {
            throw new InvalidOperationException($"{KhrAccelerationStructure.ExtensionName} is not available.");
        }

        var structureProperties = new PhysicalDeviceAccelerationStructurePropertiesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr,
        };
        var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &structureProperties };
        context.Api.GetPhysicalDeviceProperties2(context.PhysicalDevice, &properties);
        _scratchAlignment = Math.Max(1u, structureProperties.MinAccelerationStructureScratchOffsetAlignment);
    }

    /// <summary>How many bottom-level structures are live. For tests.</summary>
    public int BottomLevelCount => _bottomLevelCount;

    /// <summary>How many bottom-level structures have really been freed, not just forgotten. For tests.</summary>
    public int BottomLevelsReleased => _bottomLevelsReleased;

    /// <summary>How many top-level structures have been built. For tests.</summary>
    public int TopLevelBuilds => _topLevelBuilds;

    /// <summary>How many top-level structures have been made to build into, rather than reused. For tests.</summary>
    public int TopLevelStructuresMade => _topLevelStructuresMade;

    /// <summary>
    /// Records a build of the top-level structure over these instances into <paramref name="cmd"/>, building any
    /// bottom-level structure they need first, followed by a barrier for later builds. Rebuilt whole every call, for
    /// fast tracing; refitting is left until something needs it.
    /// <para>
    /// The structure, its instances and its geometry table belong to the work recorded so far and are reused once that
    /// is done, so read them only in <paramref name="cmd"/>. That assumes <paramref name="cmd"/> is the frame being
    /// recorded, after its uploads (which <see cref="BottomLevel"/> insists on), or is waited on before the next frame.
    /// </para>
    /// <para>
    /// Destroying or changing the opacity of a material frees its bottom-level structures into the slot of the frame
    /// last submitted (<see cref="ForgetMaterial"/>), which doesn't cover the frame being recorded. So nothing may
    /// change a material between this call and the frame's submit. Nothing in <c>Renderer.Render</c> does today.
    /// </para>
    /// </summary>
    public TopLevelStructure TopLevel(CommandBuffer cmd, IReadOnlyList<TraceInstance> instances) =>
        Build(cmd, instances, false, out _);

    /// <summary>
    /// Records a build of the top-level structure as <see cref="TopLevel(CommandBuffer, IReadOnlyList{TraceInstance})"/>
    /// does, but the structure isn't reused when the frame is: it is held until <paramref name="release"/> is disposed,
    /// so work in later command buffers, such as a bake's steps, can read it. Dispose the release only once the GPU is
    /// done with every command buffer that reads the structure. The scratch buffer is still shared, so the build itself
    /// belongs to <paramref name="cmd"/> as any other build does.
    /// </summary>
    public TopLevelStructure TopLevel(CommandBuffer cmd, IReadOnlyList<TraceInstance> instances, out IDisposable release)
    {
        var structure = Build(cmd, instances, true, out var held);
        release = held!;
        return structure;
    }

    private TopLevelStructure Build(CommandBuffer cmd, IReadOnlyList<TraceInstance> instances, bool hold, out IDisposable? release)
    {
        if (_bottomLevelAddresses.Length < instances.Count)
        {
            Array.Resize(ref _bottomLevelAddresses, Math.Max(instances.Count, _bottomLevelAddresses.Length * 2));
        }

        var bottomLevels = _bottomLevelAddresses.AsSpan(0, instances.Count);
        for (int i = 0; i < instances.Count; i++)
        {
            bottomLevels[i] = BottomLevel(cmd, instances[i].Mesh, instances[i].Parts.Span);
        }

        var target = Rent(instances.Count);
        var packed = new Span<AccelerationStructureInstanceKHR>(target.Instances.Mapped, instances.Count);
        _packed.Clear();
        try
        {
            Pack(instances, bottomLevels, packed, _packed);
        }
        catch
        {
            // Nothing has been recorded with it, so it can go straight back.
            Return(target);
            throw;
        }

        // Copied into the target's own table now, so nothing holds on to the list once this returns.
        var table = target.Table(_packed.Count);
        for (int i = 0; i < _packed.Count; i++)
        {
            var (meshHandle, part, tint) = _packed[i];
            var mesh = _meshes(meshHandle);
            table[i] = new GpuTraceGeometry
            {
                Vertices = mesh.Vertices.Address,
                Indices = mesh.Indices.Address,
                FirstIndex = part.FirstIndex,
                Material = (uint)part.Material.Index,
                TintRG = Half2(tint.X, tint.Y),
                TintB = Half2(tint.Z, 0f),
            };
        }

        var geometry = InstancesGeometry(target.Instances.Address);
        var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = (uint)instances.Count };
        var rangePointer = &range;
        var info = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1,
            PGeometries = &geometry,
            DstAccelerationStructure = target.Handle,
            ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = Scratch(target.ScratchSize) },
        };
        _api.CmdBuildAccelerationStructures(cmd, 1, &info, &rangePointer);
        WaitForBuild(cmd);

        target.Recycler ??= new Recycle(this, target);
        if (hold)
        {
            release = target.Recycler;
        }
        else
        {
            _releaseAfterRecorded(target.Recycler);
            release = null;
        }

        _topLevelBuilds++;
        return new TopLevelStructure(target.Handle, target.Address, target.GeometryTable!, _packed.Count);
    }

    /// <summary>
    /// Makes compute shaders recorded after this wait for every structure built before it, so they can trace them.
    /// <see cref="TopLevel"/>'s own barrier only covers later builds.
    /// </summary>
    public void ReadyToTrace(CommandBuffer cmd)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            SrcAccessMask = AccessFlags2.AccelerationStructureWriteBitKhr,
            DstStageMask = TraceStages,
            DstAccessMask = AccessFlags2.AccelerationStructureReadBitKhr,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }

    /// <summary>
    /// Fills in one Vulkan instance per trace instance, and appends their parts to the geometry table they index. Each
    /// instance's custom index is where its first part sits in the table, so a hit's entry is its custom index plus its
    /// geometry index.
    /// </summary>
    public static void Pack(
        IReadOnlyList<TraceInstance> instances,
        ReadOnlySpan<ulong> bottomLevels,
        Span<AccelerationStructureInstanceKHR> into,
        List<TraceGeometry> geometries)
    {
        uint first = (uint)geometries.Count;
        for (int i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            if (first + (ulong)instance.Parts.Length > MaxCustomIndex + 1ul)
            {
                throw new InvalidOperationException(
                    $"The scene has more than {MaxCustomIndex + 1} traced parts, which is more than an instance's custom index can count.");
            }

            into[i] = new AccelerationStructureInstanceKHR
            {
                Transform = TraceScene.ToVulkan(instance.Transform),
                InstanceCustomIndex = first,
                Mask = (instance.Flags & TraceFlags.SeeThrough) != 0 ? 0xFFu & ~(uint)SeeThroughMaskBit : 0xFFu,
                InstanceShaderBindingTableRecordOffset = 0,
                AccelerationStructureReference = bottomLevels[i],
            };
            var parts = instance.Parts.Span;
            for (int p = 0; p < parts.Length; p++)
            {
                geometries.Add(new TraceGeometry(instance.Mesh, parts[p], instance.TintOf(p)));
            }

            first += (uint)instance.Parts.Length;
        }
    }

    /// <summary>Two floats as half floats in one word, as GLSL's unpackHalf2x16 reads them: the first in the low half.</summary>
    internal static uint Half2(float low, float high) =>
        BitConverter.HalfToUInt16Bits((Half)low) | ((uint)BitConverter.HalfToUInt16Bits((Half)high) << 16);

    /// <summary>How rays treat a material: whether it has holes to check, and whether it's see-through.</summary>
    public static TraceFlags TraceFlagsFor(bool cutouts, MaterialFlags flags) =>
        (cutouts ? TraceFlags.Cutout : TraceFlags.None)
        | ((flags & MaterialFlags.Transparent) != 0 ? TraceFlags.SeeThrough : TraceFlags.None);

    private static AccelerationStructureGeometryKHR InstancesGeometry(ulong instances) => new()
    {
        SType = StructureType.AccelerationStructureGeometryKhr,
        GeometryType = GeometryTypeKHR.InstancesKhr,
        Geometry = new AccelerationStructureGeometryDataKHR
        {
            Instances = new AccelerationStructureGeometryInstancesDataKHR
            {
                SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
                ArrayOfPointers = false,
                Data = new DeviceOrHostAddressConstKHR { DeviceAddress = instances },
            },
        },
    };

    /// <summary>A free top-level target with room for this many instances, made when none is big enough.</summary>
    private TopLevelTarget Rent(int count)
    {
        for (int i = 0; i < _freeTopLevels.Count; i++)
        {
            if (_freeTopLevels[i].Capacity >= count)
            {
                var found = _freeTopLevels[i];
                _freeTopLevels.RemoveAt(i);
                return found;
            }
        }

        // Every free one is too small and nothing reads them any more, so they go now.
        foreach (var small in _freeTopLevels)
        {
            small.Dispose();
        }

        _freeTopLevels.Clear();
        int capacity = (int)Math.Max(16u, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)count));
        var geometry = InstancesGeometry(0);
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
        _api.GetAccelerationStructureBuildSizes(_context.Device, AccelerationStructureBuildTypeKHR.DeviceKhr, &info, &maxInstances, &sizes);
        _topLevelStructuresMade++;
        return new TopLevelTarget(_context, _api, capacity, sizes.AccelerationStructureSize, sizes.BuildScratchSize);
    }

    private void Return(TopLevelTarget target)
    {
        if (_disposed)
        {
            target.Dispose();
            return;
        }

        _freeTopLevels.Add(target);
    }

    /// <summary>
    /// The device address of the bottom-level structure for these parts of the mesh. The first call for a mesh and
    /// part list records its build into <paramref name="cmd"/>, followed by a barrier, so later work in the same
    /// command buffer can read it; later calls return the same structure and record nothing. The mesh's bytes must
    /// already be uploaded by work that runs before <paramref name="cmd"/>, or earlier in it; a build from a mesh whose
    /// copy is still queued throws rather than caching a structure made from empty memory.
    /// </summary>
    public ulong BottomLevel(CommandBuffer cmd, MeshHandle mesh, ReadOnlySpan<TracePart> parts)
    {
        if (_bottomLevels.TryGetValue(mesh, out var built))
        {
            foreach (var structure in built)
            {
                if (parts.SequenceEqual(structure.Parts))
                {
                    return structure.Address;
                }
            }
        }

        var live = _meshes(mesh);
        if (_uploadPending(live.Vertices) || _uploadPending(live.Indices))
        {
            throw new InvalidOperationException(
                $"Mesh {mesh.Index}'s bytes haven't been uploaded yet; record the frame's uploads before building its structure.");
        }

        var created = Build(cmd, live, parts);
        if (built is null)
        {
            built = [];
            _bottomLevels[mesh] = built;
        }

        built.Add(created);
        _bottomLevelCount++;
        return created.Address;
    }

    /// <summary>
    /// Lets go of every structure built for the mesh. They're freed once the frames already submitted are done, as the
    /// mesh's own buffers are. Call it when the mesh is destroyed, before its handle can be handed out again.
    /// </summary>
    public void Forget(MeshHandle mesh)
    {
        if (!_bottomLevels.Remove(mesh, out var built))
        {
            return;
        }

        foreach (var structure in built)
        {
            _releaseAfterSubmitted(structure);
        }

        _bottomLevelCount -= built.Count;
    }

    /// <summary>
    /// Lets go of every structure with a part drawn in this material, because each part's opaque flag is baked in when
    /// it's built. Call it when the material stops or starts being opaque to rays, and when it's destroyed, since its
    /// handle may be handed to another material. Freed the same way as <see cref="Forget"/>.
    /// </summary>
    public void ForgetMaterial(MaterialHandle material)
    {
        List<MeshHandle>? emptied = null;
        foreach (var (mesh, built) in _bottomLevels)
        {
            for (int i = built.Count - 1; i >= 0; i--)
            {
                if (!Uses(built[i].Parts, material))
                {
                    continue;
                }

                _releaseAfterSubmitted(built[i]);
                built.RemoveAt(i);
                _bottomLevelCount--;
            }

            if (built.Count == 0)
            {
                (emptied ??= []).Add(mesh);
            }
        }

        if (emptied is not null)
        {
            foreach (var mesh in emptied)
            {
                _bottomLevels.Remove(mesh);
            }
        }
    }

    private static bool Uses(TracePart[] parts, MaterialHandle material)
    {
        foreach (var part in parts)
        {
            if (part.Material == material)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Opaque lets rays skip the any-hit check. A surface with cut-outs or a see-through one needs that check, so it
    /// gets no flags.
    /// </summary>
    public static GeometryFlagsKHR GeometryFlagsFor(bool cutouts, MaterialFlags flags) =>
        cutouts || (flags & MaterialFlags.Transparent) != 0 ? GeometryFlagsKHR.None : GeometryFlagsKHR.OpaqueBitKhr;

    private BottomLevelStructure Build(CommandBuffer cmd, Mesh mesh, ReadOnlySpan<TracePart> parts)
    {
        if (parts.IsEmpty)
        {
            throw new ArgumentException("A structure needs at least one part.", nameof(parts));
        }

        uint vertexCount = (uint)(mesh.Vertices.Size / (ulong)sizeof(Vertex));
        var triangles = new AccelerationStructureGeometryTrianglesDataKHR
        {
            SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
            VertexFormat = Format.R32G32B32Sfloat,
            VertexData = new DeviceOrHostAddressConstKHR
            {
                DeviceAddress = mesh.Vertices.Address + (ulong)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position)),
            },
            VertexStride = (ulong)sizeof(Vertex),
            MaxVertex = vertexCount - 1,
            IndexType = IndexType.Uint32,
            IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = mesh.Indices.Address },
        };

        var geometries = new AccelerationStructureGeometryKHR[parts.Length];
        var ranges = new AccelerationStructureBuildRangeInfoKHR[parts.Length];
        var triangleCounts = new uint[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.IndexCount == 0 || part.IndexCount % 3 != 0 || (ulong)part.FirstIndex + part.IndexCount > mesh.IndexCount)
            {
                throw new ArgumentException(
                    $"Part {i} covers indices {part.FirstIndex} to {part.FirstIndex + part.IndexCount}, which is not whole "
                    + $"triangles within the mesh's {mesh.IndexCount} indices.", nameof(parts));
            }

            geometries[i] = new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.TrianglesKhr,
                Geometry = new AccelerationStructureGeometryDataKHR { Triangles = triangles },
                Flags = GeometryFlagsFor(_resources.HasCutouts(part.Material), _resources.GetFlags(part.Material)),
            };
            triangleCounts[i] = part.IndexCount / 3;
            ranges[i] = new AccelerationStructureBuildRangeInfoKHR
            {
                PrimitiveCount = triangleCounts[i],
                PrimitiveOffset = part.FirstIndex * sizeof(uint),
            };
        }

        fixed (AccelerationStructureGeometryKHR* geometry = geometries)
        fixed (AccelerationStructureBuildRangeInfoKHR* range = ranges)
        fixed (uint* triangleCount = triangleCounts)
        {
            var info = new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = AccelerationStructureTypeKHR.BottomLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
                Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                GeometryCount = (uint)parts.Length,
                PGeometries = geometry,
            };
            var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
            _api.GetAccelerationStructureBuildSizes(
                _context.Device, AccelerationStructureBuildTypeKHR.DeviceKhr, &info, triangleCount, &sizes);

            var structure = new BottomLevelStructure(this, parts.ToArray(), sizes.AccelerationStructureSize);
            info.DstAccelerationStructure = structure.Handle;
            info.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = Scratch(sizes.BuildScratchSize) };
            _api.CmdBuildAccelerationStructures(cmd, 1, &info, &range);
            WaitForBuild(cmd);
            return structure;
        }
    }

    /// <summary>A scratch address with room for <paramref name="size"/> bytes, aligned as the device asks.</summary>
    private ulong Scratch(ulong size)
    {
        // The buffer's own address only promises the memory's alignment, so it's made big enough to round up.
        ulong needed = size + _scratchAlignment;
        if (_scratch is null || _scratch.Size < needed)
        {
            if (_scratch is not null)
            {
                _releaseAfterRecorded(_scratch);
            }

            _scratch = new GpuBuffer(_context, needed,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, MemoryPropertyFlags.DeviceLocalBit);
        }

        return AlignUp(_scratch.Address, _scratchAlignment);
    }

    /// <summary>The smallest multiple of <paramref name="alignment"/> that is at least <paramref name="value"/>.</summary>
    public static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

    /// <summary>
    /// Makes later work wait for the build: anything that reads the structure, and the next build, which reuses the
    /// scratch buffer.
    /// </summary>
    private void WaitForBuild(CommandBuffer cmd)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            SrcAccessMask = AccessFlags2.AccelerationStructureWriteBitKhr,
            DstStageMask = PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            DstAccessMask = AccessFlags2.AccelerationStructureReadBitKhr | AccessFlags2.AccelerationStructureWriteBitKhr,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }

    /// <summary>Frees every structure and the scratch buffer at once. Only once the GPU is idle.</summary>
    public void Dispose()
    {
        foreach (var built in _bottomLevels.Values)
        {
            foreach (var structure in built)
            {
                structure.Dispose();
            }
        }

        _bottomLevels.Clear();
        _bottomLevelCount = 0;
        foreach (var target in _freeTopLevels)
        {
            target.Dispose();
        }

        _freeTopLevels.Clear();
        _disposed = true;
        _scratch?.Dispose();
        _scratch = null;
    }

    /// <summary>Hands a top-level target back to the pool once the work recorded with it is done.</summary>
    private sealed class Recycle(RayTracing owner, TopLevelTarget target) : IDisposable
    {
        public void Dispose() => owner.Return(target);
    }

    /// <summary>
    /// A top-level structure sized for up to <see cref="Capacity"/> instances, the host-visible buffer its instances are
    /// written into, and the host-visible geometry table those instances index.
    /// </summary>
    private sealed class TopLevelTarget : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly KhrAccelerationStructure _api;
        private readonly GpuBuffer _buffer;

        public readonly int Capacity;
        public readonly ulong ScratchSize;
        public readonly GpuBuffer Instances;
        public readonly AccelerationStructureKHR Handle;

        /// <summary>Hands this target back to the pool. Made the first time the target goes out, then reused.</summary>
        public IDisposable? Recycler;

        /// <summary>The geometry table, made by the first <see cref="Table"/> call and grown by later ones.</summary>
        public GpuBuffer? GeometryTable { get; private set; }

        private int _tableCapacity;

        /// <summary>
        /// Room for <paramref name="count"/> entries in the geometry table. A target is only handed out once nothing
        /// reads it, so a table that's too small can go at once.
        /// </summary>
        public Span<GpuTraceGeometry> Table(int count)
        {
            if (GeometryTable is null || _tableCapacity < count)
            {
                GeometryTable?.Dispose();
                _tableCapacity = (int)Math.Max(16u, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)count));
                GeometryTable = GpuBuffer.CreateHostVisible(
                    _context, (ulong)(_tableCapacity * sizeof(GpuTraceGeometry)), BufferUsageFlags.StorageBufferBit);
            }

            return new Span<GpuTraceGeometry>(GeometryTable.Mapped, count);
        }
        public readonly ulong Address;

        public TopLevelTarget(VulkanContext context, KhrAccelerationStructure api, int capacity, ulong size, ulong scratchSize)
        {
            _context = context;
            _api = api;
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
            (Handle, Address) = CreateStructure(context, api, _buffer, size, AccelerationStructureTypeKHR.TopLevelKhr);
        }

        public void Dispose()
        {
            _api.DestroyAccelerationStructure(_context.Device, Handle, null);
            _buffer.Dispose();
            Instances.Dispose();
            GeometryTable?.Dispose();
        }
    }

    private static (AccelerationStructureKHR Handle, ulong Address) CreateStructure(
        VulkanContext context, KhrAccelerationStructure api, GpuBuffer buffer, ulong size, AccelerationStructureTypeKHR type)
    {
        var createInfo = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = buffer.Handle,
            Size = size,
            Type = type,
        };
        api.CreateAccelerationStructure(context.Device, &createInfo, null, out var handle).Check();

        var addressInfo = new AccelerationStructureDeviceAddressInfoKHR
        {
            SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
            AccelerationStructure = handle,
        };
        return (handle, api.GetAccelerationStructureDeviceAddress(context.Device, &addressInfo));
    }

    /// <summary>One built bottom-level structure and the buffer it lives in.</summary>
    private sealed class BottomLevelStructure : IDisposable
    {
        private readonly RayTracing _owner;
        private readonly VulkanContext _context;
        private readonly KhrAccelerationStructure _api;
        private readonly GpuBuffer _buffer;

        public readonly TracePart[] Parts;
        public readonly AccelerationStructureKHR Handle;
        public readonly ulong Address;

        public BottomLevelStructure(RayTracing owner, TracePart[] parts, ulong size)
        {
            _owner = owner;
            _context = owner._context;
            _api = owner._api;
            Parts = parts;
            _buffer = new GpuBuffer(_context, size,
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.DeviceLocalBit);
            (Handle, Address) = CreateStructure(_context, _api, _buffer, size, AccelerationStructureTypeKHR.BottomLevelKhr);
        }

        public void Dispose()
        {
            _api.DestroyAccelerationStructure(_context.Device, Handle, null);
            _buffer.Dispose();
            _owner._bottomLevelsReleased++;
        }
    }
}
