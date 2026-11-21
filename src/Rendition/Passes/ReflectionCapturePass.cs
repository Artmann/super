using System.Numerics;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>The renderer's lists a capture draws from, and the pass that draws its sun's shadow map.</summary>
internal readonly record struct CaptureScene(List<Mesh?> Meshes, List<PartDetail> Details, ShadowPass Shadows);

/// <summary>Where the planned capture is taken from, and how bright the sky behind it is.</summary>
internal readonly record struct PlannedCapture(Vector3 Position, float EnvironmentIntensity);

/// <summary>
/// Draws the scene round one reflection probe into the six faces of a <see cref="ReflectionCaptureCube"/> in one
/// multiview pass, as <see cref="ProbeCapturePass"/> does for light probes, lit as reflection_capture.frag says. It is
/// all recorded into the frame, so a capture never waits for the GPU. The sun's shadow comes from a map of its own round
/// the probe's box. Like the light probes' capture it leaves out ghosts, skinned and swimming things and glass, draws
/// what sways at rest, and runs no surface shader: each material shows its own colour and glow.
/// See "Reflections" in docs/design.md.
/// </summary>
internal sealed unsafe class ReflectionCapturePass : IDisposable
{
    /// <summary>How far from a probe a draw can be and still be in its picture, in metres.</summary>
    public const float Reach = 100f;

    /// <summary>Texels across the sun shadow map round a capture.</summary>
    public const uint SunShadowResolution = 1024;

    /// <summary>How far round the middle of a probe's box its sun shadow map reaches, in the box's half-diagonals.</summary>
    public const float SunShadowReach = 2f;

    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly CaptureScene _scene;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _layout;
    private readonly List<PipelineVariants> _pipelines = [];
    private readonly CubeCapture _faces;
    private readonly List<int> _kept = [];
    private readonly ProbeSunShadow _sun;
    private PlannedCapture _planned;

    /// <summary>Makes the pass, its capture cube and its sun shadow map. See <see cref="ReflectionCaptureCube"/> for <paramref name="sky"/>.</summary>
    public ReflectionCapturePass(
        VulkanContext context, DescriptorSetLayout frameSetLayout, ResourceTable resources, CaptureScene scene, DescriptorImageInfo sky)
    {
        _context = context;
        _resources = resources;
        _scene = scene;
        Cube = new ReflectionCaptureCube(context, sky);
        var materials = resources.Materials;
        _sun = new ProbeSunShadow(context, material => materials[material].Flags, SunShadowResolution);

        _setLayout = context.CreateSetLayout(LitCaptureSet.Bindings);
        _pool = context.CreateDescriptorPool(1, new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 4));
        _set = context.AllocateSet(_pool, _setLayout);
        _layout = context.CreatePipelineLayout(
            [frameSetLayout, resources.Layout, _setLayout],
            new PushConstantRange(SceneDraws.PushStages, 0, (uint)(sizeof(SceneDraws.DrawConstants) + sizeof(ProbeCapturePass.ProbeConstants))));
        _faces = new CubeCapture(context, resources, _layout, _pipelines, new Extent2D(ProbeCubes.FaceSize, ProbeCubes.FaceSize));

        if (!context.Abilities.Multiview)
        {
            return;
        }

        var description = new PipelineDescription
        {
            VertexShader = "probe_capture.vert",
            FragmentShader = "reflection_capture.frag",
            Layout = _layout,
            ColorFormats = [GBuffer.HdrFormat],
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = SceneDraws.VertexBindings,
            VertexAttributes = ProbeCapturePass.CaptureAttributes,
            ViewMask = 0x3F,
        };
        var culled = PipelineFactory.Create(context, description);
        _pipelines.Add(new PipelineVariants(culled, PipelineFactory.Create(context, description with { TwoSided = true })));
    }

    /// <summary>Whether the GPU can draw a capture: it needs six views at once.</summary>
    public bool Supported => _pipelines.Count > 0;

    /// <summary>The cube the capture is drawn into, which the blur reads.</summary>
    public ReflectionCaptureCube Cube { get; }

    /// <summary>World to the sun shadow map round the planned capture, or zero without a sun.</summary>
    public Matrix4x4 SunMatrix => _sun.Matrix;

    /// <summary>
    /// Points the capture at the light probes' atlases and the environment's irradiance, for the soft light, and at its
    /// own sun shadow map through <paramref name="shadowSampler"/>. Call again whenever any is made anew.
    /// </summary>
    public void WriteSet(DescriptorImageInfo irradiance, DescriptorImageInfo depth, DescriptorImageInfo environment, Sampler shadowSampler)
    {
        var sunShadow = new DescriptorImageInfo(shadowSampler, _sun.View, ImageLayout.ShaderReadOnlyOptimal);
        LitCaptureSet.Write(_context, _set, new LitCaptureTextures(irradiance, depth, sunShadow, environment));
    }

    /// <summary>
    /// Plans a capture from <paramref name="probe"/>'s position under <paramref name="view"/>'s sky: picks the draws it
    /// sees, and the sun shadow map round its box for <paramref name="sun"/>, the direction towards the shadow-casting
    /// sun, or null for none. Returns how many instances <see cref="Record"/> will add, so the frame's instance buffer can
    /// be made big enough first.
    /// </summary>
    public int Plan(ReadOnlySpan<DrawItem> draws, in ReflectionProbe probe, in FrameView view, Vector3? sun, long frame)
    {
        _planned = new PlannedCapture(probe.Position, view.Environment.Intensity);
        _kept.Clear();
        _faces.Keep(draws, _scene.Details, probe.Position, Reach, _kept);

        var center = (probe.BoxMin + probe.BoxMax) * 0.5f;
        float radius = MathF.Max(Vector3.Distance(probe.BoxMin, probe.BoxMax) * 0.5f * SunShadowReach, 1f);
        _sun.Plan(sun, center, radius, frame);
        return _kept.Count + (_sun.Redraw ? draws.Length : 0);
    }

    /// <summary>
    /// Records the capture <see cref="Plan"/> planned: the sun's map, the six faces, then the sky and the mips. It leaves
    /// <see cref="ReflectionCaptureCube.CubeView"/> ready for compute shaders to sample.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, InstanceBuffer instances)
    {
        var api = _context.Api;
        _sun.Record(api, cmd, _scene.Shadows, frameSet, new SceneGeometry(draws, _scene.Meshes, _scene.Details, instances));
        Cube.RecordStart(cmd);

        _faces.Begin(cmd, Cube.Faces, Cube.Depth, Vector4.Zero);
        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set, _set };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 3, sets, 0, null);
        var probe = new ProbeCapturePass.ProbeConstants { Probe = new Vector4(_planned.Position, 1f) };
        api.CmdPushConstants(cmd, _layout, SceneDraws.PushStages, (uint)sizeof(SceneDraws.DrawConstants),
            (uint)sizeof(ProbeCapturePass.ProbeConstants), &probe);
        int[] kept = System.Buffers.ArrayPool<int>.Shared.Rent(Math.Max(_kept.Count, 1));
        _kept.CopyTo(kept);
        _faces.Draw(cmd, draws, kept.AsSpan(0, _kept.Count), _scene.Meshes, instances);
        System.Buffers.ArrayPool<int>.Shared.Return(kept);
        api.CmdEndRendering(cmd);

        Cube.RecordFinish(cmd, _planned.EnvironmentIntensity);
    }

    public void Dispose()
    {
        foreach (var pipelines in _pipelines)
        {
            pipelines.Culled.Destroy();
            pipelines.TwoSided.Destroy();
        }

        _context.DestroyLayouts(_layout, _pool, _setLayout);
        Cube.Dispose();
        _sun.Dispose();
    }
}
