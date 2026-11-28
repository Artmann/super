namespace Rendition.Gpu;

/// <summary>
/// The GPU as the renderer's passes see it, shaped like WebGPU so a browser backend can stand in for Vulkan.
/// Everything it makes is an id; drawing is recorded into a <see cref="CommandList"/>. See "GPU layer" in
/// docs/design.md.
/// </summary>
internal abstract class GpuDevice : IDisposable
{
    public abstract SamplerId CreateSampler(in SamplerDescription description);

    /// <summary>A compiled shader from the shaders folder, such as <c>tonemap.frag</c>, with the names of its bindings.</summary>
    public abstract ShaderId CreateShader(string name);

    public abstract PipelineId CreateRenderPipeline(in RenderPipelineDescription description);

    public abstract PipelineId CreateComputePipeline(in ComputePipelineDescription description);

    /// <summary>
    /// The resources for one group of <paramref name="pipeline"/>'s shaders. Each <see cref="Bind"/> names the binding
    /// it fills; a name the shaders lack, or one in another group, throws.
    /// </summary>
    public abstract BindGroupId CreateBindGroup(PipelineId pipeline, int group, ReadOnlySpan<Bind> binds);

    public abstract void Destroy(PipelineId pipeline);

    public abstract void Destroy(BindGroupId bindGroup);

    public abstract void Dispose();
}
