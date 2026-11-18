using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The acceleration structures rays are traced through. A mesh's bottom-level structure is built the first time
/// something traces it, into whatever command buffer asks, and kept until the mesh is destroyed (<see cref="BottomLevels"/>);
/// top-level structures are rebuilt whole for each frame from a pool (<see cref="TopLevelPool"/>), and every build
/// shares a <see cref="StructureBuilder"/>. Exists only where the device can trace. See "Ray tracing" in docs/design.md.
/// </summary>
internal sealed unsafe class RayTracing : IDisposable
{
    /// <summary>
    /// The stages that trace rays and read hit data: mesh bytes through buffer addresses and textures through the
    /// resource table. Uploads and <see cref="ReadyToTrace"/> make their writes visible to these stages.
    /// </summary>
    public const PipelineStageFlags2 TraceStages = TraceEncoding.TraceStages;

    private readonly StructureBuilder _builder;
    private readonly BottomLevels _bottomLevels;
    private readonly TopLevelPool _topLevels;
    private readonly Func<MeshHandle, Mesh> _meshes;

    // The parts of the build being made, before they're copied into its geometry table. Reused by every build.
    private readonly List<TraceGeometry> _packed = [];

    /// <param name="context">The device, which must be able to trace.</param>
    /// <param name="resources">Says which materials are opaque to rays.</param>
    /// <param name="meshes">The live mesh behind a handle, and whether a buffer's bytes are still waiting in the upload queue.</param>
    /// <param name="release">
    /// How to free something once every frame submitted so far is done with it, for a destroyed mesh's structures,
    /// and once the work recorded so far, including the command buffer being recorded now, is done with it, for a
    /// scratch buffer that has been outgrown and for a top-level structure going back to the pool. That assumes the
    /// command buffer is the frame being recorded or is waited on before the next frame.
    /// </param>
    public RayTracing(
        VulkanContext context,
        ResourceTable resources,
        (Func<MeshHandle, Mesh> Live, Func<GpuBuffer, bool> UploadPending) meshes,
        (Action<IDisposable> AfterSubmitted, Action<IDisposable> AfterRecorded) release)
    {
        if (!context.CanTrace)
        {
            throw new InvalidOperationException("Ray tracing needs a device that can trace.");
        }

        _meshes = meshes.Live;
        _builder = new StructureBuilder(context, release.AfterRecorded);
        _bottomLevels = new BottomLevels(_builder, resources, meshes, release.AfterSubmitted);
        _topLevels = new TopLevelPool(_builder, release.AfterRecorded);
    }

    /// <summary>How many bottom-level structures are live. For tests.</summary>
    public int BottomLevelCount => _bottomLevels.Count;

    /// <summary>How many bottom-level structures have really been freed, not just forgotten. For tests.</summary>
    public int BottomLevelsReleased => _bottomLevels.Released;

    /// <summary>How many top-level structures have been built. For tests.</summary>
    public int TopLevelBuilds { get; private set; }

    /// <summary>How many top-level structures have been made to build into, rather than reused. For tests.</summary>
    public int TopLevelStructuresMade => _topLevels.StructuresMade;

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
    /// belongs to <paramref name="cmd"/> as any other build does. Only the top level is held: <see cref="Forget"/> and
    /// <see cref="ForgetMaterial"/> still free the bottom levels it points at, so whoever holds it must stop reading it
    /// before either runs, as the renderer stops its background bake.
    /// </summary>
    public TopLevelStructure TopLevel(CommandBuffer cmd, IReadOnlyList<TraceInstance> instances, out IDisposable release)
    {
        var structure = Build(cmd, instances, true, out var held);
        release = held!;
        return structure;
    }

    /// <summary>
    /// Makes compute shaders recorded after this wait for every structure built before it, so they can trace them.
    /// <see cref="TopLevel(CommandBuffer, IReadOnlyList{TraceInstance})"/>'s own barrier only covers later builds.
    /// </summary>
    public void ReadyToTrace(CommandBuffer cmd) => _builder.ReadyToTrace(cmd);

    /// <summary>
    /// The device address of the bottom-level structure for these parts of the mesh. The first call for a mesh and
    /// part list records its build into <paramref name="cmd"/>, followed by a barrier, so later work in the same
    /// command buffer can read it; later calls return the same structure and record nothing. The mesh's bytes must
    /// already be uploaded by work that runs before <paramref name="cmd"/>, or earlier in it; a build from a mesh whose
    /// copy is still queued throws rather than caching a structure made from empty memory.
    /// </summary>
    public ulong BottomLevel(CommandBuffer cmd, MeshHandle mesh, ReadOnlySpan<TracePart> parts) =>
        _bottomLevels.AddressOf(cmd, mesh, parts);

    /// <summary>
    /// Lets go of every structure built for the mesh. They're freed once the frames already submitted are done, as the
    /// mesh's own buffers are. Call it when the mesh is destroyed, before its handle can be handed out again.
    /// </summary>
    public void Forget(MeshHandle mesh) => _bottomLevels.Forget(mesh);

    /// <summary>
    /// Lets go of every structure with a part drawn in this material, because each part's opaque flag is baked in when
    /// it's built. Call it when the material stops or starts being opaque to rays, and when it's destroyed, since its
    /// handle may be handed to another material. Freed the same way as <see cref="Forget"/>.
    /// </summary>
    public void ForgetMaterial(MaterialHandle material) => _bottomLevels.ForgetMaterial(material);

    /// <summary>Frees every structure and the scratch buffer at once. Only once the GPU is idle.</summary>
    public void Dispose()
    {
        _bottomLevels.Dispose();
        _topLevels.Dispose();
        _builder.Dispose();
    }

    private TopLevelStructure Build(CommandBuffer cmd, IReadOnlyList<TraceInstance> instances, bool hold, out IDisposable? release)
    {
        var bottomLevels = _bottomLevels.AddressesFor(cmd, instances);
        var target = _topLevels.Rent(instances.Count);
        var packed = new Span<AccelerationStructureInstanceKHR>(target.Instances.Mapped, instances.Count);
        _packed.Clear();
        try
        {
            TraceEncoding.Pack(instances, bottomLevels, packed, _packed);
        }
        catch
        {
            // Nothing has been recorded with it, so it can go straight back.
            _topLevels.Return(target);
            throw;
        }

        // Copied into the target's own table now, so nothing holds on to the list once this returns.
        target.WriteTable(_packed, _meshes);
        target.Record(cmd, instances.Count);
        release = _topLevels.HandOut(target, hold);
        TopLevelBuilds++;
        return new TopLevelStructure(target.Handle, target.Address, target.GeometryTable!, _packed.Count);
    }
}
