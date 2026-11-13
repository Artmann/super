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
/// <see cref="Next"/> is one pass over every probe that keeps the best <c>budget</c> in a small
/// binary heap, so it costs O(probes + budget log budget) with no sort of all probes. Once its
/// buffers have grown to the budget, nothing on the per-frame path allocates: <see cref="Next"/>
/// hands back the same list each call, and <see cref="Settled"/> reads counts kept as probes change
/// state rather than scanning them.
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

    // Squared distances are scaled by (1 + cascade / 2) squared, so a finer cascade beats a coarser
    // one at the same distance, and by 0.75 squared for a probe in front of the camera. The fixed grid
    // wins inside its box, so it counts as the finest.
    private static readonly float[] CascadeScale = { 1f, 2.25f, 4f, 6.25f, 1f };
    private const float FrontScale = 0.5625f;

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
    private Candidate[] _nearHeap = Array.Empty<Candidate>();
    private Candidate[] _heap = Array.Empty<Candidate>();
    private readonly List<ProbeId> _batch = [];

    public ProbeScheduler(ProbeCascades cascades, int fixedGridProbes)
    {
        ArgumentNullException.ThrowIfNull(cascades);
        ArgumentOutOfRangeException.ThrowIfNegative(fixedGridProbes);

        _cascades = cascades;
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
            _heap = new Candidate[budget];
            _nearHeap = new Candidate[budget];
        }

        int nearSize = 0;
        int size = 0;
        bool facing = forward != Vector3.Zero;

        for (int group = 0; group <= FixedGrid; group++)
        {
            var state = _state[group];
            if (state == null)
            {
                continue;
            }

            var updated = _updated[group]!;
            var position = _position[group]!;
            float scale = CascadeScale[group];
            bool fine = group <= 1 || group == FixedGrid;

            for (int slot = 0; slot < state.Length; slot++)
            {
                var offset = position[slot] - camera;
                float distance = offset.LengthSquared() * scale;
                if (facing && Vector3.Dot(offset, forward) > 0f)
                {
                    distance *= FrontScale;
                }

                var candidate = new Candidate(
                    state[slot],
                    state[slot] == Clean ? Frame - updated[slot] : 0,
                    distance,
                    group,
                    slot);

                if (fine && state[slot] == Dirty)
                {
                    Offer(_nearHeap, ref nearSize, budget, candidate);
                }
                else
                {
                    Offer(_heap, ref size, budget, candidate);
                }
            }
        }

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

    // Keeps the best `budget` candidates offered so far in a heap whose root is the worst of them.
    private static void Offer(Candidate[] heap, ref int size, int budget, in Candidate candidate)
    {
        if (size < budget)
        {
            heap[size] = candidate;
            SiftUp(heap, size);
            size++;
        }
        else if (candidate.Before(heap[0]))
        {
            heap[0] = candidate;
            SiftDown(heap, size);
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
            if (state[id.Slot] != Clean)
            {
                _pending[id.Cascade]--;
            }
            else
            {
                _refreshesSinceChange++;
            }

            state[id.Slot] = Clean;
            _updated[id.Cascade]![id.Slot] = Frame;
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
        // Every new probe starts never captured.
        _state[group] = new byte[count];
        _updated[group] = new long[count];
        _position[group] = new Vector3[count];
        _pending[group] = count;
    }

    // The heap's root is the worst probe kept so far, so a new probe is compared with the root only.
    private static void SiftUp(Candidate[] heap, int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (!heap[parent].Before(heap[index]))
            {
                return;
            }

            (heap[parent], heap[index]) = (heap[index], heap[parent]);
            index = parent;
        }
    }

    private static void SiftDown(Candidate[] heap, int size)
    {
        int index = 0;
        while (true)
        {
            int worst = index;
            int left = index * 2 + 1;
            int right = left + 1;

            if (left < size && heap[worst].Before(heap[left]))
            {
                worst = left;
            }

            if (right < size && heap[worst].Before(heap[right]))
            {
                worst = right;
            }

            if (worst == index)
            {
                return;
            }

            (heap[worst], heap[index]) = (heap[index], heap[worst]);
            index = worst;
        }
    }

    private readonly struct Candidate(byte tier, long age, float distance, int group, int slot) : IComparable<Candidate>
    {
        public readonly byte Tier = tier;
        public readonly long Age = age;
        public readonly float Distance = distance;
        public readonly int Group = group;
        public readonly int Slot = slot;

        // A total order, so the same probes always come out in the same order: the tier, then (for clean
        // probes) the oldest, then the nearest, then the finest cascade, then the lowest slot.
        public bool Before(Candidate other)
        {
            if (Tier != other.Tier)
            {
                return Tier < other.Tier;
            }

            if (Age != other.Age)
            {
                return Age > other.Age;
            }

            if (Distance != other.Distance)
            {
                return Distance < other.Distance;
            }

            if (Group != other.Group)
            {
                return Group < other.Group;
            }

            return Slot < other.Slot;
        }

        // Sorting by IComparable needs no comparer, so it makes no delegate; a sort given a comparer would.
        public int CompareTo(Candidate other)
        {
            if (Before(other))
            {
                return -1;
            }

            return other.Before(this) ? 1 : 0;
        }
    }
}
