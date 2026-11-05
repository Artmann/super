using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Renders the scene's depth as seen from the shadow-casting light, once per cascade.
/// Each cascade covers three times the radius of the one before, in its own layer, and
/// is fitted to the next slice of what the camera sees.
/// </summary>
internal sealed unsafe class ShadowPass : IDisposable
{
    public const int Resolution = 2048;
    public const int MaxCascades = 4;
    public const float CascadeGrowth = 3f;

    /// <summary>The share of a map, at its edges, over which a cascade hands over to the next.</summary>
    public const float HandOver = 0.1f;

    /// <summary>The share of the furthest reach over which shadows fade out.</summary>
    public const float FadeShare = 0.15f;

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
            new(2, 0, Format.R8G8B8A8Uint, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Joints))),
            new(3, 0, Format.R8G8B8A8Unorm, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Weights))),
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
    /// How wide a perspective view opens: the distance from its axis to a corner of the
    /// screen, one unit in front of the camera. Null for a parallel (orthographic) view,
    /// whose cascades stay centred on the settings' centre.
    /// </summary>
    public static float? Spread(in Matrix4x4 projection)
    {
        if (projection.M34 == 0f || projection.M11 == 0f || projection.M22 == 0f)
        {
            return null;
        }

        // An off-centre projection reaches further on one side, so take that side.
        float x = (1f + MathF.Abs(projection.M31)) / MathF.Abs(projection.M11);
        float y = (1f + MathF.Abs(projection.M32)) / MathF.Abs(projection.M22);
        return MathF.Sqrt(x * x + y * y);
    }

    /// <summary>
    /// Fits a cascade's ball to the part of a perspective view from <paramref name="start"/>
    /// onwards, measured along the view from the camera. The ball keeps the radius it's given,
    /// so its sharpness never changes as the camera turns. The answer is where its centre sits
    /// along the view and how far the view reaches inside it, which is where the next cascade
    /// starts. A slice of the view whose corners are already wider than the ball gets none of it.
    /// </summary>
    public static (float Center, float End) FitSlice(float start, float radius, float spread)
    {
        float spread2 = spread * spread;
        if (start * spread >= radius)
        {
            return (start, start);
        }

        // The ball round the slice only grows as the slice gets deeper, and a slice deeper
        // than the ball's width can't fit, so halving finds the end to well under a millimetre.
        float low = start;
        float high = start + 2f * radius;
        for (int i = 0; i < 40; i++)
        {
            float middle = 0.5f * (low + high);
            if (SliceRadius(start, middle, spread2) <= radius)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return (SliceCenter(start, low, spread2), low);
    }

    /// <summary>
    /// The centre of the smallest ball round the slice of a view between two depths, along the
    /// view. It sits where the near and far corners are equally far, or at the far end when the
    /// far corners alone are wider than the near ones are from there.
    /// </summary>
    private static float SliceCenter(float near, float far, float spread2) =>
        MathF.Min(0.5f * (near + far) * (1f + spread2), far);

    private static float SliceRadius(float near, float far, float spread2)
    {
        float center = SliceCenter(near, far, spread2);
        float toNear = (center - near) * (center - near) + near * near * spread2;
        float toFar = (far - center) * (far - center) + far * far * spread2;
        return MathF.Sqrt(MathF.Max(toNear, toFar));
    }

    /// <summary>
    /// A reversed-Z parallel projection covering a sphere, matching the camera's
    /// conventions so the same pipelines' winding and depth test apply. The centre
    /// moves in whole texels as the camera does, so shadow edges don't shimmer.
    /// Depth runs from <paramref name="reach"/> towards the light, or the radius if that's
    /// further, to the radius away from it, so casters outside the sphere on the light's
    /// side still land in the map.
    /// </summary>
    public static Matrix4x4 CreateMatrix(Vector3 lightDirection, Vector3 center, float radius, float reach = 0f)
    {
        reach = MathF.Max(reach, radius);
        var up = MathF.Abs(lightDirection.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

        // Snapping happens in the light's own x and y, which a rotation-only view gives.
        var rotation = Matrix4x4.CreateLookAt(lightDirection, Vector3.Zero, up);
        var lightSpace = Vector3.Transform(center, rotation);
        float texel = 2f * radius / Resolution;
        lightSpace.X = MathF.Round(lightSpace.X / texel) * texel;
        lightSpace.Y = MathF.Round(lightSpace.Y / texel) * texel;
        Matrix4x4.Invert(rotation, out var back);
        center = Vector3.Transform(lightSpace, back);

        var view = Matrix4x4.CreateLookAt(center + lightDirection * reach, center, up);
        var projection = new Matrix4x4(
            1f / radius, 0f, 0f, 0f,
            0f, -1f / radius, 0f, 0f,
            0f, 0f, 1f / (reach + radius), 0f,
            0f, 0f, 1f, 1f);
        return view * projection;
    }

    /// <summary>
    /// How many units of depth one unit across the map covers, for a cascade made by
    /// <see cref="CreateMatrix"/>: 1 when it reaches its radius each way, less when it reaches further.
    /// </summary>
    public static float DepthScale(float radius, float reach) => 2f * radius / (MathF.Max(reach, radius) + radius);

    /// <summary>Draws one cascade into its layer of the map.</summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, List<Mesh?> meshes, InstanceBuffer instances, int cascade)
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
        SceneDraws.Record(api, cmd, _layout, _pipelines, _resources, draws, meshes, instances, ScenePass.Shadow, solid: _solid, cascade: cascade);

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
