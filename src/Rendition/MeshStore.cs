namespace Rendition;

/// <summary>
/// The renderer's meshes and the detail entries of their parts. A freed slot goes to the next one made, so handles
/// stay small. The passes read both lists by index.
/// </summary>
internal sealed class MeshStore : IDisposable
{
    private readonly Stack<int> _freeMeshes = [];
    private readonly Stack<int> _freeDetails = [];

    /// <summary>Every mesh slot, null where a mesh was freed.</summary>
    public List<Mesh?> MeshSlots { get; } = [];

    /// <summary>Every detail slot. Slot 0 stays empty, for draws made without detail.</summary>
    public List<PartDetail> DetailSlots { get; } = [default];

    public MeshHandle AddMesh(Mesh mesh)
    {
        if (_freeMeshes.Count > 0)
        {
            int slot = _freeMeshes.Pop();
            MeshSlots[slot] = mesh;
            return new MeshHandle(slot);
        }

        MeshSlots.Add(mesh);
        return new MeshHandle(MeshSlots.Count - 1);
    }

    public int AddDetail(in PartDetail detail)
    {
        if (_freeDetails.Count > 0)
        {
            int slot = _freeDetails.Pop();
            DetailSlots[slot] = detail;
            return slot;
        }

        DetailSlots.Add(detail);
        return DetailSlots.Count - 1;
    }

    /// <summary>The mesh behind a live handle. Throws for a handle that isn't live.</summary>
    public Mesh LiveMesh(MeshHandle handle) =>
        handle.Index >= 0 && handle.Index < MeshSlots.Count && MeshSlots[handle.Index] is { } mesh
            ? mesh
            : throw new ArgumentException($"Mesh {handle.Index} is not live.", nameof(handle));

    /// <summary>
    /// Takes a live mesh out of its slot, which the next mesh made can have. Its detail entries stay until
    /// <see cref="FreeDetailsOf"/>, once no frame in flight reads them.
    /// </summary>
    public Mesh TakeMesh(MeshHandle mesh)
    {
        if (mesh.Index < 0 || mesh.Index >= MeshSlots.Count || MeshSlots[mesh.Index] is not { } live)
        {
            throw new ArgumentException("The mesh does not exist.", nameof(mesh));
        }

        MeshSlots[mesh.Index] = null;
        _freeMeshes.Push(mesh.Index);
        return live;
    }

    /// <summary>Frees the detail entries made for a mesh's parts.</summary>
    public void FreeDetailsOf(Mesh mesh)
    {
        foreach (int detail in mesh.Details)
        {
            DetailSlots[detail] = default;
            _freeDetails.Push(detail);
        }
    }

    /// <summary>
    /// The sphere around a part, in the model's units, before its draw's transform. A part without detail is the
    /// point at its origin.
    /// </summary>
    public BakeSphere PartBounds(DetailHandle detail)
    {
        var part = DetailSlots[detail.Index];
        return new BakeSphere(part.Center, part.Radius);
    }

    /// <summary>
    /// The area, in a live mesh's own units, of the triangles in an index range, for <see cref="LightmapLayout.Build"/>.
    /// Read from a running total kept when the mesh was made. Throws for a mesh that isn't live.
    /// </summary>
    public float TriangleArea(MeshHandle handle, uint firstIndex, uint indexCount) =>
        LightmapLayout.RangeArea(LiveMesh(handle).CumulativeAreas, firstIndex, indexCount);

    public void Dispose()
    {
        foreach (var mesh in MeshSlots)
        {
            mesh?.Dispose();
        }
    }
}
