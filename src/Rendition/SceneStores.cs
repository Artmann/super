using Silk.NET.Vulkan;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The renderer's long-lived stores, which its assets, bakes and frames all share: the device, the settings, the
/// meshes, the bindless textures and materials, what waits to be freed or uploaded, the frame slots and the
/// structures rays are traced through.
/// </summary>
internal sealed class SceneStores
{
    public SceneStores(RendererOptions options)
    {
        Context = new VulkanContext(options);
        Uploads = new UploadQueue(Context);
        Settings = new RenderSettings(options);
        Context.Api.GetPhysicalDeviceProperties(Context.PhysicalDevice, out var deviceProperties);
        MaxImageSize = (int)Math.Min(deviceProperties.Limits.MaxImageDimension2D, int.MaxValue);
        Kit = new DescriptorKit(Context);
        Gpu = new VulkanGpu(Context);
        Frames = new FrameSlots(Context, Kit);
        Resources = new ResourceTable(Context);
        Retired = new RetiredResources(Ring, Meshes, Resources);
        if (Context.CanTrace)
        {
            Tracing = new RayTracing(Context, Resources, (MeshFor, Uploads.IsPending),
                (AfterSubmitted: structure => Retired.AfterLastSubmitted.FreedOthers.Add(structure),
                AfterRecorded: used => Retired.AfterRecording.FreedOthers.Add(used)));
        }
    }

    /// <summary>The Vulkan device and what it can do.</summary>
    public VulkanContext Context { get; }

    /// <summary>How the renderer draws: the quality and cost settings that take effect on the next frame.</summary>
    public RenderSettings Settings { get; }

    /// <summary>The largest image side the device can make, which the scene is shrunk to fit.</summary>
    public int MaxImageSize { get; }

    /// <summary>Which frame slot is being drawn, and which the GPU may still be reading.</summary>
    public FrameRing Ring { get; } = new();

    /// <summary>The live meshes and the detail of their parts.</summary>
    public MeshStore Meshes { get; } = new();

    /// <summary>What is let go of once the frames that read it are done.</summary>
    public RetiredResources Retired { get; }

    /// <summary>The bytes that ride the next frame to the GPU.</summary>
    public UploadQueue Uploads { get; }

    /// <summary>The bindless textures and materials, which a shader that traces rays reads hits through.</summary>
    public ResourceTable Resources { get; }

    /// <summary>The acceleration structures rays are traced through, or null when the GPU can't trace.</summary>
    public RayTracing? Tracing { get; }

    /// <summary>Counts the changes that can change any light's shadow tile, such as a mesh or texture going.</summary>
    public ShadowWorld ShadowWorld { get; } = new();

    /// <summary>The samplers and set layouts every pass shares.</summary>
    public DescriptorKit Kit { get; }

    /// <summary>The GPU layer, which the passes that have moved to it draw through.</summary>
    public VulkanGpu Gpu { get; }

    /// <summary>Each frame in flight's command buffer, uniforms, instances and bones.</summary>
    public FrameSlots Frames { get; }

    /// <summary>What the frame being drawn leaves out, and the debug view it shows.</summary>
    public FrameSwitches Switches { get; } = new();

    /// <summary>The mesh behind a live handle. Tests read its buffers' addresses through it.</summary>
    public Mesh MeshFor(MeshHandle handle) => Meshes.LiveMesh(handle);

    /// <summary>
    /// The area, in a live mesh's own units, of the triangles in an index range, for <see cref="LightmapLayout.Build"/>.
    /// Read from a running total kept when the mesh was made. Throws for a mesh that isn't live.
    /// </summary>
    public float AreaOf(MeshHandle handle, uint firstIndex, uint indexCount) => Meshes.TriangleArea(handle, firstIndex, indexCount);

    /// <summary>How rays treat a material, for <see cref="TraceScene.Collect"/>.</summary>
    public TraceFlags TraceFlagsOf(MaterialHandle material) =>
        TraceEncoding.TraceFlagsFor(Resources.Materials[material].Cutouts, Resources.Materials[material].Flags);

    /// <summary>
    /// Frees the GPU layer, the bindless table, the meshes, the frame slots and the shared samplers, but not the
    /// device. The passes go first, so nothing drawn through the layer is left.
    /// </summary>
    public void DisposeStores()
    {
        Gpu.Dispose();
        Resources.Dispose();
        Meshes.Dispose();
        Frames.Dispose();
        Kit.Dispose();
    }
}
