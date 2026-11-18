using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Every built bottom-level structure, by mesh and part list. A mesh's structure is built the first time something
/// traces it, into whatever command buffer asks, and kept until the mesh is destroyed or a part's material stops or
/// starts being opaque to rays.
/// </summary>
internal sealed unsafe class BottomLevels : IDisposable
{
    /// <summary>One built bottom-level structure and the buffer it lives in.</summary>
    private sealed class Built : IDisposable
    {
        private readonly BottomLevels _owner;
        private readonly GpuBuffer _buffer;

        public readonly TracePart[] Parts;
        public readonly AccelerationStructureKHR Handle;
        public readonly ulong Address;

        public Built(BottomLevels owner, TracePart[] parts, ulong size)
        {
            _owner = owner;
            Parts = parts;
            _buffer = new GpuBuffer(owner._builder.Context, size,
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.DeviceLocalBit);
            (Handle, Address) = owner._builder.CreateStructure(_buffer, size, AccelerationStructureTypeKHR.BottomLevelKhr);
        }

        public void Dispose()
        {
            _owner._builder.Api.DestroyAccelerationStructure(_owner._builder.Context.Device, Handle, null);
            _buffer.Dispose();
            _owner.Released++;
        }
    }

    private readonly StructureBuilder _builder;
    private readonly ResourceTable _resources;
    private readonly (Func<MeshHandle, Mesh> Live, Func<GpuBuffer, bool> UploadPending) _meshes;
    private readonly Action<IDisposable> _releaseAfterSubmitted;

    // Every built structure, by mesh. A mesh rarely has more than one, so a list searched by part list is enough.
    private readonly Dictionary<MeshHandle, List<Built>> _built = [];

    // The bottom-level structure of each instance of the build being made. Reused by every build.
    private ulong[] _addresses = [];

    /// <param name="builder">Builds the structures.</param>
    /// <param name="resources">Says which materials are opaque to rays.</param>
    /// <param name="meshes">The live mesh behind a handle, and whether a buffer's bytes are still waiting in the upload queue.</param>
    /// <param name="releaseAfterSubmitted">Frees a forgotten structure once every frame submitted so far is done with it.</param>
    public BottomLevels(
        StructureBuilder builder, ResourceTable resources, (Func<MeshHandle, Mesh> Live, Func<GpuBuffer, bool> UploadPending) meshes,
        Action<IDisposable> releaseAfterSubmitted)
    {
        _builder = builder;
        _resources = resources;
        _meshes = meshes;
        _releaseAfterSubmitted = releaseAfterSubmitted;
    }

    /// <summary>How many bottom-level structures are live. For tests.</summary>
    public int Count { get; private set; }

    /// <summary>How many bottom-level structures have really been freed, not just forgotten. For tests.</summary>
    public int Released { get; private set; }

    /// <summary>The bottom-level structure of each instance, built now for a mesh and parts that have none yet.</summary>
    public Span<ulong> AddressesFor(CommandBuffer cmd, IReadOnlyList<TraceInstance> instances)
    {
        if (_addresses.Length < instances.Count)
        {
            Array.Resize(ref _addresses, Math.Max(instances.Count, _addresses.Length * 2));
        }

        var addresses = _addresses.AsSpan(0, instances.Count);
        for (int i = 0; i < instances.Count; i++)
        {
            addresses[i] = AddressOf(cmd, instances[i].Mesh, instances[i].Parts.Span);
        }

        return addresses;
    }

    /// <summary>
    /// The device address of the structure for these parts of the mesh, built into <paramref name="cmd"/> and followed
    /// by a barrier the first time. The mesh's bytes must already be uploaded.
    /// </summary>
    public ulong AddressOf(CommandBuffer cmd, MeshHandle mesh, ReadOnlySpan<TracePart> parts)
    {
        if (_built.TryGetValue(mesh, out var built))
        {
            foreach (var structure in built)
            {
                if (parts.SequenceEqual(structure.Parts))
                {
                    return structure.Address;
                }
            }
        }

        var live = _meshes.Live(mesh);
        if (_meshes.UploadPending(live.Vertices) || _meshes.UploadPending(live.Indices))
        {
            throw new InvalidOperationException(
                $"Mesh {mesh.Index}'s bytes haven't been uploaded yet; record the frame's uploads before building its structure.");
        }

        var created = Build(cmd, live, parts);
        if (built is null)
        {
            built = [];
            _built[mesh] = built;
        }

        built.Add(created);
        Count++;
        return created.Address;
    }

    /// <summary>Lets go of every structure built for the mesh, freed once the frames already submitted are done.</summary>
    public void Forget(MeshHandle mesh)
    {
        if (!_built.Remove(mesh, out var built))
        {
            return;
        }

        foreach (var structure in built)
        {
            _releaseAfterSubmitted(structure);
        }

        Count -= built.Count;
    }

    /// <summary>Lets go of every structure with a part drawn in this material, freed the same way as <see cref="Forget"/>.</summary>
    public void ForgetMaterial(MaterialHandle material)
    {
        List<MeshHandle>? emptied = null;
        foreach (var (mesh, built) in _built)
        {
            for (int i = built.Count - 1; i >= 0; i--)
            {
                if (!Array.Exists(built[i].Parts, part => part.Material == material))
                {
                    continue;
                }

                _releaseAfterSubmitted(built[i]);
                built.RemoveAt(i);
                Count--;
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
                _built.Remove(mesh);
            }
        }
    }

    /// <summary>Frees every structure at once. Only once the GPU is idle.</summary>
    public void Dispose()
    {
        foreach (var built in _built.Values)
        {
            foreach (var structure in built)
            {
                structure.Dispose();
            }
        }

        _built.Clear();
        Count = 0;
    }

    private Built Build(CommandBuffer cmd, Mesh mesh, ReadOnlySpan<TracePart> parts)
    {
        if (parts.IsEmpty)
        {
            throw new ArgumentException("A structure needs at least one part.", nameof(parts));
        }

        var (geometries, ranges, triangleCounts) = DescribeParts(mesh, parts);
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
            _builder.Api.GetAccelerationStructureBuildSizes(
                _builder.Context.Device, AccelerationStructureBuildTypeKHR.DeviceKhr, &info, triangleCount, &sizes);

            var structure = new Built(this, parts.ToArray(), sizes.AccelerationStructureSize);
            info.DstAccelerationStructure = structure.Handle;
            info.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = _builder.Scratch(sizes.BuildScratchSize) };
            _builder.Api.CmdBuildAccelerationStructures(cmd, 1, &info, &range);
            _builder.WaitForBuild(cmd);
            return structure;
        }
    }

    /// <summary>
    /// Each part as a geometry of the mesh's triangles, with its range of them and how many there are. Throws for a
    /// part that isn't whole triangles within the mesh.
    /// </summary>
    private (AccelerationStructureGeometryKHR[] Geometries, AccelerationStructureBuildRangeInfoKHR[] Ranges, uint[] TriangleCounts)
        DescribeParts(Mesh mesh, ReadOnlySpan<TracePart> parts)
    {
        var triangles = Triangles(mesh);
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

            var facts = _resources.Materials[part.Material];
            geometries[i] = new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.TrianglesKhr,
                Geometry = new AccelerationStructureGeometryDataKHR { Triangles = triangles },
                Flags = TraceEncoding.GeometryFlagsFor(facts.Cutouts, facts.Flags),
            };
            triangleCounts[i] = part.IndexCount / 3;
            ranges[i] = new AccelerationStructureBuildRangeInfoKHR
            {
                PrimitiveCount = triangleCounts[i],
                PrimitiveOffset = part.FirstIndex * sizeof(uint),
            };
        }

        return (geometries, ranges, triangleCounts);
    }

    // The mesh's vertex positions and indices, as triangles for a bottom-level structure.
    private static AccelerationStructureGeometryTrianglesDataKHR Triangles(Mesh mesh) => new()
    {
        SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
        VertexFormat = Format.R32G32B32Sfloat,
        VertexData = new DeviceOrHostAddressConstKHR
        {
            DeviceAddress = mesh.Vertices.Address + (ulong)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position)),
        },
        VertexStride = (ulong)sizeof(Vertex),
        MaxVertex = (uint)(mesh.Vertices.Size / (ulong)sizeof(Vertex)) - 1,
        IndexType = IndexType.Uint32,
        IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = mesh.Indices.Address },
    };
}
