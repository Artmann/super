using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// A <see cref="BakeJob"/>'s steps for its pages, one page after another: its paths a few passes at a time, the
/// readback of their sums, the filter and, for a chunk, both runs of its transfer dilation. The work that touches every
/// texel of a page runs on the thread pool through <see cref="BakeCpuWork"/>. What each page leaves goes in
/// <see cref="Results"/>.
/// </summary>
internal sealed unsafe class BakePageSteps(BakeJobKit kit, BakeCpuWork cpu, bool probesFollow)
{
    private int _page;
    private PageTrace? _trace;
    private PageFilter? _filter;

    /// <summary>What each page left.</summary>
    public PageResults Results { get; } = new();

    /// <summary>Starts the next page, the probes, or ends the bake.</summary>
    public BakeStep Next()
    {
        if (_page < kit.Layout.Pages)
        {
            return new BakeStep(BakeStepResult.Continue, BakeStage.Trace);
        }

        if (probesFollow)
        {
            return new BakeStep(BakeStepResult.Continue, BakeStage.ProbeTrace);
        }

        return new BakeStep(BakeStepResult.Done, BakeStage.Done);
    }

    /// <summary>A slice of the page's passes, and once they are all traced the copy of its sums.</summary>
    public BakeStep Trace(CommandBuffer cmd, int passes)
    {
        var baker = kit.Baker;
        var trace = _trace ??= new PageTrace(kit, _page, cmd);
        var (x, y, width, height) = trace.Region;
        var constants = new BakeTraceConstants
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            LightCount = (uint)kit.Area.Lights.Length,
            Lift = BakeGpu.TraceLift,
            SkyIntensity = kit.Area.SkyIntensity,
            Chunk = kit.Area.IsChunk ? 1u : 0u,
        };
        int count = Math.Min(passes, kit.Passes - trace.Pass);
        baker.Tracing.RecordPasses(cmd, trace.Set, constants, trace.Pass, count);
        trace.Pass += count;
        kit.PassesInLastStep = count;
        kit.DoneWork += (double)width * height * count;
        if (trace.Pass < kit.Passes)
        {
            return new BakeStep(BakeStepResult.Continue, BakeStage.Trace);
        }

        trace.RecordReadBack(cmd);
        return new BakeStep(BakeStepResult.AwaitGpu, BakeStage.ReadSums);
    }

    /// <summary>Takes the page's mean light from its sums on the thread pool, then filters it.</summary>
    public BakeStep ReadSums()
    {
        var trace = _trace!;
        var readBack = trace.ReadBack;
        int passes = kit.Passes;
        var buffer = trace.Buffer!;

        // The GPU is done with the sums; only their readback is still read.
        trace.FreeTargets();
        var filter = new PageFilter(kit.Baker.CreateFilter(kit.Pool, _page, trace.Region));
        _filter = filter;
        TracedPage? raw = null;
        return cpu.Start(
            () =>
            {
                raw = readBack.Read((byte*)buffer.Mapped, passes);
                filter.Run.Fill(raw.Irradiance, raw.Direction, raw.Mask);
            },
            next =>
            {
                trace.Free();
                _trace = null;
                filter.Raw = raw;
                kit.Baker.Filtering.Record(next, filter.Run, true, kit.Filter);
                return new BakeStep(BakeStepResult.AwaitGpu, BakeStage.ReadFilter);
            });
    }

    /// <summary>
    /// Keeps the filtered page, then dilates a chunk's transfer through the same run, its first run filled on the
    /// thread pool.
    /// </summary>
    public BakeStep ReadFilter()
    {
        var filter = _filter!;
        var raw = filter.Raw!;
        var run = filter.Run;
        bool chunk = kit.Area.IsChunk;
        TracedPage? filtered = null;
        float[]? transfer = null;
        bool[]? filled = null;
        return cpu.Start(
            () =>
            {
                filtered = BakeEncoding.Filtered(raw, run);
                if (!chunk)
                {
                    return;
                }

                int count = raw.Width * raw.Height;
                transfer = new float[count * BakeGpu.TransferFloats];
                filled = new bool[count];
                var (irradiance, direction, mask) = BakeEncoding.TransferRun(raw, 0);
                run.Fill(irradiance, direction, mask);
            },
            next =>
            {
                Results.Add(filtered!);
                if (!chunk)
                {
                    return EndPage();
                }

                filter.Transfer = transfer;
                filter.Filled = filled;
                filter.TransferRun = 0;
                kit.Baker.Filtering.Record(next, run, false, false);
                return new BakeStep(BakeStepResult.AwaitGpu, BakeStage.ReadTransfer);
            });
    }

    /// <summary>Takes a run of the transfer dilation back, and fills the next, on the thread pool.</summary>
    public BakeStep ReadTransfer()
    {
        var filter = _filter!;
        var raw = filter.Raw!;
        var run = filter.Run;
        var transfer = filter.Transfer!;
        var filled = filter.Filled!;
        int index = filter.TransferRun;
        bool more = index + 1 < BakeEncoding.TransferRuns;
        return cpu.Start(
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
                filter.TransferRun = index + 1;
                if (more)
                {
                    kit.Baker.Filtering.Record(next, run, false, false);
                    return new BakeStep(BakeStepResult.AwaitGpu, BakeStage.ReadTransfer);
                }

                Results.TransferPage = raw with { Transfer = transfer, Filled = filled };
                filter.Transfer = null;
                filter.Filled = null;
                return EndPage();
            });
    }

    private BakeStep EndPage()
    {
        _filter?.Run.Dispose();
        _filter = null;
        _page++;
        return Next();
    }

    /// <summary>Frees what the page being baked holds.</summary>
    public void Free()
    {
        _trace?.Free();
        _trace = null;
        _filter?.Run.Dispose();
        _filter = null;
    }
}

/// <summary>
/// One page's tracing: the region its rectangles cover, its sums and their trace set, the passes traced, and once they
/// are all traced the buffer its sums are read back into.
/// </summary>
internal sealed class PageTrace
{
    private readonly BakeJobKit _kit;
    private RenderTarget[]? _sums;

    /// <summary>Makes the page's sums and trace set, and records the sums' clear.</summary>
    public PageTrace(BakeJobKit kit, int page, CommandBuffer cmd)
    {
        _kit = kit;
        var baker = kit.Baker;
        Region = Baker.UsedRegion(kit.Layout, page);
        ReadBack = new SumsReadBack(page, Region, kit.Area.IsChunk);
        _sums = baker.Tracing.CreateSums(Region.Width, Region.Height, kit.Area.IsChunk);
        Set = baker.Tracing.CreateSet(kit.Pool, baker.Pages[page], kit.Inputs, _sums);
        baker.Tracing.ClearSums(cmd, _sums);
    }

    public (int X, int Y, int Width, int Height) Region { get; }

    /// <summary>Where the readback of the page's sums puts what it copies.</summary>
    public SumsReadBack ReadBack { get; }

    public DescriptorSet Set { get; }

    /// <summary>The passes traced so far.</summary>
    public int Pass { get; set; }

    /// <summary>The buffer the sums are read back into, once <see cref="RecordReadBack"/> has run.</summary>
    public GpuBuffer? Buffer { get; private set; }

    /// <summary>Records the copy of the sums, and the page's positions and validity, into a new buffer.</summary>
    public void RecordReadBack(CommandBuffer cmd)
    {
        var readBack = ReadBack;
        Buffer = GpuBuffer.CreateHostVisible(_kit.Context, readBack.Bytes, BufferUsageFlags.TransferDstBit);
        _kit.Baker.Tracing.RecordReadBack(cmd, _sums!, Buffer, readBack, _kit.Baker.Pages[readBack.Page]);
    }

    /// <summary>Frees the sums, once the GPU is done with them; their readback stays.</summary>
    public void FreeTargets()
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

    /// <summary>Frees the sums and their readback.</summary>
    public void Free()
    {
        FreeTargets();
        Buffer?.Dispose();
        Buffer = null;
    }
}

/// <summary>One page's filter run, the page as traced, and a chunk's transfer as its dilation runs fill it.</summary>
internal sealed class PageFilter(FilterRun run)
{
    public FilterRun Run { get; } = run;

    /// <summary>The page as traced, once its sums are read.</summary>
    public TracedPage? Raw { get; set; }

    /// <summary>A chunk's transfer, filled a dilation run at a time.</summary>
    public float[]? Transfer { get; set; }

    /// <summary>Which of the transfer's texels hold light.</summary>
    public bool[]? Filled { get; set; }

    /// <summary>The transfer dilation run read back next.</summary>
    public int TransferRun { get; set; }
}

/// <summary>What a <see cref="BakeJob"/>'s pages left: each filtered page and a chunk's transfer, and their encoding.</summary>
internal sealed class PageResults
{
    private readonly List<TracedPage> _filtered = [];

    /// <summary>A chunk's transfer, with the texels that hold it.</summary>
    public TracedPage? TransferPage { get; set; }

    public void Add(TracedPage filtered) => _filtered.Add(filtered);

    /// <summary>Each page's irradiance, direction and mask as the file stores them, cut to <paramref name="side"/>.</summary>
    public (List<LightBakePage> Irradiance, List<LightBakePage> Direction, List<LightBakePage> Mask) Encode(int side)
    {
        var irradiance = new List<LightBakePage>();
        var direction = new List<LightBakePage>();
        var mask = new List<LightBakePage>();
        foreach (var page in _filtered)
        {
            var encoded = BakeEncoding.Encode(page);
            irradiance.Add(BakeEncoding.Crop(encoded.Irradiance, side));
            direction.Add(BakeEncoding.Crop(encoded.Direction, side));
            mask.Add(BakeEncoding.Crop(encoded.Mask, side));
        }

        return (irradiance, direction, mask);
    }

    /// <summary>The chunk's transfer as the file stores it. A chunk with nothing that can hold a lightmap still has one, holding none.</summary>
    public LightBakeTransfer EncodeTransfer(int side)
    {
        var page = TransferPage ?? new TracedPage(0, 0, 0, 0, 0, [], [], [], [], [], []) { Transfer = [] };
        return BakeEncoding.EncodeTransfer(page, side);
    }

    /// <summary>The share of the covered texels no ray found a front face for, 0 when none is covered.</summary>
    public float InvalidShare()
    {
        long covered = 0;
        long invalid = 0;
        foreach (var page in _filtered)
        {
            for (int t = 0; t < page.Covered.Length; t++)
            {
                if (page.Covered[t])
                {
                    covered++;
                    invalid += page.Valid[t] ? 0 : 1;
                }
            }
        }

        return covered == 0 ? 0f : (float)invalid / covered;
    }
}
