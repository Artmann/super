using System.Numerics;
using Rendition.Passes;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// The live light probes: their atlases, which hold nothing until the first frame with the probes on, the pass that
/// draws a few probes' faces each frame with the sun's shadow map they use, and, where the GPU can trace, the pass that
/// updates them with rays instead. Plans each frame's batch and records its update. See "Light probes" in
/// docs/design.md.
/// </summary>
internal sealed class LightProbes(VulkanContext context, EnvironmentMap environment, DescriptorKit kit, LightProbes.Sources sources)
    : IDisposable
{
    private readonly ProbeCapturePass _updatePass = new(context, kit.FrameSetLayout, sources.Resources);
    private readonly ProbeTracePass? _tracePass = sources.Tracing is not null && sources.Volume.Supported
        ? new ProbeTracePass(context, FrameRing.Count, kit.FrameSetLayout, sources.Resources, ProbeBudget.For(ProbeQuality.High).Traced)
        : null;
    private ProbeSunShadow? _sun;

    /// <summary>The atlases the probes are kept in.</summary>
    public ProbeVolume Volume => sources.Volume;

    /// <summary>The cascades, the scheduler and this frame's batch.</summary>
    public ProbeBatch Batch { get; } = new();

    /// <summary>
    /// How many light probes follow the camera, and how many are updated a frame. A change makes the atlases again on
    /// the next frame with the probes on.
    /// </summary>
    public ProbeQuality Quality { get; set; } = ProbeQuality.High;

    /// <summary>Whether surfaces read the light probes this frame: they're on, the GPU can draw them, and the view has any.</summary>
    public bool InUse { get; set; }

    /// <summary>
    /// Latched: the probes have settled at least once since their storage was made. Reflection captures hash this rather
    /// than the live <see cref="Renderer.ProbesSettled"/>, which flips on every cell the camera crosses.
    /// </summary>
    public bool SettledOnce { get; set; }

    /// <summary>How many light probes the last frame updated: none while they're off, or settled and unchanged.</summary>
    public int UpdatesLastFrame { get; set; }

    /// <summary>For tests: while true, no probe is updated, so what a test writes into the atlases stays.</summary>
    public bool UpdatesPaused { get; set; }

    /// <summary>For tests: the most triangles a frame draws round its probes, as <see cref="ProbeCapturePass.MaxTrianglesPerFrame"/>.</summary>
    public long TrianglesPerFrame { get; set; } = ProbeCapturePass.MaxTrianglesPerFrame;

    /// <summary>Whether this frame traces its batch of probes, which needs the frame's top-level structure.</summary>
    public bool TracesBatch => _tracePass is not null && Batch.Ids.Count > 0;

    /// <summary>The probes' own sun shadow map's matrix, or zero when there is none.</summary>
    public Matrix4x4 SunMatrix => _sun is not null ? _sun.Matrix : default;

    /// <summary>
    /// Frees the atlases and stops planning, so every probe is uncaptured. Waits for the GPU when it frees them. Returns
    /// whether it did, so the sets that read the atlases are pointed at the stand-ins.
    /// </summary>
    public bool Release()
    {
        bool released = Volume.Release();
        Batch.Stop();
        SettledOnce = false;
        UpdatesLastFrame = 0;
        return released;
    }

    /// <summary>
    /// Makes the atlases for <see cref="Quality"/> if they aren't already, and with them new cascades and a new
    /// scheduler, so every probe starts uncaptured. Waits for the GPU when it makes them, and returns whether it did, so
    /// the sets that read the atlases are pointed at them.
    /// </summary>
    public bool Ensure()
    {
        if (!Volume.Supported || !Volume.EnsureStorage(Quality))
        {
            return false;
        }

        Batch.Restart(Quality);
        SettledOnce = false;
        var materials = sources.Resources.Materials;
        _sun ??= new ProbeSunShadow(context, material => materials[material].Flags);
        var irradiance = new DescriptorImageInfo(kit.LinearSampler, Volume.IrradianceView, ImageLayout.General);
        var depth = new DescriptorImageInfo(kit.LinearSampler, Volume.DepthView, ImageLayout.General);
        _updatePass.WriteSet(irradiance, depth,
            new DescriptorImageInfo(kit.ShadowSampler, _sun.View, ImageLayout.ShaderReadOnlyOptimal));
        _tracePass?.WriteSet(irradiance, depth, Volume.Atlases.Staging.Handle,
            new DescriptorImageInfo(environment.Sampler, environment.Sky, ImageLayout.ShaderReadOnlyOptimal));
        return true;
    }

    /// <summary>
    /// Plans this frame's light probe work, before its uniforms are written: moves the cascades with the camera, tells
    /// the scheduler what changed, picks the batch of probes to update and where they are, and plans the probes' sun
    /// shadow map. Returns how many instances the work will draw.
    /// </summary>
    public int Plan(in FrameView view, ReadOnlySpan<DrawItem> draws, ReadOnlySpan<LocalLight> lights, in ProbePlanInputs inputs)
    {
        if (Batch.Scheduler is null)
        {
            InUse = false;
            return 0;
        }

        InUse = true;
        Batch.Follow(view, inputs.LightingKey, lights);

        // Rays need no faces to draw into, so the traced path takes a bigger batch, as many as there are staging slots.
        // Once the near probes are settled, the traced path refreshes only a quarter of its budget a frame.
        bool traced = _tracePass is not null;
        if (!UpdatesPaused)
        {
            var budget = ProbeBudget.For(Quality);
            int count = traced
                ? Math.Min(budget.Traced, Volume.Atlases.StagingSlots)
                : Math.Min(budget.Raster, Math.Min(Volume.Faces?.Slots ?? 0, Volume.Atlases.StagingSlots));
            Batch.Take(view, count, traced ? budget.TracedRefresh : count);
        }

        if (Batch.Ids.Count == 0)
        {
            return 0;
        }

        // Traced probes find the sun with shadow rays, through the frame's top-level structure, and draw nothing.
        return traced ? 0 : PlanCaptures(view, draws, inputs);
    }

    /// <summary>
    /// Plans the probes' sun shadow map and which draws each probe's faces take, within the frame's triangle budget.
    /// Returns how many instances the captures will draw.
    /// </summary>
    private int PlanCaptures(in FrameView view, ReadOnlySpan<DrawItem> draws, in ProbePlanInputs inputs)
    {
        // The probes' own sun shadow map reaches across the coarsest cascade, which holds every probe.
        var coarsest = Batch.Cascades.Layout(ProbeCascades.Count - 1);
        float reach = 0.5f * coarsest.Spacing * new Vector3(coarsest.CountX, coarsest.CountY, coarsest.CountZ).Length();
        _sun!.Plan(inputs.ShadowSun, view.CameraPosition, reach, inputs.FrameNumber);

        var (taken, captured) = _updatePass.Prepare(draws, sources.Scene.Details, Batch.Positions, Batch.Spacings, TrianglesPerFrame);

        // Probes past the frame's triangle budget stay as they are in the scheduler, so a later frame picks them.
        Batch.KeepFirst(taken);
        return captured + (_sun.Redraw ? draws.Length : 0);
    }

    /// <summary>
    /// Records this frame's light probe work, after the lights' shadow tiles: clears the cells the cascades left, then
    /// draws the batch's probes, folds their faces and blends them into the atlases. Where the GPU can trace, the batch's
    /// probes are traced through <paramref name="traced"/> instead.
    /// </summary>
    public void Record(
        CommandBuffer cmd, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, InstanceBuffer instances, TopLevelStructure? traced)
    {
        // How far a probe already captured moves towards a new rasterized capture: halfway, so its light settles over
        // a few updates instead of flickering. A first capture is taken whole.
        const float rasterBlend = 0.5f;

        Batch.RecordInvalidations(cmd, Volume);
        var batch = Batch.Ids;
        UpdatesLastFrame = batch.Count;
        if (batch.Count == 0 || Batch.Scheduler is not { } scheduler)
        {
            return;
        }

        if (_tracePass is { } tracePass && traced is { } scene)
        {
            RecordTraced(cmd, frameSet, tracePass, scene);
        }
        else
        {
            var capture = sources.Scene;
            var geometry = new SceneGeometry(draws, capture.Meshes, capture.Details, instances);
            _sun!.Record(context.Api, cmd, capture.Shadows, frameSet, geometry);
            _updatePass.Record(cmd, Volume, frameSet, geometry);
            var spacings = Batch.Spacings;
            for (int i = 0; i < batch.Count; i++)
            {
                Volume.RecordFold(cmd, i, spacings[i], Batch.EnvironmentIntensity);
            }

            Volume.RecordBlend(cmd, batch, rasterBlend);
        }

        scheduler.Updated(batch);
        SettledOnce |= scheduler.Settled;
    }

    // Traces the batch's probes, which see only 128 rays each, so an update moves a quarter of the way and the noise of
    // a few updates averages out, unless its tile's mean light changed by TracedSnap or more: then it is taken whole,
    // and a smaller change moves in proportion, never less than the quarter. A first capture is still taken whole.
    // Lower than 0.05 and a dim probe flickers more.
    private void RecordTraced(CommandBuffer cmd, DescriptorSet frameSet, ProbeTracePass tracePass, TopLevelStructure scene)
    {
        const float tracedBlend = 0.25f;
        const float tracedSnap = 0.05f;
        Volume.BeginTraces(cmd);
        sources.Tracing!.ReadyToTrace(cmd);
        var probes = new TracedProbes(Batch.Ids, Batch.Positions, Batch.Spacings)
        {
            Seed = (uint)Batch.Scheduler!.Frame,
            EnvironmentIntensity = Batch.EnvironmentIntensity,
        };
        tracePass.Record(cmd, scene, probes, (sources.Ring.Index, frameSet));
        Volume.RecordBlend(cmd, Batch.Ids, tracedBlend, tracedSnap);
    }

    public void Dispose()
    {
        Volume.Dispose();
        _updatePass.Dispose();
        _tracePass?.Dispose();
        _sun?.Dispose();
    }

    /// <summary>
    /// What the probes draw and trace through: the material table, the ray tracing layer or null, the scene a capture
    /// draws, the frames in flight, and the atlases.
    /// </summary>
    public sealed record Sources(ResourceTable Resources, RayTracing? Tracing, CaptureScene Scene, FrameRing Ring, ProbeVolume Volume);
}

/// <summary>
/// What a frame's light probe plan takes besides the view: <see cref="RendererReflections.ProbeLightingKey"/>, the direction
/// towards the sun that casts the frame's shadow, and the frame's number.
/// </summary>
internal readonly record struct ProbePlanInputs(int LightingKey, Vector3? ShadowSun, long FrameNumber);
