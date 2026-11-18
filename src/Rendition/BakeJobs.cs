using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// Makes the jobs that bake a frame's baked space or one of its chunks, and runs a job to the end for a bake that
/// waits. A job is made without waiting for the GPU: the baker comes from <see cref="PreparedBakers"/>. See "Baking
/// as a job" in docs/design.md.
/// </summary>
internal sealed class BakeJobs(
    VulkanContext context, PreparedBakers bakers, MeshStore meshes, ResourceTable resources, AreaInputs inputs)
{
    // How long one submit of the path tracer may run, below Windows' two-second GPU timeout with room to spare.
    private static readonly TimeSpan BatchBudget = TimeSpan.FromMilliseconds(80);

    /// <summary>The baker the jobs run on.</summary>
    public PreparedBakers Bakers => bakers;

    /// <summary>
    /// Checks a bake of the view's space before it starts: the space, its probes, the static draws and the chunks.
    /// </summary>
    public static CheckedSpace StartSpace(in FrameView view, ReadOnlySpan<DrawItem> draws, BakeSettings settings)
    {
        var space = BakeChecks.Space(view, settings);
        BakeIds.CheckStaticDraws(draws);
        BakeFiles.CheckChunks(view.BakedChunks);
        return space;
    }

    /// <summary>
    /// The bake of the view's space as a job to step, without waiting for the GPU: it records nothing until its first
    /// step. The draws and lights are copied, so the job doesn't need the frame's spans.
    /// </summary>
    public BakeJob CreateSpaceJob(in FrameView view, ReadOnlySpan<DrawItem> draws, BakeSettings settings) =>
        SpaceJob(view, draws, settings, StartSpace(view, draws, settings));

    /// <summary>
    /// Checks a bake of one of the view's chunks before it starts, and finds its inputs and which draws are its.
    /// </summary>
    public ResolvedChunk StartChunk(in FrameView view, ReadOnlySpan<DrawItem> draws, int chunk, BakeSettings settings)
    {
        BakeChecks.Chunk(view, chunk, settings);
        long started = Stopwatch.GetTimestamp();
        BakeIds.CheckStaticDraws(draws);
        BakeFiles.CheckChunks(view.BakedChunks);
        return ResolveChunk(view, draws, chunk, started);
    }

    /// <summary>The bake of one of the view's chunks as a job to step, as <see cref="CreateSpaceJob"/> makes a space's.</summary>
    public BakeJob CreateChunkJob(in FrameView view, ReadOnlySpan<DrawItem> draws, int chunk, BakeSettings settings) =>
        ChunkJob(view, draws, settings, StartChunk(view, draws, chunk, settings));

    /// <summary>
    /// Steps a job to the end, a submit at a time, with as many passes in each as fit the batch budget, then makes its
    /// file. Waits for the GPU.
    /// </summary>
    public byte[] RunToEnd(BakeJob job)
    {
        int batch = 1;
        while (true)
        {
            var result = BakeStepResult.Continue;
            long stepStarted = Stopwatch.GetTimestamp();
            context.SubmitAndWait(cmd => result = job.Step(cmd, batch));
            if (result == BakeStepResult.Done)
            {
                return job.Finish();
            }

            if (result == BakeStepResult.AwaitCpu)
            {
                // A whole bake may wait, so the next step finds the work done rather than submitting empty steps.
                job.WaitForCpu();
            }

            batch = NextBatch(job.PassesInLastStep, stepStarted);
        }
    }

    // As many passes as fit the budget at the rate the last step ran, never more than double at once. A step that
    // traced nothing starts the next stage's passes over from one, since its passes may cost something else.
    private static int NextBatch(int count, long stepStarted)
    {
        if (count == 0)
        {
            return 1;
        }

        double perPass = Math.Max(Stopwatch.GetElapsedTime(stepStarted).TotalMilliseconds / count, 1e-3);
        return Math.Clamp((int)(BatchBudget.TotalMilliseconds / perPass), 1, Math.Max(count * 2, 1));
    }

    /// <summary>Lays out the static draws the space owns on its lightmap pages, and throws when they don't fit.</summary>
    private LightmapLayout LayOutSpace(
        ReadOnlySpan<DrawItem> draws, BakedSpace space, ReadOnlySpan<BakedChunk> chunks, BakeSettings settings)
    {
        var owned = new List<DrawItem>();
        foreach (ref readonly var draw in draws)
        {
            if (draw.BakeId != 0
                && BakeAreas.OwnerOfDraw(BakeAreas.Transform(meshes.PartBounds(draw.Detail), draw.Transform), space, chunks) == BakeAreas.Space)
            {
                owned.Add(draw);
            }
        }

        var layout = LightmapLayout.Build(CollectionsMarshal.AsSpan(owned), meshes.TriangleArea, settings, GetsLightmap);
        if (layout.Left.Count > 0)
        {
            throw new InvalidOperationException(
                $"{layout.Left.Count.ToString(CultureInfo.InvariantCulture)} of the {owned.Count.ToString(CultureInfo.InvariantCulture)} static draws in the baked space \"{space.Name}\" don't fit in {LightmapLayout.MaxPages.ToString(CultureInfo.InvariantCulture)} lightmap pages of {LightmapLayout.PageSize.ToString(CultureInfo.InvariantCulture)} texels, so they would get no baked light. Use fewer than {settings.TexelsPerMetre.ToString(CultureInfo.InvariantCulture)} texels a metre, or bake a smaller space.");
        }

        return layout;
    }

    /// <summary>
    /// Resolves the space's inputs, lays out its draws and takes its lights, and makes the job that bakes them. Never
    /// waits: the baker and its probe volume come from <see cref="PreparedBakers.Prepare"/>.
    /// </summary>
    public BakeJob SpaceJob(in FrameView view, ReadOnlySpan<DrawItem> draws, BakeSettings settings, CheckedSpace checkedSpace)
    {
        var (space, probeSettings, started) = checkedSpace;

        // A resolver of its own, so the frame's resolver keeps what the last frame drew. It still tells the tracker
        // where each light is, so a light that moved between frames is known to be moving.
        var areaInputs = inputs.ResolveAreas(new BakeResolver(NoBakes.Instance), view, draws).Space!.Value;

        var layout = LayOutSpace(draws, space, view.BakedChunks, settings);
        var lights = BakeLights.TakeSpace(view, space, inputs.Lights);
        var unassigned = new List<int>();
        var channels = BakeLights.AssignSpaceChannels(view, space, lights, unassigned);
        var bakeLights = CollectionsMarshal.AsSpan(lights.Lights);
        var baker = bakers.Prepared(space.Name, probes: true);
        var area = new AreaJob(draws.ToArray(), bakeLights.ToArray(), false, layout, view.EnvironmentIntensity, true,
            probeSettings);
        var skipped = unassigned.ToArray();
        var quality = settings.Quality;
        var environment = bakers.Environment;
        return new BakeJob(baker, new BakeJobInfo(BakeFiles.Space(space.Name), space.Name, areaInputs, settings), area,
            (environment.Sky, environment.Sampler),
            result =>
            {
                var report = new LightBakeReport(skipped, result.InvalidShare, result.Passes, Stopwatch.GetElapsedTime(started));
                return new LightBakeFile(areaInputs, quality, layout.Draws, result.Irradiance, result.Direction, result.Mask,
                    result.ProbeIrradiance, result.ProbeDepth, probeSettings, channels, report);
            });
    }

    // A chunk's inputs, and which draws are its, with the same resolver the space's bake uses, so the chunk takes exactly
    // the draws its inputs name.
    private ResolvedChunk ResolveChunk(in FrameView view, ReadOnlySpan<DrawItem> draws, int chunk, long started)
    {
        var resolver = new BakeResolver(NoBakes.Instance);
        var chunkInputs = inputs.ResolveAreas(resolver, view, draws).ChunkBakes.InputsOf(chunk);
        var ids = new HashSet<ulong>(resolver.ChunkIds(chunk));
        if (ids.Count == 0)
        {
            throw new InvalidOperationException(
                $"The baked chunk \"{view.BakedChunks[chunk].Name}\" owns no static draws, so there is nothing to bake. A static draw belongs to the chunk whose box holds its centre, unless the baked space's box touches it.");
        }

        return new ResolvedChunk(chunk, chunkInputs, resolver, ids, started);
    }

    /// <summary>
    /// The chunk's draws, in its own space, and whether any of them glows. The rest of the world neither blocks nor
    /// bounces its light. Only this copy's: every copy of a chunk has the same ids, so an id alone would take the other
    /// copies' draws too.
    /// </summary>
    private List<DrawItem> MoveChunkDraws(ReadOnlySpan<DrawItem> draws, ResolvedChunk resolved, in BakedChunk baked, out bool glows)
    {
        var owners = resolved.Resolver.Owners;
        var owned = new List<DrawItem>();
        glows = false;
        for (int d = 0; d < draws.Length; d++)
        {
            ref readonly var draw = ref draws[d];
            if (d < owners.Length && owners[d] == resolved.Chunk && draw.BakeId != 0 && resolved.Ids.Contains(draw.BakeId))
            {
                var moved = draw;
                moved.Transform = BakeAreas.Relative(draw.Transform, baked.Transform);
                owned.Add(moved);
                glows |= resources.Materials[draw.Material].Glows;
            }
        }

        return owned;
    }

    /// <summary>
    /// Moves the chunk's draws and lights into its space, lays them out on its page, and makes the job that bakes them.
    /// Never waits: the baker comes from <see cref="PreparedBakers.Prepare"/>.
    /// </summary>
    public BakeJob ChunkJob(in FrameView view, ReadOnlySpan<DrawItem> draws, BakeSettings settings, ResolvedChunk resolved)
    {
        var baked = view.BakedChunks[resolved.Chunk];
        var chunkInputs = resolved.Inputs;
        var owned = MoveChunkDraws(draws, resolved, baked, out bool glows);
        var layout = LightmapLayout.BuildSingle(CollectionsMarshal.AsSpan(owned), meshes.TriangleArea, baked.TexelsPerMetre, GetsLightmap);
        if (layout.Left.Count > 0)
        {
            throw new InvalidOperationException(
                $"{layout.Left.Count.ToString(CultureInfo.InvariantCulture)} of the {owned.Count.ToString(CultureInfo.InvariantCulture)} static draws in the baked chunk \"{baked.Name}\" don't fit in one lightmap page of {LightmapLayout.PageSize.ToString(CultureInfo.InvariantCulture)} texels, so they would get no baked light. Use fewer than {baked.TexelsPerMetre.ToString(CultureInfo.InvariantCulture)} texels a metre, or make the chunk smaller.");
        }

        var lights = BakeLights.TakeChunk(view, resolved.Chunk, inputs.Lights);
        var unassigned = new List<int>();
        var (bakeLights, chunkChannels) = BakeLights.AssignChunkChannels(view.LocalLights, baked, lights, unassigned);

        // No sky and no sun in the plain light: the transfer carries them, for whatever sky the chunk is placed under.
        bool plainLight = lights.Lights.Count > 0 || glows;
        var baker = bakers.Prepared(baked.Name, probes: false);
        var area = new AreaJob(owned.ToArray(), bakeLights, true, layout, 0f, plainLight, null);
        var skipped = unassigned.ToArray();
        var quality = settings.Quality;
        long started = resolved.Started;
        var environment = bakers.Environment;
        var info = new BakeJobInfo(BakeFiles.Chunk(baked.Name, chunkInputs.Combined), baked.Name, chunkInputs, settings);
        return new BakeJob(baker, info, area, (environment.Sky, environment.Sampler),
            result =>
            {
                var report = new LightBakeReport(skipped, result.InvalidShare, result.Passes, Stopwatch.GetElapsedTime(started));
                return new LightBakeFile(chunkInputs, quality, layout.Draws, result.Irradiance, result.Direction, result.Mask,
                    null, null, null, [], report)
                {
                    Transfer = result.Transfer,
                    ChunkChannels = chunkChannels,
                };
            });
    }

    // Whether a material may have a lightmap: not one that sways in the wind or is see-through.
    private bool GetsLightmap(MaterialHandle material) =>
        resources.Materials[material].Wind == 0f && (resources.Materials[material].Flags & MaterialFlags.Transparent) == 0;

    /// <summary>A baked space that passed <see cref="BakeChecks.Space"/>, its probes' grid, and when its bake started.</summary>
    public readonly record struct CheckedSpace(BakedSpace Space, ProbeVolumeSettings Probes, long Started);

    /// <summary>
    /// A chunk of the frame, its inputs, the resolver that found them, the bake ids of its draws, and when its bake
    /// started.
    /// </summary>
    public sealed record ResolvedChunk(int Chunk, BakeInputs Inputs, BakeResolver Resolver, HashSet<ulong> Ids, long Started);

    /// <summary>The checks a bake of a frame's space or chunk makes before it starts.</summary>
    private static class BakeChecks
    {
        /// <summary>
        /// Checks the view's baked space: there is one, it is baked at its own texel density, and its probes fit in a
        /// bake.
        /// </summary>
        public static CheckedSpace Space(in FrameView view, BakeSettings settings)
        {
            if (view.BakedSpace is not { } space)
            {
                throw new InvalidOperationException("Bake needs a baked space in the frame; call view.Indirect.BakedLighting first.");
            }

            if (settings.TexelsPerMetre != space.TexelsPerMetre)
            {
                throw new ArgumentException(
                    $"The baked space \"{space.Name}\" is lit at {space.TexelsPerMetre.ToString(CultureInfo.InvariantCulture)} texels a metre, so it can't be baked at {settings.TexelsPerMetre.ToString(CultureInfo.InvariantCulture)}: no frame would find that bake fresh.",
                    nameof(settings));
            }

            // The space's probes, laid out as the live probes are, so the runtime copies their tiles straight in.
            var probeSettings = new ProbeVolumeSettings(space.Min, space.Max, space.ProbeSpacing);
            long probeCount = ProbeGrid.Total(probeSettings);
            if (probeCount > BakeFiles.MaxSpaceProbes)
            {
                throw new InvalidOperationException(
                    $"The baked space \"{space.Name}\" holds {probeCount.ToString(CultureInfo.InvariantCulture)} probes, but a bake holds at most {BakeFiles.MaxSpaceProbes.ToString(CultureInfo.InvariantCulture)}.");
            }

            return new CheckedSpace(space, probeSettings, Stopwatch.GetTimestamp());
        }

        /// <summary>Checks that the frame has the chunk, and that it is baked at its own texel density.</summary>
        public static void Chunk(in FrameView view, int chunk, BakeSettings settings)
        {
            var chunks = view.BakedChunks;
            if (chunk < 0 || chunk >= chunks.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(chunk), chunk, string.Create(CultureInfo.InvariantCulture,
                    $"The frame has {chunks.Length} baked {(chunks.Length == 1 ? "chunk" : "chunks")}, so chunk {chunk} doesn't exist."));
            }

            var baked = chunks[chunk];
            if (settings.TexelsPerMetre != baked.TexelsPerMetre)
            {
                throw new ArgumentException(
                    $"The baked chunk \"{baked.Name}\" is lit at {baked.TexelsPerMetre.ToString(CultureInfo.InvariantCulture)} texels a metre, so it can't be baked at {settings.TexelsPerMetre.ToString(CultureInfo.InvariantCulture)}: no frame would find that bake fresh.",
                    nameof(settings));
            }
        }
    }
}
