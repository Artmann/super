using System.Numerics;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>What a <see cref="BakeJob"/> step asks of whoever runs it.</summary>
internal enum BakeStepResult
{
    /// <summary>Submit the step's work and step again whenever suits.</summary>
    Continue,

    /// <summary>
    /// The step copied results into memory the next step reads, so wait until the GPU is done with this step's work
    /// before the next one.
    /// </summary>
    AwaitGpu,

    /// <summary>
    /// The step recorded nothing and started work on the thread pool; step again whenever suits, and the step that
    /// finds the work done records what follows it. A caller that may wait can call <see cref="BakeJob.WaitForCpu"/>.
    /// </summary>
    AwaitCpu,

    /// <summary>Nothing is left to record; <see cref="BakeJob.Finish"/> makes the file.</summary>
    Done,
}

/// <summary>
/// One area to bake, a space or a chunk, everything in the area's own space.
/// </summary>
/// <param name="Draws">Every draw that blocks and bounces light; the job builds its top level from them.</param>
/// <param name="Lights">The lights of the bake, each Mixed one with the mask channel it was assigned.</param>
/// <param name="IsChunk">Whether the paths also sum the sky and sun transfer, on a page of the layout's own side.</param>
/// <param name="Layout">Where each draw sits: from <see cref="LightmapLayout.Build"/> for a space, <see cref="LightmapLayout.BuildSingle"/> for a chunk.</param>
/// <param name="SkyIntensity">What the sky in the plain light is scaled by; 0 leaves it out.</param>
/// <param name="PlainLight">Whether the irradiance and direction pages are kept.</param>
/// <param name="Probes">The space's light probes, or null for a chunk, which has none.</param>
internal sealed record AreaJob(
    DrawItem[] Draws, GpuBakeLight[] Lights, bool IsChunk, LightmapLayout Layout, float SkyIntensity, bool PlainLight,
    ProbeVolumeSettings? Probes);

/// <summary>What a <see cref="BakeJob"/> made: the pages as the file stores them, and what the report needs.</summary>
internal sealed record BakeResult(
    List<LightBakePage>? Irradiance, List<LightBakePage>? Direction, List<LightBakePage> Mask,
    LightBakeTransfer? Transfer, float InvalidShare, int Passes, LightBakePage? ProbeIrradiance,
    LightBakePage? ProbeDepth);

/// <summary>
/// A bake of a space or a chunk as a series of steps, each recording a slice of the work into a command buffer the
/// caller submits: texel setup, then for each page its paths a few passes at a time, the readback of their sums, the
/// filter and, for a chunk, both runs of its transfer dilation; then a space's probes, a batch of passes at a time,
/// and their folds. A step that copies results back returns <see cref="BakeStepResult.AwaitGpu"/>, and the step after
/// it reads them. The work on the CPU that touches every texel of a page, turning the readbacks into arrays and filling
/// the filter's uploads, runs on the thread pool: that step returns <see cref="BakeStepResult.AwaitCpu"/>, and the step
/// that finds it done records the GPU work after it. Each pass is seeded by its own number, so however the passes are
/// sliced the file is the same.
/// <see cref="Renderer.Bake"/> and <see cref="Renderer.BakeChunk"/> run one to the end; a background bake runs a few
/// steps a frame. The job owns its descriptor pool, its lights, its sums and readback buffers, and a held top-level
/// structure, and holds the baker's pages from its first step until it is disposed, so one job runs at a time. Dispose
/// it only once the GPU is done with its steps. See "Baking as a job" in docs/design.md.
/// </summary>
internal sealed unsafe class BakeJob : IDisposable
{
    private enum Stage
    {
        Setup,
        Trace,
        ReadSums,
        ReadFilter,
        ReadTransfer,
        ProbeTrace,
        Fold,
        ReadFold,
        Cpu,
        Done,
    }

    private readonly Baker _baker;
    private readonly VulkanContext _context;
    private readonly AreaJob _area;
    private readonly LightmapLayout _pages;
    private readonly bool _filter;
    private readonly ImageView _sky;
    private readonly Sampler _skySampler;
    private readonly ProbeVolume? _volume;
    private readonly Func<BakeResult, LightBakeFile> _compose;
    private readonly DescriptorPool _pool;
    private readonly GpuBuffer _lights;
    private readonly int _passes;
    private readonly int _probePasses;
    private readonly int _probeCount;
    private readonly double _totalWork;

    private Stage _stage = Stage.Setup;
    private double _doneWork;
    private bool _disposed;

    // Work running on the thread pool, and what the step that finds it done records after it.
    private Task? _cpu;
    private Func<CommandBuffer, BakeStepResult>? _afterCpu;

    // Texel setup.
    private TopLevelStructure _scene;
    private IDisposable? _sceneRelease;
    private bool _claimed;

    // The page being baked.
    private int _page;
    private (int X, int Y, int Width, int Height) _region;
    private RenderTarget[]? _sums;
    private DescriptorSet _traceSet;
    private int _pass;
    private GpuBuffer? _sumsBuffer;
    private TracedPage? _raw;
    private Baker.FilterRun? _run;
    private float[]? _transfer;
    private bool[]? _transferFilled;
    private int _transferRun;

    // What each page left.
    private readonly List<TracedPage> _filtered = [];
    private TracedPage? _transferPage;

    // The probes.
    private GpuBuffer? _positions;
    private RenderTarget? _probeSum;
    private RenderTarget? _probeFaces;
    private GpuBuffer? _foldBuffer;
    private DescriptorSet _probeSet;
    private int _firstProbe;
    private int _probePass;
    private int _row;
    private int _inGroup;
    private byte[]? _probeIrradiance;
    private byte[]? _probeDepth;

    /// <summary>
    /// Makes a job of <paramref name="area"/>. Records nothing and never waits: the first <see cref="Step"/> sets up
    /// the texels. <paramref name="compose"/> makes the file from what the job made, at <see cref="Finish"/>.
    /// </summary>
    internal BakeJob(
        Baker baker, VulkanContext context, string file, string name, BakeInputs inputs, BakeSettings settings,
        AreaJob area, ImageView sky, Sampler skySampler, ProbeVolume? volume, Func<BakeResult, LightBakeFile> compose)
    {
        if (area.Probes is not null && volume is null)
        {
            throw new ArgumentException("A bake with probes needs the baker's probe volume to fold them.", nameof(volume));
        }

        _baker = baker;
        _context = context;
        File = file;
        Name = name;
        Inputs = inputs;
        Quality = settings.Quality;
        _area = area;
        _pages = area.IsChunk ? area.Layout.OnFullPage() : area.Layout;
        _filter = settings.Filter;
        _sky = sky;
        _skySampler = skySampler;
        _volume = volume;
        _compose = compose;
        _passes = settings.Quality == BakeQuality.Preview ? 64 : 1024;

        // A probe face texel sees one direction, not a cosine-weighted hemisphere, and the fold averages a thousand of
        // them into each tile texel, so a sixteenth of the passes gives as quiet a result.
        _probePasses = Math.Max(1, _passes / 16);
        _probeCount = area.Probes is { } probes ? (int)ProbeGrid.Total(probes) : 0;
        if (_probeCount > ProbeGrid.MaxProbes)
        {
            throw new ArgumentException(
                $"The probes number {_probeCount}, more than the {ProbeGrid.MaxProbes} the atlases hold.", nameof(area));
        }

        // The work is the texel paths and probe paths to trace; the rest is quick beside them.
        for (int page = 0; page < _pages.Pages; page++)
        {
            var (_, _, width, height) = Baker.UsedRegion(_pages, page);
            _totalWork += (double)width * height * _passes;
        }

        var probeExtent = Baker.ProbeExtent;
        int probeBatches = (_probeCount + Baker.ProbesPerBatch - 1) / Baker.ProbesPerBatch;
        _totalWork += (double)probeExtent.Width * probeExtent.Height * _probePasses * probeBatches;

        // An empty storage buffer isn't allowed, so there is always room for one light.
        var lights = area.Lights;
        ulong lightBytes = (ulong)Math.Max(lights.Length, 1) * (ulong)sizeof(GpuBakeLight);
        _lights = GpuBuffer.CreateHostVisible(_context, lightBytes, BufferUsageFlags.StorageBufferBit);
        lights.CopyTo(new Span<GpuBakeLight>(_lights.Mapped, lights.Length));
        try
        {
            _pool = baker.CreateJobPool(_pages.Pages);
        }
        catch
        {
            _lights.Dispose();
            throw;
        }
    }

    /// <summary>Where the file goes: <see cref="BakeFiles.Space"/> of the name, or the chunk's own file.</summary>
    public string File { get; }

    /// <summary>The baked space's or chunk's name.</summary>
    public string Name { get; }

    /// <summary>The inputs the bake was made from, so a frame drawn like that one finds it fresh.</summary>
    public BakeInputs Inputs { get; }

    public BakeQuality Quality { get; }

    /// <summary>
    /// How much of the bake is done, from 0 to 1, by the texel and probe paths traced. It never goes down, and only
    /// reaches 1 once <see cref="Step"/> has returned <see cref="BakeStepResult.Done"/>.
    /// </summary>
    public float Progress => _stage == Stage.Done
        ? 1f
        : _totalWork <= 0 ? 0f : (float)Math.Min(_doneWork / _totalWork, 0.99);

    /// <summary>The passes the last step traced, texel or probe, or 0 when it traced none.</summary>
    public int PassesInLastStep { get; private set; }

    /// <summary>
    /// Whether the next step reads what the last one copied back, so the GPU must be done with the last step first.
    /// It must name every stage that reads mapped memory the GPU wrote, and each such stage must follow a step that
    /// returned <see cref="BakeStepResult.AwaitGpu"/>; a stage left out here could read a readback before it lands.
    /// </summary>
    internal bool ReadsBack => _stage is Stage.ReadSums or Stage.ReadFilter or Stage.ReadTransfer or Stage.ReadFold;

    /// <summary>Whether the first step has run, so the job holds the baker's pages.</summary>
    internal bool Started => _claimed;

    /// <summary>
    /// Whether work the job started on the thread pool is still running. It reads and writes the job's mapped buffers,
    /// so <see cref="Dispose"/> waits for it; dispose a busy job only where waiting is allowed.
    /// </summary>
    internal bool CpuBusy => _cpu is { IsCompleted: false };

    /// <summary>
    /// Waits for the work the job started on the thread pool, after <see cref="BakeStepResult.AwaitCpu"/>. Its error,
    /// if any, comes out of the next <see cref="Step"/>.
    /// </summary>
    internal void WaitForCpu()
    {
        if (_cpu is not { } task)
        {
            return;
        }

        try
        {
            task.Wait();
        }
        catch (AggregateException)
        {
            // Thrown again from the next step, on the thread that runs it.
        }
    }

    /// <summary>Whether one of the job's draws uses <paramref name="mesh"/>, so the job can't go on once it is destroyed.</summary>
    internal bool Uses(MeshHandle mesh)
    {
        foreach (ref readonly var draw in _area.Draws.AsSpan())
        {
            if (draw.Mesh == mesh)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The draws the job traces, so the renderer can tell whether a texture it destroys is read by them.</summary>
    internal ReadOnlySpan<DrawItem> Draws => _area.Draws.AsSpan();

    /// <summary>Whether one of the job's draws uses <paramref name="material"/>.</summary>
    internal bool Uses(MaterialHandle material)
    {
        foreach (ref readonly var draw in _area.Draws.AsSpan())
        {
            if (draw.Material == material)
            {
                return true;
            }
        }

        return false;
    }

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
        PassesInLastStep = 0;
        return _stage switch
        {
            Stage.Setup => SetUp(cmd),
            Stage.Trace => Trace(cmd, passes),
            Stage.ReadSums => ReadSums(),
            Stage.ReadFilter => ReadFilter(),
            Stage.ReadTransfer => ReadTransfer(),
            Stage.ProbeTrace => TraceProbes(cmd, passes),
            Stage.Fold => Fold(cmd),
            Stage.ReadFold => ReadFold(),
            Stage.Cpu => AfterCpu(cmd),
            _ => BakeStepResult.Done,
        };
    }

    /// <summary>
    /// The <c>.lightbake</c> file's bytes, once <see cref="Step"/> has returned <see cref="BakeStepResult.Done"/>.
    /// Encodes every page on the calling thread, so call it off the render thread. Needs no GPU.
    /// </summary>
    /// <exception cref="InvalidOperationException">The job isn't done.</exception>
    public byte[] Finish()
    {
        if (_stage != Stage.Done)
        {
            throw new InvalidOperationException(
                $"The bake of \"{Name}\" isn't done, so it has no file yet; step it until Step returns Done.");
        }

        int side = _area.Layout.PageSide;
        var irradiance = new List<LightBakePage>();
        var direction = new List<LightBakePage>();
        var mask = new List<LightBakePage>();
        long covered = 0;
        long invalid = 0;
        foreach (var page in _filtered)
        {
            var encoded = BakeEncoding.Encode(page);
            irradiance.Add(BakeEncoding.Crop(encoded.Irradiance, side));
            direction.Add(BakeEncoding.Crop(encoded.Direction, side));
            mask.Add(BakeEncoding.Crop(encoded.Mask, side));
            for (int t = 0; t < page.Covered.Length; t++)
            {
                if (page.Covered[t])
                {
                    covered++;
                    if (!page.Valid[t])
                    {
                        invalid++;
                    }
                }
            }
        }

        // A chunk with nothing that can hold a lightmap still has its transfer, holding none.
        LightBakeTransfer? transfer = null;
        if (_area.IsChunk)
        {
            var page = _transferPage ?? new TracedPage(0, 0, 0, 0, 0, [], [], [], [], [], []) { Transfer = [] };
            transfer = BakeEncoding.EncodeTransfer(page, side);
        }

        LightBakePage? probeIrradiance = null;
        LightBakePage? probeDepth = null;
        if (_area.Probes is not null)
        {
            const int irradianceSide = BakedProbeAtlas.IrradianceAtlasSize;
            const int depthSide = BakedProbeAtlas.DepthAtlasSize;
            probeIrradiance = new LightBakePage(irradianceSide, irradianceSide, LightBakePageFormat.Rgba16f, ProbeIrradiance);
            probeDepth = new LightBakePage(depthSide, depthSide, LightBakePageFormat.Rgba16f, ProbeDepth);
        }

        var result = new BakeResult(
            _area.PlainLight ? irradiance : null, _area.PlainLight ? direction : null, mask, transfer,
            covered == 0 ? 0f : (float)invalid / covered, _passes, probeIrradiance, probeDepth);
        return _compose(result).ToBytes();
    }

    private byte[] ProbeIrradiance =>
        _probeIrradiance ??= new byte[BakedProbeAtlas.IrradianceAtlasSize * BakedProbeAtlas.IrradianceAtlasSize * 8];

    private byte[] ProbeDepth =>
        _probeDepth ??= new byte[BakedProbeAtlas.DepthAtlasSize * BakedProbeAtlas.DepthAtlasSize * 8];

    // Builds the held top level and draws the texels into the baker's pages.
    private BakeStepResult SetUp(CommandBuffer cmd)
    {
        _baker.ClaimPages(this, Name);
        _claimed = true;
        _scene = _baker.BuildScene(cmd, _area.Draws, out var release);
        _sceneRelease = release;
        _baker.RecordTexels(cmd, _pages, _area.Draws, _pool, _scene);
        _page = 0;
        return NextPage();
    }

    // Starts the next page, the probes, or ends the bake.
    private BakeStepResult NextPage()
    {
        if (_page < _pages.Pages)
        {
            _stage = Stage.Trace;
            return BakeStepResult.Continue;
        }

        if (_probeCount > 0)
        {
            _stage = Stage.ProbeTrace;
            return BakeStepResult.Continue;
        }

        _stage = Stage.Done;
        return BakeStepResult.Done;
    }

    // A slice of the page's passes, and once they are all traced the copy of its sums.
    private BakeStepResult Trace(CommandBuffer cmd, int passes)
    {
        if (_sums is null)
        {
            _region = Baker.UsedRegion(_pages, _page);
            _sums = _baker.CreateSums(_region.Width, _region.Height, _area.IsChunk);
            _traceSet = _baker.CreateTraceSet(_pool, _page, _scene, _lights, _sums, _sky, _skySampler);
            _baker.ClearSums(cmd, _sums);
            _pass = 0;
        }

        var (x, y, width, height) = _region;
        var constants = new Baker.TraceConstants
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            LightCount = (uint)_area.Lights.Length,
            Lift = Baker.TraceLift,
            SkyIntensity = _area.SkyIntensity,
            Chunk = _area.IsChunk ? 1u : 0u,
        };
        int count = Math.Min(passes, _passes - _pass);
        _baker.RecordPasses(cmd, _traceSet, constants, _pass, count, width, height);
        _pass += count;
        PassesInLastStep = count;
        _doneWork += (double)width * height * count;
        if (_pass < _passes)
        {
            return BakeStepResult.Continue;
        }

        _sumsBuffer = GpuBuffer.CreateHostVisible(
            _context, Baker.SumsReadBackBytes(width, height, _area.IsChunk), BufferUsageFlags.TransferDstBit);
        _baker.RecordSumsReadBack(cmd, _page, _sums, _sumsBuffer, x, y, width, height, _area.IsChunk);
        _stage = Stage.ReadSums;
        return BakeStepResult.AwaitGpu;
    }

    /// <summary>
    /// Starts <paramref name="work"/> on the thread pool and returns <see cref="BakeStepResult.AwaitCpu"/>; the first
    /// step that finds it done calls <paramref name="then"/> on its own thread to record what follows. The work may
    /// touch only what it was handed and the job's mapped buffers, which the job keeps until it is done.
    /// </summary>
    private BakeStepResult OffThread(Action work, Func<CommandBuffer, BakeStepResult> then)
    {
        _cpu = Task.Run(work);
        _afterCpu = then;
        _stage = Stage.Cpu;
        return BakeStepResult.AwaitCpu;
    }

    private BakeStepResult AfterCpu(CommandBuffer cmd)
    {
        var task = _cpu!;
        if (!task.IsCompleted)
        {
            return BakeStepResult.AwaitCpu;
        }

        var then = _afterCpu!;
        _cpu = null;
        _afterCpu = null;

        // Throws the work's own exception, as if it had run in this step.
        task.GetAwaiter().GetResult();
        return then(cmd);
    }

    // Takes the page's mean light from its sums on the thread pool, then filters it.
    private BakeStepResult ReadSums()
    {
        var (x, y, width, height) = _region;
        int page = _page;
        int passes = _passes;
        bool chunk = _area.IsChunk;
        var buffer = _sumsBuffer!;

        // The GPU is done with the sums; only their readback is still read.
        FreeSumTargets();
        var run = _baker.CreateFilter(_pool, page, x, y, width, height);
        _run = run;
        TracedPage? raw = null;
        return OffThread(
            () =>
            {
                raw = Baker.ReadSumsBack(buffer, page, x, y, width, height, passes, chunk);
                run.Fill(raw.Irradiance, raw.Direction, raw.Mask);
            },
            next =>
            {
                FreeSums();
                _raw = raw;
                _baker.RecordFilter(next, run, true, _filter);
                _stage = Stage.ReadFilter;
                return BakeStepResult.AwaitGpu;
            });
    }

    // Keeps the filtered page, then dilates a chunk's transfer through the same run, its first run filled on the
    // thread pool.
    private BakeStepResult ReadFilter()
    {
        var raw = _raw!;
        var run = _run!;
        bool chunk = _area.IsChunk;
        TracedPage? filtered = null;
        float[]? transfer = null;
        bool[]? filled = null;
        return OffThread(
            () =>
            {
                filtered = BakeEncoding.Filtered(raw, run);
                if (!chunk)
                {
                    return;
                }

                int count = raw.Width * raw.Height;
                transfer = new float[count * Baker.TransferFloats];
                filled = new bool[count];
                var (irradiance, direction, mask) = BakeEncoding.TransferRun(raw, 0);
                run.Fill(irradiance, direction, mask);
            },
            next =>
            {
                _filtered.Add(filtered!);
                if (!chunk)
                {
                    return EndPage();
                }

                _transfer = transfer;
                _transferFilled = filled;
                _transferRun = 0;
                _baker.RecordFilter(next, run, false, false);
                _stage = Stage.ReadTransfer;
                return BakeStepResult.AwaitGpu;
            });
    }

    // Takes a run of the transfer dilation back, and fills the next, on the thread pool.
    private BakeStepResult ReadTransfer()
    {
        var raw = _raw!;
        var run = _run!;
        var transfer = _transfer!;
        var filled = _transferFilled!;
        int index = _transferRun;
        bool more = index + 1 < BakeEncoding.TransferRuns;
        return OffThread(
            () =>
            {
                BakeEncoding.ReadTransferRun(run, index, transfer, filled);
                if (more)
                {
                    var (irradiance, direction, mask) = BakeEncoding.TransferRun(raw, index + 1);
                    run.Fill(irradiance, direction, mask);
                }
            },
            next =>
            {
                _transferRun = index + 1;
                if (more)
                {
                    _baker.RecordFilter(next, run, false, false);
                    _stage = Stage.ReadTransfer;
                    return BakeStepResult.AwaitGpu;
                }

                _transferPage = raw with { Transfer = transfer, Filled = filled };
                _transfer = null;
                _transferFilled = null;
                return EndPage();
            });
    }

    private BakeStepResult EndPage()
    {
        _run?.Dispose();
        _run = null;
        _raw = null;
        _page++;
        return NextPage();
    }

    // A slice of a batch of probes' passes, after clearing the batch's sums.
    private BakeStepResult TraceProbes(CommandBuffer cmd, int passes)
    {
        if (_probeSum is null)
        {
            var settings = _area.Probes!.Value;
            _positions = GpuBuffer.CreateHostVisible(
                _context, (ulong)_probeCount * (ulong)sizeof(Vector4), BufferUsageFlags.StorageBufferBit);
            var positions = new Span<Vector4>(_positions.Mapped, _probeCount);
            for (int i = 0; i < _probeCount; i++)
            {
                positions[i] = new Vector4(ProbeGrid.Position(settings, i), 1f);
            }

            (_probeSum, _probeFaces) = _baker.CreateProbeTargets();
            _probeSet = _baker.CreateProbeSet(_pool, _scene, _lights, _positions, _probeSum, _probeFaces, _sky, _skySampler);
            int slots = Math.Min(_volume!.FaceSlots, _volume.StagingSlots);
            _foldBuffer = GpuBuffer.CreateHostVisible(
                _context, (ulong)slots * ProbeVolume.StagedTileBytes, BufferUsageFlags.TransferDstBit);
            _firstProbe = 0;
            _probePass = 0;
        }

        if (_probePass == 0)
        {
            _baker.ClearSums(cmd, [_probeSum, _probeFaces!]);
        }

        var constants = new Baker.ProbeTraceConstants
        {
            FirstProbe = (uint)_firstProbe,
            ProbeCount = (uint)_probeCount,
            Passes = (uint)_probePasses,
            LightCount = (uint)_area.Lights.Length,
            Lift = Baker.TraceLift,
            SkyIntensity = _area.SkyIntensity,
            MaxDistance = _area.Probes!.Value.Spacing * 2f,
        };
        var extent = Baker.ProbeExtent;
        int count = Math.Min(passes, _probePasses - _probePass);
        _baker.RecordProbePasses(cmd, _probeSet, constants, _probePass, count, extent);
        _probePass += count;
        PassesInLastStep = count;
        _doneWork += (double)extent.Width * extent.Height * count;
        if (_probePass == _probePasses)
        {
            _row = 0;
            _stage = Stage.Fold;
        }

        return BakeStepResult.Continue;
    }

    // The live fold of as many of the batch's probes as the volume has slots for, and the copy of their tiles. The
    // traced distances are already clamped short of the fold's sky mark, so it keeps the sky the paths brought back.
    private BakeStepResult Fold(CommandBuffer cmd)
    {
        var volume = _volume!;
        int slots = Math.Min(volume.FaceSlots, volume.StagingSlots);
        int inBatch = Math.Min(Baker.ProbesPerBatch, _probeCount - _firstProbe);
        _inGroup = Math.Min(slots, inBatch - _row);
        _baker.RecordCopyFaces(cmd, _probeFaces!, _row, _inGroup, volume);
        volume.BeginFolds(cmd);
        for (int slot = 0; slot < _inGroup; slot++)
        {
            volume.RecordFold(cmd, slot, _area.Probes!.Value.Spacing, _area.SkyIntensity, Vector4.Zero);
        }

        volume.RecordStagingReadBack(cmd, _foldBuffer!, _inGroup);
        _stage = Stage.ReadFold;
        return BakeStepResult.AwaitGpu;
    }

    // Writes the folded tiles into the atlases, then folds the next group, traces the next batch, or ends the bake.
    private BakeStepResult ReadFold()
    {
        for (int slot = 0; slot < _inGroup; slot++)
        {
            var tile = new ReadOnlySpan<float>(
                (byte*)_foldBuffer!.Mapped + (long)slot * ProbeVolume.StagedTileBytes,
                ProbeVolume.StagedTileBytes / sizeof(float));
            BakeEncoding.WriteTile(tile, _firstProbe + _row + slot, ProbeIrradiance, ProbeDepth);
        }

        _row += _inGroup;
        int inBatch = Math.Min(Baker.ProbesPerBatch, _probeCount - _firstProbe);
        if (_row < inBatch)
        {
            _stage = Stage.Fold;
            return BakeStepResult.Continue;
        }

        _firstProbe += Baker.ProbesPerBatch;
        _probePass = 0;
        if (_firstProbe < _probeCount)
        {
            _stage = Stage.ProbeTrace;
            return BakeStepResult.Continue;
        }

        FreeProbes();
        _stage = Stage.Done;
        return BakeStepResult.Done;
    }

    private void FreeSums()
    {
        FreeSumTargets();
        _sumsBuffer?.Dispose();
        _sumsBuffer = null;
    }

    private void FreeSumTargets()
    {
        if (_sums is null)
        {
            return;
        }

        foreach (var sum in _sums)
        {
            sum.Dispose();
        }

        _sums = null;
    }

    private void FreeProbes()
    {
        _probeSum?.Dispose();
        _probeSum = null;
        _probeFaces?.Dispose();
        _probeFaces = null;
        _positions?.Dispose();
        _positions = null;
        _foldBuffer?.Dispose();
        _foldBuffer = null;
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
        WaitForCpu();
        _cpu = null;
        _afterCpu = null;
        FreeSums();
        _run?.Dispose();
        _run = null;
        FreeProbes();
        _lights.Dispose();
        _baker.DestroyPool(_pool);
        _sceneRelease?.Dispose();
        _sceneRelease = null;
        if (_claimed)
        {
            _baker.ReleasePages(this);
        }
    }
}
