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

    // Where probes.glsl finds the light probes' atlases, and where the capture finds its sun shadow map, in set 2.
    internal const uint IrradianceBinding = 11;
    internal const uint DepthBinding = 12;
    internal const uint SunShadowBinding = 14;

    /// <summary>The bindings of set 2 that both this pass and <see cref="ReflectionCapturePass"/> read.</summary>
    internal static readonly DescriptorSetLayoutBinding[] AtlasBindings =
    [
        new(IrradianceBinding, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
        new(DepthBinding, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
        new(SunShadowBinding, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
    ];

    // Only what probe_capture.vert reads: position, normal and UV. Handing it the scene's other
    // attributes would make the validation layers warn that they go unused.
    internal static readonly VertexInputAttributeDescription[] CaptureAttributes =
    [
        SceneDraws.VertexAttributes[0],
        SceneDraws.VertexAttributes[1],
        SceneDraws.VertexAttributes[3],
    ];

    // Must match the push block in probe_capture.vert, probe_capture.frag and reflection_capture.frag.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProbeConstants
    {
        public Vector4 Probe;
    }

    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _layout;
    private readonly LivePipeline? _pipeline;
    private readonly CubeCapture _faces;

    // The draws kept for each probe of the batch: _kept[_starts[p] .. _starts[p + 1]).
    private readonly List<int> _kept = [];
    private readonly List<int> _starts = [];
    private readonly List<Vector3> _positions = [];

    public ProbeCapturePass(VulkanContext context, DescriptorSetLayout frameSetLayout, ResourceTable resources)
    {
        _context = context;
        _resources = resources;
        var api = context.Api;

        _setLayout = context.CreateSetLayout(AtlasBindings);
        _pool = context.CreateDescriptorPool(1, new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 3));
        _set = context.AllocateSet(_pool, _setLayout);
        _layout = context.CreatePipelineLayout(
            [frameSetLayout, resources.Layout, _setLayout],
            new PushConstantRange(SceneDraws.PushStages, 0, (uint)(sizeof(SceneDraws.DrawConstants) + sizeof(ProbeConstants))));
        List<PipelineVariants> pipelines = [];
        _faces = new CubeCapture(context, resources, _layout, pipelines, new Extent2D(ProbeVolume.FaceSize, ProbeVolume.FaceSize));

        if (!context.Abilities.Multiview)
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
        pipelines.Add(new PipelineVariants(_pipeline, _pipeline));
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
            writes[i] = Descriptors.ImageWrite(_set, targets[i], DescriptorType.CombinedImageSampler, &infos[i]);
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
            long probeTriangles = _faces.Keep(draws, details, positions[p], spacings[p] * ReachSpacings, _kept);
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
        ReadyFaces(cmd, volume, layers);

        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set, _set };
        int[] order = System.Buffers.ArrayPool<int>.Shared.Rent(Math.Max(_kept.Count, 1));
        for (int p = 0; p < probes; p++)
        {
            _faces.Begin(cmd, volume.SlotColorView(p), volume.SlotDepthView(p), new Vector4(0f, 0f, 0f, ProbeVolume.SkyMark));
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

            _faces.Draw(cmd, draws, kept, meshes, instances);
            api.CmdEndRendering(cmd);
        }

        System.Buffers.ArrayPool<int>.Shared.Return(order);

        api.Transition(cmd, volume.FacesImage, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit, layerCount: layers);
    }

    // Makes the first `layers` faces ready to draw into, colour and depth. The last fold may still be reading them.
    private void ReadyFaces(CommandBuffer cmd, ProbeVolume volume, uint layers)
    {
        var api = _context.Api;
        api.Transition(cmd, volume.FacesImage, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit, layerCount: layers);
        api.Transition(cmd, volume.FacesDepthImage, ImageAspectFlags.DepthBit,
            ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal,
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentWriteBit,
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit, layerCount: layers);
    }

    public void Dispose()
    {
        var api = _context.Api;
        _pipeline?.Destroy();

        api.DestroyPipelineLayout(_context.Device, _layout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }
}
