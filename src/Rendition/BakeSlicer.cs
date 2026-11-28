using System.Collections.Concurrent;
using System.Diagnostics;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// Steps the running background bake a slice a frame, inside the frame's command buffer, and hands it to the
/// <see cref="BakeFinisher"/> once every step is done. A step that throws ends the bake, which
/// <paramref name="failed"/> hears of. See "Background bakes" in docs/design.md.
/// </summary>
internal sealed class BakeSlicer(
    VulkanGpu gpu, FrameRing ring, RetiredResources retired, PreparedBakers bakers,
    Action<BakeSlicer.RunningBake, Exception> failed) : IDisposable
{
    /// <summary>Times the slices and sizes the next.</summary>
    public BakeSliceTimer Timer { get; } = new(gpu);

    /// <summary>Makes the finished bakes' files and frees the stopped jobs.</summary>
    public BakeFinisher Finisher { get; } = new();

    /// <summary>
    /// The running background bake, stepping or finishing, or null. Kept while its file is made on the thread pool, so
    /// no other bake sets up on the baker's pages before it lets them go. See "Lifecycle state in engine code" in
    /// docs/design.md.
    /// </summary>
    public RunningBake? Running { get; set; }

    /// <summary>The time the render thread spent in the bake's last step, in milliseconds, for tests.</summary>
    public float StepCpuMilliseconds { get; private set; }

    /// <summary>Starts stepping a job, from one pass a slice.</summary>
    public void Start(BakeJob job, BakeWant want, BakeQuality quality)
    {
        Running = new SteppingBake(job, want, quality, readyAt: 0);
        Timer.StartOver();
    }

    /// <summary>
    /// Records the running bake's next slice into the frame's command buffer, between timestamps, with as many trace
    /// passes as the last measured slice says fit the budget. Skips the frame while a step's readback is still on its
    /// way, and while the last bake still holds the baker's pages.
    /// </summary>
    public void RecordSlice(CommandBuffer cmd)
    {
        Timer.Untimed(ring.Index);
        if (Running is not SteppingBake { Job: var job } stepping || ring.FramesSubmitted < stepping.ReadyAt
            || (!job.Started && bakers.PagesClaimed))
        {
            return;
        }

        bool timed = Timer.CanTime;
        if (timed)
        {
            Timer.Begin(cmd, ring.Index);
        }

        BakeStepResult result;
        try
        {
            if (!TryStep(cmd, stepping, out result))
            {
                return;
            }
        }
        finally
        {
            if (timed)
            {
                Timer.End(cmd);
            }
        }

        AfterStep(stepping, result, timed);
    }

    /// <summary>
    /// Records the bake's next step. False when the step throws, after ending the bake: what the step recorded rides
    /// this frame, so the job goes with this frame's slot.
    /// </summary>
    private bool TryStep(CommandBuffer cmd, SteppingBake stepping, out BakeStepResult result)
    {
        long stepStarted = Stopwatch.GetTimestamp();
        try
        {
            result = stepping.Job.Step(cmd, Timer.SlicePasses);
            StepCpuMilliseconds = (float)Stopwatch.GetElapsedTime(stepStarted).TotalMilliseconds;
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            Finisher.Retire(stepping.Job, retired.AfterRecording);
            Running = null;
            failed(stepping, e);
            result = default;
            return false;
        }
    }

    /// <summary>
    /// Keeps what the slice ran for the timer, and moves the bake on: to wait for the step's readback, or to make its
    /// file once every step is done.
    /// </summary>
    private void AfterStep(SteppingBake stepping, BakeStepResult result, bool timed)
    {
        var job = stepping.Job;
        Timer.Keep(ring.Index, job.PassesInLastStep, timed);
        switch (result)
        {
            case BakeStepResult.AwaitGpu:
                // This frame is the next submitted; its fence has passed once as many more frames have been.
                Running = new SteppingBake(job, stepping.Want, stepping.RunQuality, ring.FramesSubmitted + FrameRing.Count);
                break;
            case BakeStepResult.Done:
                Running = Finisher.StartFinishing(stepping);
                break;
        }
    }

    /// <summary>
    /// Stops the running bake, if any, without finishing it, and says whether there was one. Steps already submitted may
    /// still run, so the job is freed with the last submitted frame. A bake whose file is being made is dropped, and its
    /// job freed when the thread pool is done with it.
    /// </summary>
    public bool Stop()
    {
        switch (Running)
        {
            case null:
                return false;
            case FinishingBake finishing:
                finishing.WasCancelled = true;
                break;
            default:
                Finisher.Retire(Running.Job, retired.AfterLastSubmitted);
                break;
        }

        Running = null;
        return true;
    }

    public void Dispose() => Timer.Dispose();

    /// <summary>The running background bake: the job the queue started, for which area, at which quality.</summary>
    public abstract class RunningBake(BakeJob job, BakeWant want, BakeQuality quality)
    {
        public readonly BakeJob Job = job;
        public readonly BakeWant Want = want;
        public readonly BakeQuality RunQuality = quality;
    }

    /// <summary>
    /// A background bake recording a slice a frame, from the frame after <paramref name="readyAt"/> frames have been
    /// submitted, when a step that copied results back has landed.
    /// </summary>
    public sealed class SteppingBake(BakeJob job, BakeWant want, BakeQuality quality, long readyAt)
        : RunningBake(job, want, quality)
    {
        public readonly long ReadyAt = readyAt;
    }

    /// <summary>A background bake whose steps are done and whose file is being made on the thread pool.</summary>
    public sealed class FinishingBake(BakeJob job, BakeWant want, BakeQuality quality) : RunningBake(job, want, quality)
    {
        public Task? FinishTask;
        public byte[]? FileBytes;
        public Exception? Failure;

        // Set on the render thread when the bake was stopped while finishing: its file is dropped.
        public bool WasCancelled;
    }

    /// <summary>
    /// Times the background bake's slices on the GPU, and sizes the next trace slice from them: as many passes as fit
    /// <see cref="AutoBake.BudgetMilliseconds"/> at the rate the last slice ran, never more than double. A step that
    /// traces nothing starts the count over, and a slice measured from before then says nothing about the passes now.
    /// </summary>
    public sealed class BakeSliceTimer(VulkanGpu gpu) : IDisposable
    {
        private readonly GpuTimer _timer = new(gpu, FrameRing.Count, ["Background bake"]);

        // Each frame slot's slice: whether it was timed, its passes and the run of measurements it belongs to.
        private readonly bool[] _timed = new bool[FrameRing.Count];
        private readonly int[] _passes = new int[FrameRing.Count];
        private readonly int[] _epochs = new int[FrameRing.Count];
        private int _epoch;

        /// <summary>How many passes the next trace slice records.</summary>
        public int SlicePasses { get; private set; } = 1;

        /// <summary>The GPU time of the last slice measured, in milliseconds.</summary>
        public float LastSliceMilliseconds { get; private set; }

        /// <summary>How many slices have been measured on the GPU, for tests.</summary>
        public int SlicesMeasured { get; private set; }

        /// <summary>Whether the GPU can time the slices at all.</summary>
        public bool CanTime => _timer.IsSupported;

        /// <summary>Starts the passes over from one, for a new bake or a stage whose passes may cost something else.</summary>
        public void StartOver()
        {
            SlicePasses = 1;
            _epoch++;
        }

        /// <summary>Marks a frame slot as recording no timed slice.</summary>
        public void Untimed(int slot) => _timed[slot] = false;

        public void Begin(CommandBuffer cmd, int slot) => _timer.Begin(cmd, slot);

        public void End(CommandBuffer cmd)
        {
            _timer.Mark(cmd);
            _timer.End(cmd);
        }

        /// <summary>Keeps what a frame slot's slice ran, for <see cref="Measure"/> once its fence has passed.</summary>
        public void Keep(int slot, int passes, bool timed)
        {
            if (passes == 0)
            {
                // The next stage's passes may cost something else, so they start over from one.
                StartOver();
            }

            _timed[slot] = timed;
            _passes[slot] = passes;
            _epochs[slot] = _epoch;
        }

        /// <summary>
        /// Reads the slice timed in this frame slot, whose fence has passed, and sizes the next trace slice from it
        /// within <paramref name="auto"/>'s budget.
        /// </summary>
        public void Measure(int slot, AutoBake? auto)
        {
            if (!_timed[slot])
            {
                return;
            }

            _timed[slot] = false;
            if (!_timer.Collect(slot))
            {
                return;
            }

            float milliseconds = (float)_timer.Times[0].Milliseconds;
            LastSliceMilliseconds = milliseconds;
            SlicesMeasured++;
            int passes = _passes[slot];
            if (passes == 0 || _epochs[slot] != _epoch || auto is null)
            {
                return;
            }

            double perPass = Math.Max(milliseconds / passes, 1e-4);
            SlicePasses = (int)Math.Clamp(auto.BudgetMilliseconds / perPass, 1, Math.Max(passes * 2, 1));
        }

        public void Dispose() => _timer.Dispose();
    }

    /// <summary>
    /// Makes finished background bakes' files on the thread pool, since encoding the pages takes longer than a frame,
    /// and frees stopped jobs once no frame in flight and no work on the thread pool reads them, so the render thread
    /// never waits for either.
    /// </summary>
    public sealed class BakeFinisher
    {
        // Finishes on the thread pool, until the render thread takes their results; and those results.
        private readonly List<FinishingBake> _finishing = [];
        private readonly ConcurrentQueue<FinishingBake> _finished = new();

        // Stopped jobs whose steps a frame in flight may still run, freed with the frame slot they were queued against.
        private readonly HashSet<BakeJob> _retiring = [];

        // Stopped jobs the GPU is done with whose work on the thread pool still runs, freed by a later frame once it is
        // done.
        private readonly List<BakeJob> _busyOnCpu = [];

        /// <summary>Makes the stepped job's file on the thread pool, and returns the bake that waits for it.</summary>
        public FinishingBake StartFinishing(SteppingBake stepping)
        {
            var finishing = new FinishingBake(stepping.Job, stepping.Want, stepping.RunQuality);
            _finishing.Add(finishing);
            finishing.FinishTask = Task.Run(() =>
            {
                try
                {
                    finishing.FileBytes = finishing.Job.Finish();
                }
                catch (Exception e)
                {
                    finishing.Failure = e;
                }

                _finished.Enqueue(finishing);
            });
            return finishing;
        }

        /// <summary>
        /// Takes a bake whose file the thread pool has finished, and frees its job: every step ran before it was done,
        /// so the GPU is finished with it.
        /// </summary>
        public bool TryTakeFinished(out FinishingBake finished)
        {
            if (!_finished.TryDequeue(out finished!))
            {
                return false;
            }

            _finishing.Remove(finished);
            finished.Job.Dispose();
            return true;
        }

        /// <summary>
        /// Frees a stopped job once the frame slot it was queued against is done. A job that never stepped holds
        /// nothing the GPU reads, so it goes at once.
        /// </summary>
        public void Retire(BakeJob job, RetiredSlot slot)
        {
            if (!job.Started)
            {
                job.Dispose();
                return;
            }

            _retiring.Add(job);
            slot.FreedOthers.Add(new RetiredBake(this, job));
        }

        /// <summary>Frees the stopped jobs whose work on the thread pool has ended.</summary>
        public void FreeDoneOnCpu()
        {
            for (int i = _busyOnCpu.Count - 1; i >= 0; i--)
            {
                var job = _busyOnCpu[i];
                if (job.CpuBusy)
                {
                    continue;
                }

                _busyOnCpu.RemoveAt(i);
                _retiring.Remove(job);
                job.Dispose();
            }
        }

        /// <summary>
        /// Frees every job once the GPU is idle: waits for the files being made, drops them, and frees the stopped
        /// jobs.
        /// </summary>
        public void DropAll()
        {
            foreach (var finishing in _finishing)
            {
                finishing.WasCancelled = true;
                finishing.FinishTask?.Wait();
            }

            while (_finished.TryDequeue(out var finished))
            {
                finished.Job.Dispose();
            }

            _finishing.Clear();
            foreach (var job in _retiring)
            {
                // Disposing twice is harmless, so the slot's later release changes nothing. A job whose work on the
                // thread pool still runs is waited for here, where waiting is allowed.
                job.Dispose();
            }

            _retiring.Clear();
            _busyOnCpu.Clear();
        }

        /// <summary>
        /// A stopped job, freed once the frame slot it was queued against is done, or, while its work on the thread
        /// pool still runs, by the first frame after that work ends.
        /// </summary>
        private sealed class RetiredBake(BakeFinisher finisher, BakeJob job) : IDisposable
        {
            public void Dispose()
            {
                if (job.CpuBusy)
                {
                    finisher._busyOnCpu.Add(job);
                    return;
                }

                finisher._retiring.Remove(job);
                job.Dispose();
            }
        }
    }
}
