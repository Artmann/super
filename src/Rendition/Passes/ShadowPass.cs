using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Renders the scene's depth as seen from the shadow-casting light, once per cascade.
/// Each cascade covers three times the radius of the one before, in its own layer.
/// </summary>
internal sealed unsafe class ShadowPass : IDisposable
{
    public const int Resolution = 2048;
    public const int MaxCascades = 4;
    public const float CascadeGrowth = 3f;

    private static readonly Extent2D Extent = new(Resolution, Resolution);

    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly PipelineLayout _layout;
    private readonly List<PipelineVariants> _pipelines = [];
    private readonly PipelineVariants _solid;

    public readonly RenderTarget Target;

    public ShadowPass(VulkanContext context, DescriptorSetLayout frameSetLayout, ResourceTable resources)
    {
        _context = context;
        _resources = resources;
        Target = new RenderTarget(
            context, GBuffer.DepthFormat, Extent, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit, MaxCascades);

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

        // Most materials have no holes, so nothing needs to run for their pixels.
        description.FragmentShader = null;
        description.TwoSided = false;
        var solidCulled = PipelineFactory.Create(context, description);
        description.TwoSided = true;
        _solid = new PipelineVariants(solidCulled, PipelineFactory.Create(context, description));
    }

    /// <summary>How many cascades a setting asks for: 1 to <see cref="MaxCascades"/>, with 0 meaning 1.</summary>
    public static int CascadeCount(in ShadowSettings shadows) => Math.Clamp(shadows.Cascades, 1, MaxCascades);

    /// <summary>The radius a cascade covers, growing from the settings' radius.</summary>
    public static float CascadeRadius(in ShadowSettings shadows, int cascade) =>
        shadows.Radius * MathF.Pow(CascadeGrowth, cascade);

    /// <summary>
    /// A reversed-Z parallel projection covering a sphere, matching the camera's
    /// conventions so the same pipelines' winding and depth test apply. The centre
    /// moves in whole texels as the camera does, so shadow edges don't shimmer.
    /// </summary>
    public static Matrix4x4 CreateMatrix(Vector3 lightDirection, Vector3 center, float radius)
    {
        var up = MathF.Abs(lightDirection.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

        // Snapping happens in the light's own x and y, which a rotation-only view gives.
        var rotation = Matrix4x4.CreateLookAt(lightDirection, Vector3.Zero, up);
        var lightSpace = Vector3.Transform(center, rotation);
        float texel = 2f * radius / Resolution;
        lightSpace.X = MathF.Round(lightSpace.X / texel) * texel;
        lightSpace.Y = MathF.Round(lightSpace.Y / texel) * texel;
        Matrix4x4.Invert(rotation, out var back);
        center = Vector3.Transform(lightSpace, back);

        var view = Matrix4x4.CreateLookAt(center + lightDirection * radius, center, up);
        var projection = new Matrix4x4(
            1f / radius, 0f, 0f, 0f,
            0f, -1f / radius, 0f, 0f,
            0f, 0f, 0.5f / radius, 0f,
            0f, 0f, 1f, 1f);
        return view * projection;
    }

    /// <summary>Draws one cascade into its layer of the map.</summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, List<Mesh?> meshes, int cascade)
    {
        var api = _context.Api;
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = Target.LayerViews[cascade],
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
        SceneDraws.Record(api, cmd, _layout, _pipelines, _resources, draws, meshes, solid: _solid, cascade: cascade);

        api.CmdEndRendering(cmd);
    }

    public void Dispose()
    {
        _context.Api.DestroyPipeline(_context.Device, _solid.TwoSided, null);
        _context.Api.DestroyPipeline(_context.Device, _solid.Culled, null);
        _context.Api.DestroyPipeline(_context.Device, _pipelines[0].TwoSided, null);
        _context.Api.DestroyPipeline(_context.Device, _pipelines[0].Culled, null);
        _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
        Target.Dispose();
    }
}
