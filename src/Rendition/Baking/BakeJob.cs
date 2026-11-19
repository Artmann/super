using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// A bake of a space or a chunk as a series of steps, each recording a slice of the work into a command buffer the
/// caller submits: texel setup, then for each page its paths a few passes at a time, the readback of their sums, the
/// filter and, for a chunk, both runs of its transfer dilation; then a space's probes, a batch of passes at a time,
/// and their folds. A step that copies results back returns <see cref="BakeStepResult.AwaitGpu"/>, and the step after
/// it reads them. The work on the CPU that touches every texel of a page, turning the readbacks into arrays and filling
/// the filter's uploads, runs on the thread pool: that step returns <see cref="BakeStepResult.AwaitCpu"/>, and the step
/// that finds it done records the GPU work after it. Each pass is seeded by its own number, so however the passes are
/// sliced the file is the same.
/// <see cref="RendererBaking.Bake"/> and <see cref="RendererBaking.BakeChunk"/> run one to the end; a background bake runs a few
/// steps a frame. The job owns its descriptor pool, its lights, its sums and readback buffers, and a held top-level
/// structure, and holds the baker's pages from its first step until it is disposed, so one job runs at a time. Dispose
/// it only once the GPU is done with its steps. Its parts: <see cref="BakeJobKit"/> holds what every step works with,
/// <see cref="BakePageSteps"/> and <see cref="BakeProbeSteps"/> run the pages and the probes, and
/// <see cref="BakeCpuWork"/> runs work on the thread pool. See "Baking as a job" in docs/design.md.
/// </summary>
internal sealed class BakeJob : IDisposable
{
    // What a step does in each stage, in the order of BakeStage.
    private static readonly Func<BakeJob, CommandBuffer, int, BakeStep>[] Steps =
    [
        (job, cmd, _) =>
        {
            // Builds the held top level and draws the texels into the baker's pages.
            job._kit.SetUp(cmd, job, job.Info.Name);
            return job._pages.Next();
        },
        (job, cmd, passes) => job._pages.Trace(cmd, passes),
        (job, _, _) => job._pages.ReadSums(),
        (job, _, _) => job._pages.ReadFilter(),
        (job, _, _) => job._pages.ReadTransfer(),
        (job, cmd, passes) => job._probes.Trace(cmd, passes),
        (job, cmd, _) => job._probes.Fold(cmd),
        (job, _, _) => job._probes.ReadFold(),
        (job, cmd, _) => job._cpu.After(cmd),
        (_, _, _) => new BakeStep(BakeStepResult.Done, BakeStage.Done),
    ];

    private readonly BakeJobKit _kit;
    private readonly BakePageSteps _pages;
    private readonly BakeProbeSteps _probes;
    private readonly BakeCpuWork _cpu = new();
    private readonly Func<BakeResult, LightBakeFile> _compose;
    private BakeStage _stage = BakeStage.Setup;
    private bool _disposed;

    /// <summary>
    /// Makes a job of <paramref name="area"/> on <paramref name="baker"/>, lit by <paramref name="sky"/>. Records nothing
    /// and never waits: the first <see cref="Step"/> sets up the texels. <paramref name="compose"/> makes the file from
    /// what the job made, at <see cref="Finish"/>. A space's probes fold through the baker's probe volume.
    /// </summary>
    internal BakeJob(
        Baker baker, BakeJobInfo info, AreaJob area, (ImageView View, Sampler Sampler) sky, Func<BakeResult, LightBakeFile> compose)
    {
        var volume = area.Probes is null ? null : baker.Probes.Volume;
        if (area.Probes is not null && volume is null)
        {
            throw new ArgumentException("A bake with probes needs the baker's probe volume to fold them.", nameof(baker));
        }

        Info = info;
        _compose = compose;
        _kit = new BakeJobKit(baker, area, info.Settings, sky);
        _probes = new BakeProbeSteps(_kit, volume);
        _pages = new BakePageSteps(_kit, _cpu, _probes.Count > 0);
        _kit.Allocate();
    }

    /// <summary>The file the job makes, the area's name, the inputs and the settings it bakes with.</summary>
    public BakeJobInfo Info { get; }

    /// <summary>The area the job bakes: its draws, lights and layout.</summary>
    internal AreaJob Area => _kit.Area;

    /// <summary>
    /// How much of the bake is done, from 0 to 1, by the texel and probe paths traced. It never goes down, and only
    /// reaches 1 once <see cref="Step"/> has returned <see cref="BakeStepResult.Done"/>.
    /// </summary>
    public float Progress => _stage == BakeStage.Done
        ? 1f
        : _kit.TotalWork <= 0 ? 0f : (float)Math.Min(_kit.DoneWork / _kit.TotalWork, 0.99);

    /// <summary>The passes the last step traced, texel or probe, or 0 when it traced none.</summary>
    public int PassesInLastStep => _kit.PassesInLastStep;

    /// <summary>
    /// Whether the next step reads what the last one copied back, so the GPU must be done with the last step first.
    /// It must name every stage that reads mapped memory the GPU wrote, and each such stage must follow a step that
    /// returned <see cref="BakeStepResult.AwaitGpu"/>; a stage left out here could read a readback before it lands.
    /// </summary>
    internal bool ReadsBack =>
        _stage is BakeStage.ReadSums or BakeStage.ReadFilter or BakeStage.ReadTransfer or BakeStage.ReadFold;

    /// <summary>Whether the first step has run, so the job holds the baker's pages.</summary>
    internal bool Started => _kit.Claimed;

    /// <summary>
    /// Whether work the job started on the thread pool is still running. It reads and writes the job's mapped buffers,
    /// so <see cref="Dispose"/> waits for it; dispose a busy job only where waiting is allowed.
    /// </summary>
    internal bool CpuBusy => _cpu.Busy;

    /// <summary>
    /// Waits for the work the job started on the thread pool, after <see cref="BakeStepResult.AwaitCpu"/>. Its error,
    /// if any, comes out of the next <see cref="Step"/>.
    /// </summary>
    internal void WaitForCpu() => _cpu.Wait();

    /// <summary>
    /// Records the next slice of the bake into <paramref name="cmd"/>, tracing at most <paramref name="passes"/> passes.
    /// Never waits. The caller submits the command buffer; after <see cref="BakeStepResult.AwaitGpu"/> it waits for that
    /// submit to finish before the next step.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another bake holds the baker's pages.</exception>
    public BakeStepResult Step(CommandBuffer cmd, int passes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(passes, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _kit.PassesInLastStep = 0;
        var step = Steps[(int)_stage](this, cmd, passes);
        _stage = step.Next;
        return step.Result;
    }

    /// <summary>
    /// The <c>.lightbake</c> file's bytes, once <see cref="Step"/> has returned <see cref="BakeStepResult.Done"/>.
    /// Encodes every page on the calling thread, so call it off the render thread. Needs no GPU.
    /// </summary>
    /// <exception cref="InvalidOperationException">The job isn't done.</exception>
    public byte[] Finish()
    {
        if (_stage != BakeStage.Done)
        {
            throw new InvalidOperationException(
                $"The bake of \"{Info.Name}\" isn't done, so it has no file yet; step it until Step returns Done.");
        }

        var area = _kit.Area;
        int side = area.Layout.PageSide;
        var results = _pages.Results;
        var (irradiance, direction, mask) = results.Encode(side);
        var transfer = area.IsChunk ? results.EncodeTransfer(side) : null;
        LightBakePage? probeIrradiance = null;
        LightBakePage? probeDepth = null;
        if (area.Probes is not null)
        {
            (probeIrradiance, probeDepth) = _probes.Pages();
        }

        var result = new BakeResult(
            area.PlainLight ? irradiance : null, area.PlainLight ? direction : null, mask, transfer,
            results.InvalidShare(), _kit.Passes, probeIrradiance, probeDepth);
        return _compose(result).ToBytes();
    }

    /// <summary>
    /// Frees what the job holds and gives the baker's pages back. Call it only once the GPU is done with every step
    /// submitted; what <see cref="Finish"/> needs stays readable. Waits for work still running on the thread pool, which
    /// reads the buffers freed here, so the renderer disposes a job only once <see cref="CpuBusy"/> is false.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cpu.Wait();
        _cpu.Forget();
        _pages.Free();
        _probes.Free();
        _kit.Free(this);
    }
}
