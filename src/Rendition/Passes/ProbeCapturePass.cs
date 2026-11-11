using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Draws the scene round a few light probes a frame, each into the six faces of its own slot of
/// <see cref="ProbeVolume.FacesImage"/> in one multiview pass. Each surface is lit as
/// probe_light.glsl says, with the sun, the point and spot lights, the bounce the probes already
/// hold and its own glow, and the alpha keeps its distance from the probe: negative where the probe
/// saw the back of a one-sided surface. Everything is drawn two-sided, so a probe inside a wall can
/// tell. Things too far from a probe to matter are left out on the CPU, and so is anything that
/// moves on its own: ghosts, skinned and swimming things, and glass. Wind is drawn at rest.
/// See "Light probes" in docs/design.md.
/// </summary>
internal sealed unsafe class ProbeCapturePass : IDisposable
{
    /// <summary>How far, in the probe's cascade spacings, a draw can be and still be drawn round it.</summary>
    public const float ReachSpacings = 16f;

    private const uint AtlasSet = 2;
    private const uint IrradianceBinding = 11;
    private const uint DepthBinding = 12;
    private const uint SunShadowBinding = 14;

    // Only what probe_capture.vert reads: position, normal and UV. Handing it the scene's other
    // attributes would make the validation layers warn that they go unused.
    private static readonly VertexInputAttributeDescription[] CaptureAttributes =
    [
        SceneDraws.VertexAttributes[0],
        SceneDraws.VertexAttributes[1],
        SceneDraws.VertexAttributes[3],
    ];

    // Must match the push block in probe_capture.vert and probe_capture.frag.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeConstants
    {
        public Vector4 Probe;
    }

    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _layout;
    private readonly Pipeline _pipeline;
    private readonly List<PipelineVariants> _pipelines = [];
    private readonly List<DrawBatch> _batches = [];

    // The draws kept for each probe of the batch: _kept[_starts[p] .. _starts[p + 1]).
    private readonly List<int> _kept = [];
    private readonly List<int> _starts = [];
    private readonly List<Vector3> _positions = [];

    public ProbeCapturePass(VulkanContext context, DescriptorSetLayout frameSetLayout, ResourceTable resources)
    {
        _context = context;
        _resources = resources;
        var api = context.Api;

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(IrradianceBinding, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(DepthBinding, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(SunShadowBinding, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
        };
        var setLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = bindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &setLayoutInfo, null, out _setLayout).Check();

        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 3);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool).Check();

        var setLayout = _setLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        api.AllocateDescriptorSets(context.Device, &allocInfo, out _set).Check();

        var setLayouts = stackalloc DescriptorSetLayout[] { frameSetLayout, resources.Layout, _setLayout };
        var pushRange = new PushConstantRange(
            SceneDraws.PushStages, 0, (uint)(sizeof(SceneDraws.DrawConstants) + sizeof(ProbeConstants)));
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 3,
            PSetLayouts = setLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        api.CreatePipelineLayout(context.Device, &layoutInfo, null, out _layout).Check();

        if (!context.Multiview)
        {
            return;
        }

        var description = new PipelineDescription
        {
            VertexShader = "probe_capture.vert",
            FragmentShader = "probe_capture.frag",
            Layout = _layout,
            ColorFormats = [ProbeVolume.FacesFormat],
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = SceneDraws.VertexBindings,
            VertexAttributes = CaptureAttributes,
            TwoSided = true,
            ViewMask = 0x3F,
        };
        _pipeline = PipelineFactory.Create(context, description);
        _pipelines.Add(new PipelineVariants(_pipeline, _pipeline));
    }

    /// <summary>
    /// Points the capture at the probes' atlases, for the bounce, and at the probes' sun shadow map.
    /// Call again whenever either is made anew.
    /// </summary>
    public void WriteSet(DescriptorImageInfo irradiance, DescriptorImageInfo depth, DescriptorImageInfo sunShadow)
    {
        var infos = stackalloc DescriptorImageInfo[] { irradiance, depth, sunShadow };
        var targets = stackalloc uint[] { IrradianceBinding, DepthBinding, SunShadowBinding };
        var writes = stackalloc WriteDescriptorSet[3];
        for (int i = 0; i < 3; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = targets[i],
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &infos[i],
            };
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 3, writes, 0, null);
    }

    /// <summary>
    /// Whether a draw belongs in a probe's view at all: not a ghost, nothing skinned or swimming,
    /// nothing see-through, and something to draw.
    /// </summary>
    public static bool Captures(in DrawItem draw, MaterialFlags flags) =>
        draw.Ghost is null
        && draw.Bones.Count == 0
        && draw.Swim.Amplitude == 0f
        && draw.IndexCount != 0
        && (flags & MaterialFlags.Transparent) == 0;

    /// <summary>
    /// The most triangles one frame draws round its probes, counted once per probe rather than per
    /// face. Past it the rest of the batch waits for a later frame, so a dense scene updates fewer
    /// probes a frame instead of stalling the GPU: 32 probes round Courtyard's 830,000 triangles
    /// lost the device.
    /// </summary>
    public const long MaxTrianglesPerFrame = 8_000_000;

    /// <summary>
    /// Picks the draws each probe of this frame's batch will see, from <paramref name="positions"/>
    /// and each probe's cascade <paramref name="spacings"/>, taking the probes in order until
    /// <paramref name="maxTriangles"/> is reached. The first probe is always taken. Returns how many
    /// probes it took, and how many instances <see cref="Record"/> will add, so the frame's instance
    /// buffer can be made big enough first.
    /// </summary>
    public (int Probes, int Instances) Prepare(
        ReadOnlySpan<DrawItem> draws, List<PartDetail> details, ReadOnlySpan<Vector3> positions, ReadOnlySpan<float> spacings,
        long maxTriangles = MaxTrianglesPerFrame)
    {
        _kept.Clear();
        _starts.Clear();
        _positions.Clear();
        long triangles = 0;
        for (int p = 0; p < positions.Length; p++)
        {
            int start = _kept.Count;
            long probeTriangles = 0;
            float reach = spacings[p] * ReachSpacings;
            for (int i = 0; i < draws.Length; i++)
            {
                ref readonly var draw = ref draws[i];
                if (!Captures(draw, _resources.GetFlags(draw.Material)))
                {
                    continue;
                }

                if (DrawSelection.TryBounds(draw, details, out var center, out float radius)
                    && Vector3.Distance(center, positions[p]) - radius > reach)
                {
                    continue;
                }

                _kept.Add(i);
                probeTriangles += draw.IndexCount / 3;
            }

            if (p > 0 && triangles + probeTriangles > maxTriangles)
            {
                _kept.RemoveRange(start, _kept.Count - start);
                break;
            }

            triangles += probeTriangles;
            _starts.Add(start);
            _positions.Add(positions[p]);
        }

        _starts.Add(_kept.Count);
        return (_positions.Count, _kept.Count);
    }

    /// <summary>
    /// Draws each probe <see cref="Prepare"/> picked into the slot of the same index in
    /// <paramref name="volume"/>'s faces, and leaves the faces ready for <see cref="ProbeVolume.RecordFold"/>.
    /// </summary>
    public void Record(
        CommandBuffer cmd, ProbeVolume volume, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, List<Mesh?> meshes, InstanceBuffer instances)
    {
        int probes = _positions.Count;
        if (probes == 0)
        {
            return;
        }

        var api = _context.Api;
        uint layers = (uint)probes * 6;

        // The last fold may still be reading the faces.
        api.Transition(cmd, volume.FacesImage, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit, layerCount: layers);
        api.Transition(cmd, volume.FacesDepthImage, ImageAspectFlags.DepthBit,
            ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal,
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentWriteBit,
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit, layerCount: layers);

        var extent = new Extent2D(ProbeVolume.FaceSize, ProbeVolume.FaceSize);
        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set, _set };
        int[] order = System.Buffers.ArrayPool<int>.Shared.Rent(Math.Max(_kept.Count, 1));
        for (int p = 0; p < probes; p++)
        {
            var color = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = volume.SlotColorView(p),
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(new ClearColorValue(0f, 0f, 0f, ProbeVolume.SkyMark)),
            };
            var depth = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = volume.SlotDepthView(p),
                ImageLayout = ImageLayout.DepthAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.DontCare,
                ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0)), // Reversed-Z far plane.
            };
            var rendering = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(default, extent),
                LayerCount = 1,
                ViewMask = 0x3F,
                ColorAttachmentCount = 1,
                PColorAttachments = &color,
                PDepthAttachment = &depth,
            };

            api.CmdBeginRendering(cmd, &rendering);
            api.SetFullViewport(cmd, extent);
            api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 3, sets, 0, null);
            var probe = new ProbeConstants { Probe = new Vector4(_positions[p], 1f) };
            api.CmdPushConstants(cmd, _layout, SceneDraws.PushStages, (uint)sizeof(SceneDraws.DrawConstants), (uint)sizeof(ProbeConstants), &probe);

            int start = _starts[p];
            int count = _starts[p + 1] - start;
            var kept = order.AsSpan(0, count);
            for (int i = 0; i < count; i++)
            {
                kept[i] = _kept[start + i];
            }

            SceneDraws.Batch(draws, kept, keepOrder: false, _batches);
            var bound = new SceneDraws.Bound();
            foreach (var batch in _batches)
            {
                // No wind: a probe sees things at rest.
                var constants = SceneDraws.Constants(draws[kept[batch.First]], 0);
                SceneDraws.Draw(api, cmd, _layout, _pipelines, _resources, draws, kept.Slice(batch.First, batch.Count), meshes, instances, constants, null, ref bound);
            }

            api.CmdEndRendering(cmd);
        }

        System.Buffers.ArrayPool<int>.Shared.Return(order);

        api.Transition(cmd, volume.FacesImage, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit, layerCount: layers);
    }

    public void Dispose()
    {
        var api = _context.Api;
        if (_pipeline.Handle != 0)
        {
            api.DestroyPipeline(_context.Device, _pipeline, null);
        }

        api.DestroyPipelineLayout(_context.Device, _layout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }
}
