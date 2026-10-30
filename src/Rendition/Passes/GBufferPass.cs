using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>Writes surface properties for every visible pixel. No lighting happens here.</summary>
internal sealed unsafe class GBufferPass : IDisposable
{
    /// <summary>Must match the Draw block in the gbuffer shaders.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DrawConstants
    {
        public Matrix4x4 Model;
        public Vector4 BaseColor;
        public float Metallic;
        public float Roughness;
    }

    private const ShaderStageFlags PushStages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;

    private readonly VulkanContext _context;
    private readonly PipelineLayout _layout;
    private readonly Pipeline _pipeline;

    public GBufferPass(VulkanContext context, DescriptorSetLayout frameSetLayout)
    {
        _context = context;

        var pushRange = new PushConstantRange(PushStages, 0, (uint)sizeof(DrawConstants));
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &frameSetLayout,
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
        ];
        _pipeline = PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "gbuffer.vert",
            FragmentShader = "gbuffer.frag",
            Layout = _layout,
            ColorFormats = [GBuffer.BaseColorFormat, GBuffer.NormalFormat],
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = bindings,
            VertexAttributes = attributes,
        });
    }

    public void Record(
        CommandBuffer cmd,
        GBuffer targets,
        DescriptorSet frameSet,
        ReadOnlySpan<DrawItem> draws,
        IReadOnlyList<Mesh> meshes)
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
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        api.SetFullViewport(cmd, targets.Extent);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, &frameSet, 0, null);

        foreach (ref readonly var draw in draws)
        {
            var mesh = meshes[draw.Mesh.Index];
            var constants = new DrawConstants
            {
                Model = draw.Transform,
                BaseColor = new Vector4(draw.Material.BaseColor, 1f),
                Metallic = draw.Material.Metallic,
                Roughness = draw.Material.Roughness,
            };
            var vertexBuffer = mesh.Vertices.Handle;
            ulong offset = 0;
            api.CmdPushConstants(cmd, _layout, PushStages, 0, (uint)sizeof(DrawConstants), &constants);
            api.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
            api.CmdBindIndexBuffer(cmd, mesh.Indices.Handle, 0, IndexType.Uint32);
            api.CmdDrawIndexed(cmd, mesh.IndexCount, 1, 0, 0, 0);
        }

        api.CmdEndRendering(cmd);
    }

    public void Dispose()
    {
        _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
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
