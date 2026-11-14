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
/// <see cref="Next"/> never looks at every probe. The probes that aren't clean are kept in a list as they change
/// state, and the best <c>budget</c> of them in a small binary heap. Clean probes are found through a queue of
/// captures in the order they were made, so the oldest are at its front and only about a budget of them are read. A
/// settled scene so costs O(budget log budget) a frame however many probes there are. Once its buffers have grown,
/// nothing on the per-frame path allocates: <see cref="Next"/> hands back the same list each call, and
/// <see cref="Settled"/> reads counts kept as probes change state rather than scanning them.
/// </summary>
internal sealed class ProbeScheduler
{
    /// <summary>The cascade index of the fixed grid in a <see cref="ProbeId"/>.</summary>
    public const int FixedGrid = ProbeCascades.Count;

    /// <summary>
    /// How many refreshes of every probe, counted together, <see cref="Next"/> hands out at the full budget after the
    /// last never-captured or dirty probe is done, before it holds clean refreshes to its refresh budget. Each refresh
    /// adds one more bounce of light, and the white test room has about 80% of its bounce after 12.
    /// </summary>
    public const int BounceRefreshes = 12;

    private const byte NeverCaptured = 0;
    private const byte Dirty = 1;
    private const byte Clean = 2;

    private readonly ProbeCascades _cascades;

    // Per group (cascades 0 to 3, then the fixed grid) and slot. The cascades' arrays are created by
    // the first Scrolled, which is the only place their sizes are known.
    private readonly byte[]?[] _state = new byte[]?[FixedGrid + 1];
    private readonly long[]?[] _updated = new long[]?[FixedGrid + 1];
    private readonly Vector3[]?[] _position = new Vector3[]?[FixedGrid + 1];

    // Per group, how many probes are not clean (never captured or dirty), so Settled needn't scan.
    private readonly int[] _pending = new int[FixedGrid + 1];

    private bool _scrolled;

    // Clean probes refreshed since the last frame that had a never-captured or dirty probe in any group.
    private long _refreshesSinceChange;

    // Where the fixed grid's probes are, so LightChanged can find the cells a light covers. A spacing of 0 means
    // SetFixedGrid hasn't been called.
    private Vector3 _gridMin;
    private Int3 _gridCount;
    private float _gridSpacing;

    // The best dirty probes of the fine groups, and the best of all the rest, each kept to the budget.
    private ProbeCandidate[] _nearHeap = Array.Empty<ProbeCandidate>();
    private ProbeCandidate[] _heap = Array.Empty<ProbeCandidate>();
    private readonly List<ProbeId> _batch = [];

    // The probes that aren't clean, each once: _listed says which are in it. A probe stays listed after it is
    // captured until the next Next, which drops it.
    private readonly List<ProbeId> _unsettled = [];
    private readonly bool[]?[] _listed = new bool[]?[FixedGrid + 1];

    // Every capture in the order they were made, so the oldest clean probes are at the front.
    private readonly ProbeCaptureQueue _captures;

    public ProbeScheduler(ProbeCascades cascades, int fixedGridProbes)
    {
        ArgumentNullException.ThrowIfNull(cascades);
        ArgumentOutOfRangeException.ThrowIfNegative(fixedGridProbes);

        _cascades = cascades;
        _captures = new ProbeCaptureQueue(_state, _updated, _position, Clean);
        AllocateGroup(FixedGrid, fixedGridProbes);
    }

    /// <summary>How many calls to <see cref="Next"/> there have been. The first call is frame 1.</summary>
    public long Frame { get; private set; }

    /// <summary>The number of probes in the fixed grid.</summary>
    public int FixedGridProbes => _state[FixedGrid]!.Length;

    /// <summary>
    /// True when every probe in cascades 0 and 1 and in the fixed grid has been captured since its last
    /// change. Coarser cascades don't count, as they only matter far from the camera. False before the
    /// first <see cref="Scrolled"/>.
    /// </summary>
    public bool Settled
    {
        get
        {
            if (!_scrolled)
            {
                return false;
            }

            return _pending[0] == 0 && _pending[1] == 0 && _pending[FixedGrid] == 0;
        }
    }

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

        AllocateGroup(FixedGrid, checked(countX * countY * countZ));
        _gridMin = min;
        _gridCount = new Int3(countX, countY, countZ);
        _gridSpacing = spacing;
        var positions = _position[FixedGrid]!;
        for (int z = 0; z < countZ; z++)
        {
            for (int y = 0; y < countY; y++)
            {
                for (int x = 0; x < countX; x++)
                {
                    positions[x + countX * (y + countY * z)] = min + new Vector3(x, y, z) * spacing;
                }
            }
        }
    }

    /// <summary>Takes away the fixed grid, for a view that no longer has one.</summary>
    public void ClearFixedGrid()
    {
        AllocateGroup(FixedGrid, 0);
        _gridSpacing = 0f;
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
            if (_state[c] == null)
            {
                if (!scroll.Teleported(c))
                {
                    throw new InvalidOperationException(
                        $"Cascade {c} has not been scrolled before, so its scroll must be a whole new box (the first Move).");
                }

                AllocateGroup(c, slots.Count);
            }

            var state = _state[c]!;
            var updated = _updated[c]!;
            var position = _position[c]!;
            for (int i = 0; i < slots.Count; i++)
            {
                int slot = slots[i];
                if (state[slot] == Clean)
                {
                    _pending[c]++;
                }

                state[slot] = NeverCaptured;
                List(c, slot);
                updated[slot] = 0;
                position[slot] = _cascades.Position(c, slot);
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
            if (_state[c] == null)
            {
                continue;
            }

            var layout = _cascades.Layout(c);
            var origin = _cascades.Origin(c);
            var x = Overlap(center.X, range, 0f, layout.Spacing, origin.X, layout.CountX);
            var y = Overlap(center.Y, range, 0f, layout.Spacing, origin.Y, layout.CountY);
            var z = Overlap(center.Z, range, 0f, layout.Spacing, origin.Z, layout.CountZ);
            for (int cz = z.First; cz <= z.Last; cz++)
            {
                for (int cy = y.First; cy <= y.Last; cy++)
                {
                    for (int cx = x.First; cx <= x.Last; cx++)
                    {
                        MarkIfInRange(c, ProbeCascades.Wrap(new Int3(cx, cy, cz), layout), center, range);
                    }
                }
            }
        }

        if (_gridSpacing > 0f)
        {
            var x = Overlap(center.X, range, _gridMin.X, _gridSpacing, 0, _gridCount.X);
            var y = Overlap(center.Y, range, _gridMin.Y, _gridSpacing, 0, _gridCount.Y);
            var z = Overlap(center.Z, range, _gridMin.Z, _gridSpacing, 0, _gridCount.Z);
            for (int gz = z.First; gz <= z.Last; gz++)
            {
                for (int gy = y.First; gy <= y.Last; gy++)
                {
                    for (int gx = x.First; gx <= x.Last; gx++)
                    {
                        MarkIfInRange(FixedGrid, gx + _gridCount.X * (gy + _gridCount.Y * gz), center, range);
                    }
                }
            }
        }
        else
        {
            // A grid whose positions were never set has every probe at the origin, so check each one.
            var state = _state[FixedGrid]!;
            for (int slot = 0; slot < state.Length; slot++)
            {
                MarkIfInRange(FixedGrid, slot, center, range);
            }
        }
    }

    private void MarkIfInRange(int group, int slot, Vector3 center, float range)
    {
        var state = _state[group]!;
        if (state[slot] == Clean && Vector3.DistanceSquared(_position[group]![slot], center) <= range * range)
        {
            state[slot] = Dirty;
            List(group, slot);
            _pending[group]++;
        }
    }

    // The indices first..last, within [start, start + count), whose positions offset + index * spacing are within range
    // of center along one axis. Empty (first > last) when none are.
    private static (int First, int Last) Overlap(float center, float range, float offset, float spacing, int start, int count)
    {
        float low = Math.Clamp(MathF.Ceiling((center - range - offset) / spacing), start, start + count);
        float high = Math.Clamp(MathF.Floor((center + range - offset) / spacing), start - 1, start + count - 1);
        return ((int)low, (int)high);
    }

    /// <summary>The sun, sky or environment changed: every captured probe is dirty, but its value stays in use.</summary>
    public void EverythingChanged()
    {
        for (int group = 0; group <= FixedGrid; group++)
        {
            var state = _state[group];
            if (state == null)
            {
                continue;
            }

            for (int slot = 0; slot < state.Length; slot++)
            {
                if (state[slot] == Clean)
                {
                    state[slot] = Dirty;
                    List(group, slot);
                    _pending[group]++;
                }
            }
        }
    }

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
        _batch.Clear();

        long probes = 0;
        bool pending = false;
        for (int group = 0; group <= FixedGrid; group++)
        {
            probes += _state[group]?.Length ?? 0;
            pending |= _pending[group] > 0;
        }

        if (pending)
        {
            _refreshesSinceChange = 0;
        }

        bool bounced = _refreshesSinceChange >= BounceRefreshes * probes;
        if (budget <= 0)
        {
            return _batch;
        }

        if (_heap.Length < budget)
        {
            _heap = new ProbeCandidate[budget];
            _nearHeap = new ProbeCandidate[budget];
        }

        int nearSize = 0;
        int size = 0;
        OfferUnsettled(budget, camera, forward, ref nearSize, ref size);

        // Clean probes come after every other, so they only fill what the rest leaves of the budget.
        int room = budget - size;
        if (Settled && bounced)
        {
            room = Math.Min(room, refreshBudget);
        }

        size += _captures.TakeOldest(_heap.AsSpan(size, Math.Max(room, 0)), Frame, camera, forward);

        // The heaps are rebuilt every call, so they can be sorted in place. The order is total, so any sort gives the
        // same result.
        var near = _nearHeap.AsSpan(0, nearSize);
        var rest = _heap.AsSpan(0, size);
        near.Sort();
        rest.Sort();

        // Never-captured probes sort first in the rest; up to a quarter of the budget is theirs whatever is dirty.
        int waiting = 0;
        while (waiting < rest.Length && rest[waiting].Tier == NeverCaptured)
        {
            waiting++;
        }

        int nearTaken = Math.Min(nearSize, budget - Math.Min(waiting, budget / 4));
        int restTaken = Math.Min(size, budget - nearTaken);

        // Clean probes sort last in the rest. Once the fine groups are settled and the bounce has had its refreshes,
        // only refreshBudget of them are taken.
        if (Settled && bounced && refreshBudget < restTaken)
        {
            int unsettled = 0;
            while (unsettled < rest.Length && rest[unsettled].Tier != Clean)
            {
                unsettled++;
            }

            restTaken = Math.Min(restTaken, unsettled + refreshBudget);
        }

        foreach (var candidate in near[..nearTaken])
        {
            _batch.Add(new ProbeId(candidate.Group, candidate.Slot));
        }

        foreach (var candidate in rest[..restTaken])
        {
            _batch.Add(new ProbeId(candidate.Group, candidate.Slot));
        }

        return _batch;
    }

    // Offers every probe that isn't clean to the heaps, and forgets the ones that have been captured since they were
    // listed.
    private void OfferUnsettled(int budget, Vector3 camera, Vector3 forward, ref int nearSize, ref int size)
    {
        int kept = 0;
        for (int i = 0; i < _unsettled.Count; i++)
        {
            var id = _unsettled[i];
            byte state = _state[id.Cascade]![id.Slot];
            if (state == Clean)
            {
                _listed[id.Cascade]![id.Slot] = false;
                continue;
            }

            _unsettled[kept++] = id;
            float rank = ProbeCandidate.Rank(_position[id.Cascade]![id.Slot], id.Cascade, camera, forward);
            var candidate = new ProbeCandidate(state, 0, rank, id.Cascade, id.Slot);
            bool fine = id.Cascade <= 1 || id.Cascade == FixedGrid;
            if (fine && state == Dirty)
            {
                ProbeCandidate.Offer(_nearHeap, ref nearSize, budget, candidate);
            }
            else
            {
                ProbeCandidate.Offer(_heap, ref size, budget, candidate);
            }
        }

        _unsettled.RemoveRange(kept, _unsettled.Count - kept);
    }

    private void List(int group, int slot)
    {
        var listed = _listed[group]!;
        if (!listed[slot])
        {
            listed[slot] = true;
            _unsettled.Add(new ProbeId(group, slot));
        }
    }

    /// <summary>The batch has been captured: its probes are clean, and as old as this frame.</summary>
    public void Updated(IReadOnlyList<ProbeId> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        for (int i = 0; i < batch.Count; i++)
        {
            var id = batch[i];
            if (!Exists(id))
            {
                throw new ArgumentOutOfRangeException(nameof(batch), id, "There is no such probe.");
            }

            var state = _state[id.Cascade]!;
            var updated = _updated[id.Cascade]!;
            if (state[id.Slot] != Clean)
            {
                _pending[id.Cascade]--;
            }
            else
            {
                _refreshesSinceChange++;
            }

            // A probe captured twice in a frame has its capture recorded already, even if it went dirty between.
            if (state[id.Slot] == NeverCaptured || updated[id.Slot] != Frame)
            {
                _captures.Record(Frame, id.Cascade, id.Slot);
            }

            state[id.Slot] = Clean;
            updated[id.Slot] = Frame;
        }
    }

    /// <summary>True when the probe has a value in use: it has been captured, whether or not it is dirty now.</summary>
    public bool Captured(ProbeId id)
    {
        return Exists(id) && _state[id.Cascade]![id.Slot] != NeverCaptured;
    }

    /// <summary>True when the probe has been captured but the lighting changed since, so it waits to be updated.</summary>
    internal bool IsDirty(ProbeId id)
    {
        return Exists(id) && _state[id.Cascade]![id.Slot] == Dirty;
    }

    private bool Exists(ProbeId id)
    {
        if (id.Cascade < 0 || id.Cascade > FixedGrid)
        {
            return false;
        }

        var state = _state[id.Cascade];
        return state != null && id.Slot >= 0 && id.Slot < state.Length;
    }

    private void AllocateGroup(int group, int count)
    {
        // What was known about the group's old probes goes with them.
        _unsettled.RemoveAll(id => id.Cascade == group);
        _captures.Forget(group);

        // Every new probe starts never captured.
        _state[group] = new byte[count];
        _updated[group] = new long[count];
        _position[group] = new Vector3[count];
        _listed[group] = new bool[count];
        _pending[group] = count;

        int probes = 0;
        for (int g = 0; g <= FixedGrid; g++)
        {
            probes += _state[g]?.Length ?? 0;
        }

        _unsettled.EnsureCapacity(probes);
        _captures.Reserve(probes);

        for (int slot = 0; slot < count; slot++)
        {
            List(group, slot);
        }
    }

}
