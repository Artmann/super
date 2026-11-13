using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// The probe debug view (<see cref="DebugView.Probes"/>): after glass and particles, probe_debug.vert draws a small
/// sphere round every light probe of the finest cascade and of the fixed grid into the lit HDR target, and
/// probe_debug.frag shades it from the probe's own irradiance tile. It reads the atlases through the lighting pass's
/// set, so it needs no set of its own and works on any GPU that runs the probes, traced or not. See "Light probes" in
/// docs/design.md.
/// </summary>
internal sealed unsafe class ProbeDebugPass : IDisposable
{
    // Must match probe_debug.vert.
    private const uint Segments = 12;
    private const uint Rings = 8;

    /// <summary>Vertices in one sphere: two triangles for each quad of its segments and rings.</summary>
    public const uint VerticesPerSphere = Segments * Rings * 6;

    private readonly VulkanContext _context;
    private readonly PipelineLayout _layout;
    private readonly Pipeline _pipeline;

    /// <param name="frameSetLayout">Set 0: the frame's uniforms, which say where the probes are.</param>
    /// <param name="lightingSetLayout">Set 1: the lighting pass's textures, among them the probe atlases.</param>
    public ProbeDebugPass(VulkanContext context, DescriptorSetLayout frameSetLayout, DescriptorSetLayout lightingSetLayout)
    {
        _context = context;
        _layout = context.CreatePipelineLayout([frameSetLayout, lightingSetLayout]);

        // Opaque, so the spheres hide each other and anything behind them.
        var description = new PipelineDescription
        {
            VertexShader = "probe_debug.vert",
            FragmentShader = "probe_debug.frag",
            Layout = _layout,
            ColorFormats = [GBuffer.HdrFormat],
            DepthFormat = GBuffer.DepthFormat,
        };
        _pipeline = PipelineFactory.Create(context, description);
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

        api.Transition(cmd, targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal, colorStage, colorAccess, colorStage, colorAccess);
        api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.DepthReadOnlyOptimal, ImageLayout.DepthAttachmentOptimal,
            depthStages | PipelineStageFlags2.FragmentShaderBit, depthRead | AccessFlags2.ShaderSampledReadBit, depthStages, depthAccess);

        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = targets.Hdr.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = targets.Depth.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, targets.Extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
            PDepthAttachment = &depthAttachment,
        };

        api.CmdBeginRendering(cmd, &rendering);
        api.SetFullViewport(cmd, targets.Extent);
        var sets = stackalloc DescriptorSet[] { frameSet, lightingSet };
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 2, sets, 0, null);
        api.CmdDraw(cmd, VerticesPerSphere, spheres, 0, 0);
        api.CmdEndRendering(cmd);

        api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.DepthAttachmentOptimal, ImageLayout.DepthReadOnlyOptimal,
            depthStages, AccessFlags2.DepthStencilAttachmentWriteBit,
            depthStages | PipelineStageFlags2.FragmentShaderBit, depthRead | AccessFlags2.ShaderSampledReadBit);
    }

    public void Dispose()
    {
        _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
        _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
    }
}
