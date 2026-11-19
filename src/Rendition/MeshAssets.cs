using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The renderer's meshes and the detail of their parts: made by handing their bytes to the next frame, and let go of
/// two frames after they're destroyed, along with the structures rays are traced through. Part of
/// <see cref="RendererAssets"/>.
/// </summary>
internal sealed class MeshAssets(
    (VulkanContext Context, UploadQueue Uploads, RetiredResources Retired, RayTracing? Tracing) gpu,
    MeshStore meshes,
    Action<Func<BakeJob, bool>> stopBakesThat)
{
    /// <summary>See <see cref="RendererAssets.CreateMesh"/>.</summary>
    public MeshHandle Create(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
    {
        var traceInput = GpuBuffer.TraceInput(gpu.Context);
        var vertexBuffer = GpuBuffer.CreateDeviceLocalStaged(gpu.Context, vertices, BufferUsageFlags.VertexBufferBit | traceInput, out var vertexStaging);
        gpu.Uploads.Add(vertexStaging, vertexBuffer);
        var indexBuffer = GpuBuffer.CreateDeviceLocalStaged(gpu.Context, indices, BufferUsageFlags.IndexBufferBit | traceInput, out var indexStaging);
        gpu.Uploads.Add(indexStaging, indexBuffer);
        return meshes.AddMesh(new Mesh(vertexBuffer, indexBuffer, (uint)indices.Length)
        {
            CumulativeAreas = LightmapLayout.CumulativeAreas(vertices, indices),
        });
    }

    /// <summary>See <see cref="RendererAssets.DestroyMesh"/>.</summary>
    public void Destroy(MeshHandle mesh)
    {
        var live = meshes.TakeMesh(mesh);

        // A mesh made this frame may still be waiting for its bytes. That copy must not
        // run into a buffer that is about to be freed.
        gpu.Uploads.Cancel(live.Vertices);
        gpu.Uploads.Cancel(live.Indices);
        gpu.Retired.AfterLastSubmitted.FreedMeshes.Add(live);

        // A background bake still tracing it would read freed buffers.
        stopBakesThat(job => job.Area.Uses(mesh));

        // Its structures read its buffers, and its handle may go to another mesh, so they go with it.
        gpu.Tracing?.Forget(mesh);
    }

    /// <summary>
    /// Uploads the parts of one mesh as draws the renderer can skip when they're out of view and, when
    /// <paramref name="simplify"/> is true, swap for simpler versions when they're small on screen. Each part's culling
    /// sphere is scaled by <paramref name="radiusScale"/>.
    /// </summary>
    public DrawItem[] CreateDetailed(
        ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<ModelPart> parts,
        ReadOnlySpan<MaterialHandle> materials, bool simplify = true, float radiusScale = 1f)
    {
        var allIndices = new List<uint>(indices.ToArray());
        var draws = new DrawItem[parts.Length];
        var details = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            ref readonly var part = ref parts[i];
            var partIndices = indices.Slice((int)part.FirstIndex, (int)part.IndexCount);
            var detail = simplify
                ? MeshDetail.Build(vertices, partIndices, allIndices)
                : MeshDetail.Full(vertices, partIndices);
            details[i] = meshes.AddDetail(detail with { Radius = detail.Radius * radiusScale });
            draws[i] = new DrawItem
            {
                FirstIndex = part.FirstIndex,
                IndexCount = part.IndexCount,
                Material = materials[part.Material],
                Transform = Matrix4x4.Identity,
                Detail = new DetailHandle(details[i]),
            };
        }

        var mesh = Create(vertices, CollectionsMarshal.AsSpan(allIndices));
        meshes.LiveMesh(mesh).Details = details;
        foreach (ref var draw in draws.AsSpan())
        {
            draw.Mesh = mesh;
        }

        return draws;
    }

    /// <summary>See <see cref="RendererAssets.BoundsOf"/>.</summary>
    public BakeSphere BoundsOf(DetailHandle detail) => meshes.PartBounds(detail);
}
