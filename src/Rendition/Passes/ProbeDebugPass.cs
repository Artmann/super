using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// The probe debug view (<see cref="DebugView.Probes"/>): after glass and particles, probe_debug.vert draws a small
/// sphere round every light probe of the finest cascade and of the fixed grid into the lit HDR target, and
/// probe_debug.frag shades it from the probe's own irradiance tile, through the GPU layer. It reads the atlases
/// through the lighting pass's set, so it needs no group of its own and works on any GPU that runs the probes, traced
/// or not. <see cref="BlendStage"/> moves Hdr and depth itself, so both are adopted as targets the layer leaves alone.
/// See "Light probes" in docs/design.md.
/// </summary>
internal sealed class ProbeDebugPass : IDisposable
{
    // Must match probe_debug.vert.
    private const uint Segments = 12;
    private const uint Rings = 8;

    /// <summary>Vertices in one sphere: two triangles for each quad of its segments and rings.</summary>
    public const uint VerticesPerSphere = Segments * Rings * 6;

    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly PipelineId _pipeline;
    private readonly AdoptedTarget _hdr;
    private readonly AdoptedTarget _depth;
    private readonly CommandList _commands = new();

    /// <param name="device">The device and the GPU layer over it.</param>
    /// <param name="frameSetLayout">Group 0: the frame's uniforms, which say where the probes are.</param>
    /// <param name="lightingSetLayout">Group 1: the lighting pass's textures, among them the probe atlases.</param>
    public ProbeDebugPass((VulkanContext Context, VulkanGpu Gpu) device, DescriptorSetLayout frameSetLayout, DescriptorSetLayout lightingSetLayout)
    {
        (_context, _gpu) = device;
        _hdr = new AdoptedTarget(_gpu);
        _depth = new AdoptedTarget(_gpu);

        // Opaque, so the spheres hide each other and anything behind them: reversed-Z, nearer is greater. Both sets
        // are made outside the layer, so their layouts are lent.
        _pipeline = _gpu.CreateRenderPipeline(new RenderPipelineDescription
        {
            VertexShader = _gpu.CreateShader("probe_debug.vert"),
            FragmentShader = _gpu.CreateShader("probe_debug.frag"),
            Targets = [new ColorTargetState(VulkanFormats.ToGpu(GBuffer.HdrFormat))],
            Depth = new DepthState(VulkanFormats.ToGpu(GBuffer.DepthFormat), CompareFunction.Greater, Write: true),
            Cull = CullMode.Back,
        }, [frameSetLayout, lightingSetLayout]);
    }

    /// <summary>
    /// Draws <paramref name="spheres"/> spheres over the lit image: the finest cascade's probes, then the fixed grid's.
    /// Hdr must be a colour attachment and depth read-only, as the transparent pass leaves them, and both are left so.
    /// Depth is written while the spheres draw, so they hide each other.
    /// </summary>
    public void Record(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, DescriptorSet lightingSet, uint spheres)
    {
        var api = _context.Api;
        const PipelineStageFlags2 depthStages =
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
        const PipelineStageFlags2 colorStage = PipelineStageFlags2.ColorAttachmentOutputBit;
        const AccessFlags2 colorAccess = AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit;
        const AccessFlags2 depthRead = AccessFlags2.DepthStencilAttachmentReadBit;
        const AccessFlags2 depthAccess = depthRead | AccessFlags2.DepthStencilAttachmentWriteBit;

        api.Transition(cmd, new ImageTransition(targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal), (colorStage, colorAccess), (colorStage, colorAccess));
        api.Transition(cmd, new ImageTransition(targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.DepthReadOnlyOptimal, ImageLayout.DepthAttachmentOptimal),
            (depthStages | PipelineStageFlags2.FragmentShaderBit, depthRead | AccessFlags2.ShaderSampledReadBit), (depthStages, depthAccess));

        _commands.Reset();
        _commands.BeginRenderPass(
            [new ColorTarget(_hdr.ForHeld(targets.Hdr.View, targets.Hdr.Image, targets.Extent), LoadOp.Load, default)],
            new DepthTarget(_depth.ForHeld(targets.Depth.View, targets.Depth.Image, targets.Extent), LoadOp.Load, 0f, ReadOnly: false));
        _commands.SetPipeline(_pipeline);
        _commands.SetBindGroups([_gpu.Adopt(frameSet), _gpu.Adopt(lightingSet)]);
        _commands.Draw((int)VerticesPerSphere, (int)spheres, 0, 0);
        _commands.EndPass();
        _gpu.Play(_commands, cmd);

        api.Transition(cmd, new ImageTransition(targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.DepthAttachmentOptimal, ImageLayout.DepthReadOnlyOptimal),
            (depthStages, AccessFlags2.DepthStencilAttachmentWriteBit),
            (depthStages | PipelineStageFlags2.FragmentShaderBit, depthRead | AccessFlags2.ShaderSampledReadBit));
    }

    public void Dispose()
    {
        _hdr.Dispose();
        _depth.Dispose();
        _gpu.Destroy(_pipeline);
    }
}
