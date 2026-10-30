using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>Writes surface properties for every visible pixel. No lighting happens here.</summary>
internal sealed unsafe class GBufferPass : IDisposable
{
    /// <summary>Must match the Draw block in Shaders/gbuffer.vert and gbuffer.frag.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DrawConstants
    {
        public Matrix4x4 Model;
        public int Material;
    }

    private const ShaderStageFlags PushStages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;

    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly PipelineLayout _layout;
    private readonly Pipeline _culledPipeline;
    private readonly Pipeline _twoSidedPipeline;

    public GBufferPass(VulkanContext context, DescriptorSetLayout frameSetLayout, ResourceTable resources)
    {
        _context = context;
        _resources = resources;

        var setLayouts = stackalloc DescriptorSetLayout[] { frameSetLayout, resources.Layout };
        var pushRange = new PushConstantRange(PushStages, 0, (uint)sizeof(DrawConstants));
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = setLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        context.Api.CreatePipelineLayout(context.Device, &layoutInfo, null, out _layout).Check();

        ReadOnlySpan<VertexInputBindingDescription> bindings =
        [
            new(0, (uint)sizeof(Vertex), VertexInputRate.Vertex),
        ];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new(0, 0, Format.R32G32B32Sfloat, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position))),
            new(1, 0, Format.R32G32B32Sfloat, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Normal))),
            new(2, 0, Format.R32G32B32A32Sfloat, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Tangent))),
            new(3, 0, Format.R32G32Sfloat, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Uv))),
        ];
        var description = new PipelineDescription
        {
            VertexShader = "gbuffer.vert",
            FragmentShader = "gbuffer.frag",
            Layout = _layout,
            ColorFormats = [GBuffer.BaseColorFormat, GBuffer.NormalFormat],
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = bindings,
            VertexAttributes = attributes,
        };
        _culledPipeline = PipelineFactory.Create(context, description);
        description.TwoSided = true;
        _twoSidedPipeline = PipelineFactory.Create(context, description);
    }

    public void Record(
        CommandBuffer cmd,
        GBuffer targets,
        DescriptorSet frameSet,
        ReadOnlySpan<DrawItem> draws,
        List<Mesh> meshes)
    {
        var api = _context.Api;

        var colorAttachments = stackalloc RenderingAttachmentInfo[2];
        colorAttachments[0] = ColorAttachment(targets.BaseColor.View);
        colorAttachments[1] = ColorAttachment(targets.Normal.View);
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = targets.Depth.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0)), // Reversed-Z far plane.
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, targets.Extent),
            LayerCount = 1,
            ColorAttachmentCount = 2,
            PColorAttachments = colorAttachments,
            PDepthAttachment = &depthAttachment,
        };

        api.CmdBeginRendering(cmd, &rendering);
        api.SetFullViewport(cmd, targets.Extent);

        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 2, sets, 0, null);

        var boundPipeline = default(Pipeline);
        int boundMesh = -1;
        foreach (ref readonly var draw in draws)
        {
            var pipeline = (_resources.GetFlags(draw.Material) & MaterialFlags.TwoSided) != 0
                ? _twoSidedPipeline
                : _culledPipeline;
            if (pipeline.Handle != boundPipeline.Handle)
            {
                api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
                boundPipeline = pipeline;
            }

            if (draw.Mesh.Index != boundMesh)
            {
                var mesh = meshes[draw.Mesh.Index];
                var vertexBuffer = mesh.Vertices.Handle;
                ulong offset = 0;
                api.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
                api.CmdBindIndexBuffer(cmd, mesh.Indices.Handle, 0, IndexType.Uint32);
                boundMesh = draw.Mesh.Index;
            }

            var constants = new DrawConstants { Model = draw.Transform, Material = draw.Material.Index };
            api.CmdPushConstants(cmd, _layout, PushStages, 0, (uint)sizeof(DrawConstants), &constants);
            api.CmdDrawIndexed(cmd, draw.IndexCount, 1, draw.FirstIndex, 0, 0);
        }

        api.CmdEndRendering(cmd);
    }

    public void Dispose()
    {
        _context.Api.DestroyPipeline(_context.Device, _twoSidedPipeline, null);
        _context.Api.DestroyPipeline(_context.Device, _culledPipeline, null);
        _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
    }

    private static RenderingAttachmentInfo ColorAttachment(ImageView view) => new()
    {
        SType = StructureType.RenderingAttachmentInfo,
        ImageView = view,
        ImageLayout = ImageLayout.ColorAttachmentOptimal,
        LoadOp = AttachmentLoadOp.Clear,
        StoreOp = AttachmentStoreOp.Store,
    };
}
