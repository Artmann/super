using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Rendition.Vulkan;

/// <summary>
/// The acceleration structures rays are traced through. A mesh's bottom-level structure is built the first time
/// something traces it, into whatever command buffer asks, and kept until the mesh is destroyed. Exists only where
/// the device can trace. See "Ray tracing" in docs/design.md.
/// </summary>
internal sealed unsafe class RayTracing : IDisposable
{
    private readonly VulkanContext _context;
    private readonly KhrAccelerationStructure _api;
    private readonly ResourceTable _resources;
    private readonly Func<MeshHandle, Mesh> _meshes;
    private readonly Action<IDisposable> _releaseAfterSubmitted;
    private readonly Action<IDisposable> _releaseAfterRecorded;
    private readonly ulong _scratchAlignment;

    // Every built structure, by mesh. A mesh rarely has more than one, so a list searched by part list is enough.
    private readonly Dictionary<MeshHandle, List<BottomLevelStructure>> _bottomLevels = [];
    private int _bottomLevelCount;

    // Shared by every build. Each build waits for the one before it, so one buffer serves them all; it grows when a
    // build needs more.
    private GpuBuffer? _scratch;

    /// <param name="meshes">The live mesh behind a handle.</param>
    /// <param name="releaseAfterSubmitted">
    /// Frees something once every frame submitted so far is done with it, for a destroyed mesh's structures.
    /// </param>
    /// <param name="releaseAfterRecorded">
    /// Frees something once the work recorded so far, including the command buffer being recorded now, is done with
    /// it, for a scratch buffer that has been outgrown.
    /// </param>
    public RayTracing(
        VulkanContext context,
        ResourceTable resources,
        Func<MeshHandle, Mesh> meshes,
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

    /// <summary>
    /// The device address of the bottom-level structure for these parts of the mesh. The first call for a mesh and
    /// part list records its build into <paramref name="cmd"/>, followed by a barrier, so later work in the same
    /// command buffer can read it; later calls return the same structure and record nothing. The mesh's bytes must
    /// already be uploaded by work that runs before <paramref name="cmd"/>, or earlier in it.
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

        var created = Build(cmd, _meshes(mesh), parts);
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

            var structure = new BottomLevelStructure(_context, _api, parts.ToArray(), sizes.AccelerationStructureSize);
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

        return (_scratch.Address + _scratchAlignment - 1) / _scratchAlignment * _scratchAlignment;
    }

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
        _scratch?.Dispose();
        _scratch = null;
    }

    /// <summary>One built bottom-level structure and the buffer it lives in.</summary>
    private sealed class BottomLevelStructure : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly KhrAccelerationStructure _api;
        private readonly GpuBuffer _buffer;

        public readonly TracePart[] Parts;
        public readonly AccelerationStructureKHR Handle;
        public readonly ulong Address;

        public BottomLevelStructure(VulkanContext context, KhrAccelerationStructure api, TracePart[] parts, ulong size)
        {
            _context = context;
            _api = api;
            Parts = parts;
            _buffer = new GpuBuffer(context, size,
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.DeviceLocalBit);

            var createInfo = new AccelerationStructureCreateInfoKHR
            {
                SType = StructureType.AccelerationStructureCreateInfoKhr,
                Buffer = _buffer.Handle,
                Size = size,
                Type = AccelerationStructureTypeKHR.BottomLevelKhr,
            };
            api.CreateAccelerationStructure(context.Device, &createInfo, null, out Handle).Check();

            var addressInfo = new AccelerationStructureDeviceAddressInfoKHR
            {
                SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
                AccelerationStructure = Handle,
            };
            Address = api.GetAccelerationStructureDeviceAddress(context.Device, &addressInfo);
        }

        public void Dispose()
        {
            _api.DestroyAccelerationStructure(_context.Device, Handle, null);
            _buffer.Dispose();
        }
    }
}
