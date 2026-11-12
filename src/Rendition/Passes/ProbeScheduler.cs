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
/// never-captured probes first, then dirty ones, then the probes that were updated longest ago.
/// In the first two tiers nearer probes go first, and so do finer cascades and probes in front of the
/// camera. In the last tier the oldest goes first, whatever its place, which bounds the time between
/// two updates of any probe to about (probes / budget) frames; distance only breaks ties.
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
    public void ClearFixedGrid() => AllocateGroup(FixedGrid, 0);

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

    /// <summary>A light moved, appeared or went out: captured probes within range of its centre are dirty.</summary>
    public void LightChanged(Vector3 center, float range)
    {
        float limit = range * range;
        for (int group = 0; group <= FixedGrid; group++)
        {
            var state = _state[group];
            if (state == null)
            {
                continue;
            }

            var position = _position[group]!;
            for (int slot = 0; slot < state.Length; slot++)
            {
                if (state[slot] == Clean && Vector3.DistanceSquared(position[slot], center) <= limit)
                {
                    state[slot] = Dirty;
                    _pending[group]++;
                }
            }
        }
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
    /// scheduler's own and the next call refills it, so copy it to keep it.
    /// </summary>
    public IReadOnlyList<ProbeId> Next(int budget, Vector3 camera, Vector3 forward)
    {
        Frame++;
        _batch.Clear();
        if (budget <= 0)
        {
            return _batch;
        }

        if (_heap.Length < budget)
        {
            _heap = new Candidate[budget];
        }

        var heap = _heap;
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
        }

        // The heap is rebuilt every call, so it can be sorted in place. The order is total, so any sort gives the
        // same result.
        var chosen = heap.AsSpan(0, size);
        chosen.Sort();
        foreach (var candidate in chosen)
        {
            _batch.Add(new ProbeId(candidate.Group, candidate.Slot));
        }

        return _batch;
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

            state[id.Slot] = Clean;
            _updated[id.Cascade]![id.Slot] = Frame;
        }
    }

    /// <summary>True when the probe has a value in use: it has been captured, whether or not it is dirty now.</summary>
    public bool Captured(ProbeId id)
    {
        return Exists(id) && _state[id.Cascade]![id.Slot] != NeverCaptured;
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
