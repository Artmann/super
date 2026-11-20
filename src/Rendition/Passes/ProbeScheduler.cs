using System.Numerics;

namespace Rendition;

/// <summary>How many probes one frame may update: with rasterized captures, and with rays.</summary>
internal readonly record struct ProbeBudget(int Raster, int Traced)
{
    public static ProbeBudget For(ProbeQuality quality)
    {
        return quality switch
        {
            ProbeQuality.Low => new ProbeBudget(8, 256),
            ProbeQuality.Medium => new ProbeBudget(16, 512),
            _ => new ProbeBudget(32, 1024)
        };
    }

    /// <summary>
    /// How many clean probes the traced path refreshes a frame once the probes near the camera are settled and their
    /// bounce has built up (see <see cref="ProbeScheduler.BounceRefreshes"/>): a quarter of its budget. Refreshing the
    /// full budget cost up to about 1 ms of GPU time a frame in a settled scene.
    /// </summary>
    public int TracedRefresh => Traced / 4;
}

/// <summary>A probe: its cascade (0 to 3, or 4 for the fixed grid) and its slot in that cascade.</summary>
internal readonly record struct ProbeId(int Cascade, int Slot);

/// <summary>The cells first to last of a grid on each axis. Empty on an axis (first greater than last) when none are.</summary>
internal readonly record struct CellBox(Int3 First, Int3 Last)
{
    /// <summary>
    /// The cells within [start, start + count) whose positions cell * spacing are from low to high, given relative to
    /// the grid's own origin.
    /// </summary>
    public static CellBox Overlapping(Vector3 low, Vector3 high, float spacing, Int3 start, Int3 count)
    {
        var x = Axis(low.X, high.X, spacing, start.X, count.X);
        var y = Axis(low.Y, high.Y, spacing, start.Y, count.Y);
        var z = Axis(low.Z, high.Z, spacing, start.Z, count.Z);
        return new CellBox(new Int3(x.First, y.First, z.First), new Int3(x.Last, y.Last, z.Last));
    }

    private static (int First, int Last) Axis(float low, float high, float spacing, int start, int count)
    {
        float first = Math.Clamp(MathF.Ceiling(low / spacing), start, start + count);
        float last = Math.Clamp(MathF.Floor(high / spacing), start - 1, start + count - 1);
        return ((int)first, (int)last);
    }
}

/// <summary>
/// Where the fixed grid's probes are: probe slot x + countX * (y + countY * z) is at min + (x, y, z) * spacing. The
/// default has a spacing of 0, for a grid whose positions were never set.
/// </summary>
internal readonly struct ProbeGridPlacement(Vector3 min, Int3 count, float spacing)
{
    /// <summary>Whether the probes are spread out, so the cells near a point can be found.</summary>
    public bool Spread => spacing > 0f;

    /// <summary>The grid as a box of cells, whose slots are its own as it never wraps.</summary>
    public ProbeCascadeLayout CellLayout => new(spacing, count.X, count.Y, count.Z);

    /// <summary>The cells within <paramref name="range"/> of <paramref name="center"/>, all inside the grid.</summary>
    public CellBox CellsNear(Vector3 center, float range) => CellBox.Overlapping(
        center - new Vector3(range) - min, center + new Vector3(range) - min, spacing, default, count);
}

/// <summary>
/// Picks which probes to update each frame, within a count budget. It never looks at the GPU and
/// never at the clock: ages are counted in calls to <see cref="Next"/>, so the same calls give the
/// same batches.
///
/// Every probe is in one of three states: never captured, dirty (captured, but the lighting changed
/// since) or clean. A dirty probe keeps its old value in use until it is updated. A frame hands out
/// dirty probes in cascades 0 and 1 and the fixed grid first, so a light changing near the camera
/// shows at once even while the coarse cascades are still filling in. Then come never-captured
/// probes, then the other dirty ones, then the probes that were updated longest ago. While any
/// probe is never captured, a quarter of the budget is kept for them, so a light that changes every
/// frame (a flickering lantern) can't stop new probes from ever being captured. In the dirty and
/// never-captured tiers nearer probes go first, and so do finer cascades and probes in front of the
/// camera. In the last tier the oldest goes first, whatever its place, which bounds the time between
/// two updates of any probe to about (probes / budget) frames, or (probes / refreshBudget) once settled and the
/// bounce has had <see cref="BounceRefreshes"/> refreshes; distance only breaks ties.
///
/// <see cref="Next"/> never looks at every probe. <see cref="ProbeStates"/> keeps the probes that aren't clean in a
/// list as they change state, and <see cref="ProbeBatchPicker"/> gives each one number and brings the lowest
/// <c>budget</c> of them forward. Clean probes are found through a queue of captures in the order they were made, so the oldest are at
/// its front and only about a budget of them are read. A settled scene so costs O(budget log budget) a frame however
/// many probes there are. Once its buffers have grown, nothing on the per-frame path allocates: <see cref="Next"/>
/// hands back the same list each call, and <see cref="Settled"/> reads counts kept as probes change state rather than
/// scanning them.
/// </summary>
internal sealed class ProbeScheduler
{
    /// <summary>The cascade index of the fixed grid in a <see cref="ProbeId"/>.</summary>
    public const int FixedGrid = ProbeStates.GridGroup;

    /// <summary>
    /// How many refreshes of every probe, counted together, <see cref="Next"/> hands out at the full budget after the
    /// last never-captured or dirty probe is done, before it holds clean refreshes to its refresh budget. Each refresh
    /// adds one more bounce of light, and the white test room has about 80% of its bounce after 12.
    /// </summary>
    public const int BounceRefreshes = 12;

    private readonly ProbeCascades _cascades;
    private readonly ProbeStates _states = new();
    private readonly ProbeBatchPicker _picker;

    // Where the fixed grid's probes are, so LightChanged can find the cells a light covers. The default, with a
    // spacing of 0, means SetFixedGrid hasn't been called.
    private ProbeGridPlacement _grid;

    private bool _scrolled;

    public ProbeScheduler(ProbeCascades cascades, int fixedGridProbes)
    {
        ArgumentNullException.ThrowIfNull(cascades);
        ArgumentOutOfRangeException.ThrowIfNegative(fixedGridProbes);

        _cascades = cascades;
        _picker = new ProbeBatchPicker(_states);
        _states.ReplaceGroup(FixedGrid, fixedGridProbes);
    }

    /// <summary>How many calls to <see cref="Next"/> there have been. The first call is frame 1.</summary>
    public long Frame { get; private set; }

    /// <summary>The number of probes in the fixed grid.</summary>
    public int FixedGridProbes => _states.ProbesIn(FixedGrid);

    /// <summary>
    /// True when every probe in cascades 0 and 1 and in the fixed grid has been captured since its last
    /// change. Coarser cascades don't count, as they only matter far from the camera. False before the
    /// first <see cref="Scrolled"/>.
    /// </summary>
    public bool Settled => _scrolled && _states.FineSettled;

    /// <summary>
    /// Sets where the fixed grid's probes are, so distance and <see cref="LightChanged"/> work for them:
    /// probe slot x + countX * (y + countY * z) is at min + (x, y, z) * spacing. Every probe of the grid
    /// becomes never captured, as the grid has changed. The scheduler has no other way to learn the
    /// grid's positions; without this call they are all at the origin.
    /// </summary>
    public void SetFixedGrid(Vector3 min, int countX, int countY, int countZ, float spacing)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countZ);

        _states.ReplaceGroup(FixedGrid, checked(countX * countY * countZ));
        _grid = new ProbeGridPlacement(min, new Int3(countX, countY, countZ), spacing);
        for (int z = 0; z < countZ; z++)
        {
            for (int y = 0; y < countY; y++)
            {
                for (int x = 0; x < countX; x++)
                {
                    _states.MakeNew(FixedGrid, x + countX * (y + countY * z), min + new Vector3(x, y, z) * spacing);
                }
            }
        }
    }

    /// <summary>Takes away the fixed grid, for a view that no longer has one.</summary>
    public void ClearFixedGrid()
    {
        _states.ReplaceGroup(FixedGrid, 0);
        _grid = default;
    }

    /// <summary>
    /// Call with every <see cref="ProbeCascades.Move"/> result. Probes in new slots are now other
    /// probes and become never captured. The first result must be the cascades' first move, as that is
    /// how the scheduler learns how many probes each cascade has.
    /// </summary>
    public void Scrolled(ScrollResult scroll)
    {
        for (int c = 0; c < ProbeCascades.Count; c++)
        {
            var slots = scroll.NewSlots(c);
            if (_states.ProbesIn(c) == 0)
            {
                if (!scroll.Teleported(c))
                {
                    throw new InvalidOperationException(
                        $"Cascade {c} has not been scrolled before, so its scroll must be a whole new box (the first Move).");
                }

                _states.ReplaceGroup(c, slots.Count);
            }

            for (int i = 0; i < slots.Count; i++)
            {
                _states.MakeNew(c, slots[i], _cascades.Position(c, slots[i]));
            }
        }

        _scrolled = true;
    }

    /// <summary>
    /// A light moved, appeared or went out: captured probes within range of its centre are dirty. Only the cells the
    /// light's sphere overlaps are looked at, in each cascade's box and in the fixed grid, so a small light costs a few
    /// probes, not a pass over all of them.
    /// </summary>
    public void LightChanged(Vector3 center, float range)
    {
        if (!(range >= 0f))
        {
            return;
        }

        for (int c = 0; c < ProbeCascades.Count; c++)
        {
            if (_states.ProbesIn(c) > 0)
            {
                var layout = _cascades.Layout(c);
                var cells = CellBox.Overlapping(
                    center - new Vector3(range),
                    center + new Vector3(range),
                    layout.Spacing,
                    _cascades.Origin(c),
                    new Int3(layout.CountX, layout.CountY, layout.CountZ));
                MarkCells(c, cells, layout, center, range);
            }
        }

        if (_grid.Spread)
        {
            // The cells are all inside the grid, so wrapping them leaves each at its own slot.
            MarkCells(FixedGrid, _grid.CellsNear(center, range), _grid.CellLayout, center, range);
            return;
        }

        // A grid whose positions were never set has every probe at the origin, so check each one.
        for (int slot = 0; slot < FixedGridProbes; slot++)
        {
            _states.DirtyIfNear(FixedGrid, slot, center, range);
        }
    }

    // Marks the captured probes of a group in the cells within range of the light.
    private void MarkCells(int group, CellBox cells, ProbeCascadeLayout layout, Vector3 center, float range)
    {
        for (int z = cells.First.Z; z <= cells.Last.Z; z++)
        {
            for (int y = cells.First.Y; y <= cells.Last.Y; y++)
            {
                for (int x = cells.First.X; x <= cells.Last.X; x++)
                {
                    _states.DirtyIfNear(group, layout.Wrap(new Int3(x, y, z)), center, range);
                }
            }
        }
    }

    /// <summary>The sun, sky or environment changed: every captured probe is dirty, but its value stays in use.</summary>
    public void EverythingChanged() => _states.DirtyAll();

    /// <summary>
    /// Advances the frame and returns the probes to update in it, best first: never more than
    /// <paramref name="budget"/>, none twice. Call <see cref="Updated"/> with the batch once its probes
    /// are captured, in the same frame and before the next change to the lighting. The list is the
    /// scheduler's own and the next call refills it, so copy it to keep it. Once <see cref="Settled"/>, and once
    /// <see cref="BounceRefreshes"/> refreshes of every probe have been updated since the last never-captured or dirty
    /// probe, at most <paramref name="refreshBudget"/> of the batch are clean probes refreshed for their age, so a
    /// settled scene costs less than one still filling in, yet bounce builds up at the full budget first.
    /// </summary>
    public IReadOnlyList<ProbeId> Next(int budget, Vector3 camera, Vector3 forward, int refreshBudget = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(refreshBudget);
        Frame++;

        // Once the fine groups are settled and the bounce has had its refreshes, only refreshBudget clean probes are
        // taken.
        bool bounced = _states.BounceBuiltUp(BounceRefreshes);
        int refreshes = Settled && bounced ? refreshBudget : int.MaxValue;
        return _picker.Pick(Frame, budget, camera, forward, refreshes);
    }

    /// <summary>The batch has been captured: its probes are clean, and as old as this frame.</summary>
    public void Updated(IReadOnlyList<ProbeId> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        for (int i = 0; i < batch.Count; i++)
        {
            var id = batch[i];
            if (!_states.TryStateOf(id, out _))
            {
                throw new ArgumentOutOfRangeException(nameof(batch), id, "There is no such probe.");
            }

            _states.MarkCaptured(id, Frame);
        }
    }

    /// <summary>True when the probe has a value in use: it has been captured, whether or not it is dirty now.</summary>
    public bool Captured(ProbeId id)
    {
        return _states.TryStateOf(id, out byte state) && state != ProbeStates.NeverCaptured;
    }

    /// <summary>True when the probe has been captured but the lighting changed since, so it waits to be updated.</summary>
    internal bool IsDirty(ProbeId id)
    {
        return _states.TryStateOf(id, out byte state) && state == ProbeStates.Dirty;
    }
}
