using System.Numerics;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// What the renderer bakes by itself, and the queue that picks what is next: each frame tells the queue which areas it
/// names whose bake is missing or stale, makes the job the queue starts, and hands finished files to
/// <see cref="Rendition.AutoBake.Done"/>. See "Background bakes" in docs/design.md.
/// </summary>
internal sealed class BackgroundBakes : IDisposable
{
    private readonly bool _canTrace;
    private readonly BakeJobs _jobs;
    private readonly BakeWants _wants;

    // Areas whose bake failed, each reported once.
    private readonly HashSet<string> _failuresLogged = new(StringComparer.Ordinal);
    private AutoBake? _auto;
    private BakeQueue? _queue;

    // Set when preparing the baker refused for the AutoBake set, so frames bake nothing until another is set.
    private bool _unavailable;

    public BackgroundBakes(VulkanContext context, FrameRing ring, RetiredResources retired, BakeJobs jobs, BakeWants wants)
    {
        _canTrace = context.CanTrace;
        _jobs = jobs;
        _wants = wants;
        Slicer = new BakeSlicer(context, ring, retired, jobs.Bakers,
            (bake, error) => Failed(bake.Want, bake.RunQuality, error));
    }

    /// <summary>Steps the running bake a slice a frame.</summary>
    public BakeSlicer Slicer { get; }

    /// <summary>
    /// What is baked in the background, or null for nothing. Setting it stops the running bake and forgets what was
    /// baked, and prepares baking, which waits for the GPU the first time. Where the GPU can trace rays but can't bake,
    /// that is reported once and nothing is baked.
    /// </summary>
    public AutoBake? AutoBake
    {
        get => _auto;
        set
        {
            Stop();
            _auto = value;
            _queue = value is null ? null : new BakeQueue(value.Qualities);
            _unavailable = false;
            if (value is null || !_canTrace)
            {
                return;
            }

            try
            {
                _jobs.Bakers.Prepare(probes: value.Spaces);
            }
            catch (InvalidOperationException e)
            {
                _unavailable = true;
                Console.Error.WriteLine($"Nothing is baked in the background: {e.Message}");
            }
        }
    }

    /// <summary>How the background bake is getting on, or null when none runs and no area waits for one.</summary>
    public BakeProgress? Progress => Slicer.Running is { Job: var job } running
        ? new BakeProgress(job.Info.Name,running is BakeSlicer.FinishingBake ? 1f : job.Progress, _queue?.Waiting ?? 0,
            Slicer.Timer.LastSliceMilliseconds)
        : _queue is { Waiting: > 0 } queue
            ? new BakeProgress("", 0f, queue.Waiting, Slicer.Timer.LastSliceMilliseconds)
            : null;

    /// <summary>Hands the files finished on the thread pool to <see cref="Rendition.AutoBake.Done"/>, on the render thread.</summary>
    public void TakeFinished()
    {
        Slicer.Finisher.FreeDoneOnCpu();
        while (Slicer.Finisher.TryTakeFinished(out var finished))
        {
            if (finished.WasCancelled)
            {
                continue;
            }

            Slicer.Running = null;
            var want = finished.Want;
            if (finished.Failure is { } error)
            {
                Failed(want, finished.RunQuality, error);
                continue;
            }

            _queue?.Finished(want.File, want.Inputs, finished.RunQuality);
            _auto?.Done(new BakeDone(want.File, want.Name, finished.Job.Info.Inputs,finished.RunQuality, finished.FileBytes!));
        }
    }

    /// <summary>
    /// Tells the queue which areas this frame names whose bake is missing or stale, from <paramref name="last"/>, stops
    /// the running bake when its area is named with other inputs, and makes the job the queue starts. Runs once a
    /// frame, from the frame as drawn with every light.
    /// </summary>
    public void Update(in FrameView view, ReadOnlySpan<DrawItem> draws, BakeFrame? last)
    {
        if (_auto is not { } auto || _queue is not { } queue || _unavailable || !_canTrace)
        {
            return;
        }

        bool cancel = _wants.Collect(view, auto, queue, last);
        var start = queue.Update(_wants.Collected, view.CameraPosition, out bool cancelled, out var quality);
        if (cancel || cancelled)
        {
            // The queue already forgot it, and may have started the next bake, so it isn't told again.
            Stop(tellQueue: false);
        }

        if (start is { } want)
        {
            StartJob(view, draws, want, quality);
        }
    }

    /// <summary>Makes the job for the bake the queue started, or reports why it can't be made.</summary>
    private void StartJob(in FrameView view, ReadOnlySpan<DrawItem> draws, BakeWant want, BakeQuality quality)
    {
        // The queue starts a bake only when none runs; a job left here would never be freed.
        if (Slicer.Running is not null)
        {
            Stop(tellQueue: false);
        }

        try
        {
            var job = want.Chunk < 0
                ? _jobs.CreateSpaceJob(view, draws, new BakeSettings(view.BakedSpace!.Value.TexelsPerMetre, quality))
                : _jobs.CreateChunkJob(view, draws, want.Chunk, new BakeSettings(view.BakedChunks[want.Chunk].TexelsPerMetre, quality));
            Slicer.Start(job, want, quality);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // Never started again with these inputs, so a bake that can't be made doesn't loop.
            Failed(want, quality, e);
        }
    }

    // A bake that failed is never started again with these inputs, and is reported once.
    private void Failed(in BakeWant want, BakeQuality quality, Exception error)
    {
        _queue?.Finished(want.File, want.Inputs, quality);
        if (_failuresLogged.Add(want.File))
        {
            Console.Error.WriteLine($"The background bake of \"{want.Name}\" failed, so its area stays lit live: {error.Message}");
        }
    }

    /// <summary>
    /// Stops the running background bake, if any, without finishing it; the queue starts its area again while a frame
    /// names it. Pass <paramref name="tellQueue"/> false when the queue itself called the bake off, since it may already
    /// have started the next one, which telling it would end too.
    /// </summary>
    public void Stop(bool tellQueue = true)
    {
        if (Slicer.Stop() && tellQueue)
        {
            _queue?.Stopped();
        }
    }

    /// <summary>
    /// Stops the running bake when <paramref name="reads"/> says it reads something about to be freed or rebuilt: a
    /// mesh, a texture, or a material whose structures go. Its held top level still points at them, so it would trace
    /// freed memory. The queue starts the area again from the scene. A bake whose steps are all done, and whose file is
    /// being made, reads nothing on the GPU any more, so it is left to finish. Every path that destroys or rebuilds what
    /// a bake traces calls this before it lets go.
    /// </summary>
    public void StopIfReads(Func<BakeJob, bool> reads)
    {
        if (Slicer.Running is BakeSlicer.SteppingBake { Job: var job } && reads(job))
        {
            Stop();
        }
    }

    /// <summary>
    /// Frees every background bake once the GPU is idle, for a whole bake, which needs the baker's pages, and for
    /// disposing: waits for the files being made, drops them, and stops the running bake.
    /// </summary>
    public void DropAll()
    {
        Stop();
        Slicer.Finisher.DropAll();
    }

    public void Dispose() => Slicer.Dispose();

    /// <summary>
    /// Collects the areas a frame names whose bake is missing or stale, once their lights hold still, for the queue.
    /// </summary>
    public sealed class BakeWants(BakeResolver resolver, ReadableBakes readable, AnimatedLights lights)
    {
        // This frame's wants, grown when a frame names more areas.
        private BakeWant[] _wants = new BakeWant[16];
        private int _count;

        /// <summary>The wants <see cref="Collect"/> found.</summary>
        public ReadOnlySpan<BakeWant> Collected => _wants.AsSpan(0, _count);

        /// <summary>
        /// Finds the last frame's areas whose bake is missing or stale, once its lights hold still, and names each area
        /// to the queue. Returns whether the running bake is of a named area with other inputs, so it must stop.
        /// </summary>
        public bool Collect(in FrameView view, AutoBake auto, BakeQueue queue, BakeFrame? last)
        {
            _count = 0;
            if (last is not { } bakes || !lights.Settled)
            {
                return false;
            }

            bool cancel = false;
            if (auto.Spaces)
            {
                cancel |= WantSpace(view, queue, bakes);
            }

            cancel |= WantChunks(view, queue, bakes);
            return cancel;
        }

        /// <summary>
        /// Names the view's baked space to the queue, and wants its bake when it is missing or stale. Returns whether
        /// the running bake is of the space with other inputs, so it must stop.
        /// </summary>
        private bool WantSpace(in FrameView view, BakeQueue queue, BakeFrame bakes)
        {
            if (view.BakedSpace is not { } space || bakes.Space is not { } spaceInputs)
            {
                return false;
            }

            string file = resolver.SpaceFile(space.Name);
            bool cancel = queue.Named(file, spaceInputs);
            if (queue.Wants(readable.Inner, file, spaceInputs))
            {
                Add(new BakeWant(file, space.Name, spaceInputs, -1, (space.Min + space.Max) * 0.5f));
            }

            return cancel;
        }

        /// <summary>
        /// Names each of the view's chunks with static draws and settled inputs to the queue, and wants the bakes that
        /// are missing or stale. Returns whether the running bake is of a chunk with other inputs, so it must stop.
        /// </summary>
        private bool WantChunks(in FrameView view, BakeQueue queue, BakeFrame bakes)
        {
            bool cancel = false;
            var chunks = view.BakedChunks;
            for (int c = 0; c < chunks.Length && c < bakes.Chunks.Count; c++)
            {
                if (bakes.ChunkPending(c) || bakes.ChunkStaticDraws(c) == 0)
                {
                    continue;
                }

                var inputs = bakes.Chunks[c];
                string file = bakes.ChunkFile(c);
                cancel |= queue.Named(file, inputs);
                if (queue.Wants(readable.Inner, file, inputs))
                {
                    var chunk = chunks[c];
                    var centre = Vector3.Transform((chunk.Min + chunk.Max) * 0.5f, chunk.Transform);
                    Add(new BakeWant(file, chunk.Name, inputs, c, centre));
                }
            }

            return cancel;
        }

        private void Add(in BakeWant want)
        {
            if (_count == _wants.Length)
            {
                Array.Resize(ref _wants, _count * 2);
            }

            _wants[_count++] = want;
        }
    }
}
