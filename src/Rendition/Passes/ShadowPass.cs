using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>Renders the scene's depth as seen from the shadow-casting light.</summary>
internal sealed unsafe class ShadowPass : IDisposable
{
    public const int Resolution = 4096;

    private static readonly Extent2D Extent = new(Resolution, Resolution);

    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly PipelineLayout _layout;
    private readonly List<PipelineVariants> _pipelines = [];

    public readonly RenderTarget Target;

    public ShadowPass(VulkanContext context, DescriptorSetLayout frameSetLayout, ResourceTable resources)
    {
        _context = context;
        _resources = resources;
        Target = new RenderTarget(
            context, GBuffer.DepthFormat, Extent, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);

        var setLayouts = stackalloc DescriptorSetLayout[] { frameSetLayout, resources.Layout };
        var pushRange = SceneDraws.PushRange;
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
            new(1, 0, Format.R32G32Sfloat, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Uv))),
        ];
        var description = new PipelineDescription
        {
            VertexShader = "shadow.vert",
            FragmentShader = "shadow.frag",
            Layout = _layout,
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = bindings,
            VertexAttributes = attributes,
        };
        var culled = PipelineFactory.Create(context, description);
        description.TwoSided = true;
        _pipelines.Add(new PipelineVariants(culled, PipelineFactory.Create(context, description)));
    }

    /// <summary>
    /// A reversed-Z parallel projection covering a sphere, matching the camera's
    /// conventions so the same pipelines' winding and depth test apply.
    /// </summary>
    public static Matrix4x4 CreateMatrix(Vector3 lightDirection, Vector3 center, float radius)
    {
        var up = MathF.Abs(lightDirection.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(center + lightDirection * radius, center, up);
        var projection = new Matrix4x4(
            1f / radius, 0f, 0f, 0f,
            0f, -1f / radius, 0f, 0f,
            0f, 0f, 0.5f / radius, 0f,
            0f, 0f, 1f, 1f);
        return view * projection;
    }

    public void Record(CommandBuffer cmd, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, List<Mesh> meshes)
    {
        var api = _context.Api;
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = Target.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0)), // Reversed-Z far plane.
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, Extent),
            LayerCount = 1,
            PDepthAttachment = &depthAttachment,
        };

        api.CmdBeginRendering(cmd, &rendering);
        api.SetFullViewport(cmd, Extent);

        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 2, sets, 0, null);
        SceneDraws.Record(api, cmd, _layout, _pipelines, _resources, draws, meshes);

        api.CmdEndRendering(cmd);
    }

    public void Dispose()
    {
        _context.Api.DestroyPipeline(_context.Device, _pipelines[0].TwoSided, null);
        _context.Api.DestroyPipeline(_context.Device, _pipelines[0].Culled, null);
        _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
        Target.Dispose();
    }
}
