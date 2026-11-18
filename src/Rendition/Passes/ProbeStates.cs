using System.Numerics;

namespace Rendition;

/// <summary>
/// What <see cref="ProbeScheduler"/> knows of each probe, per group (cascades 0 to 3, then the fixed grid) and slot:
/// whether it is never captured, dirty or clean, the frame it was last updated, and where it is. Alongside it keeps
/// the probes that aren't clean in a <see cref="ProbeWaitList"/>, every capture in order in a
/// <see cref="ProbeCaptureQueue"/>, and counts that change with the states, so nothing on the per-frame path scans
/// every probe.
/// </summary>
internal sealed class ProbeStates
{
    /// <summary>The group of the fixed grid, after the cascades.</summary>
    public const int GridGroup = ProbeCascades.Count;

    public const byte NeverCaptured = 0;
    public const byte Dirty = 1;
    public const byte Clean = 2;

    private const int Groups = GridGroup + 1;

    // The cascades' arrays are created by the first scroll, which is the only place their sizes are known.
    private readonly byte[]?[] _state = new byte[]?[Groups];
    private readonly long[]?[] _updated = new long[]?[Groups];
    private readonly Vector3[]?[] _position = new Vector3[]?[Groups];

    // Per group, how many probes are not clean (never captured or dirty), so FineSettled needn't scan.
    private readonly int[] _pending = new int[Groups];

    private readonly ProbeWaitList _waiting;

    // Clean probes refreshed since the last frame that had a never-captured or dirty probe in any group.
    private long _refreshesSinceChange;

    public ProbeStates()
    {
        _waiting = new ProbeWaitList(_state, Clean);
        CaptureQueue = new ProbeCaptureQueue(_state, _updated, _position, Clean);
    }

    /// <summary>Every capture in the order they were made, so the oldest clean probes are at the front.</summary>
    public ProbeCaptureQueue CaptureQueue { get; }

    /// <summary>True when every probe in cascades 0 and 1 and in the fixed grid has been captured since its last change.</summary>
    public bool FineSettled => _pending[0] == 0 && _pending[1] == 0 && _pending[GridGroup] == 0;

    /// <summary>How many probes a group has: 0 for a cascade that hasn't been scrolled yet.</summary>
    public int ProbesIn(int group) => _state[group]?.Length ?? 0;

    /// <summary>Replaces a group's probes with <paramref name="count"/> new ones, all never captured and at the origin.</summary>
    public void ReplaceGroup(int group, int count)
    {
        // What was known about the group's old probes goes with them.
        _waiting.DropGroup(group, count);
        CaptureQueue.Forget(group);

        // Every new probe starts never captured.
        _state[group] = new byte[count];
        _updated[group] = new long[count];
        _position[group] = new Vector3[count];
        _pending[group] = count;

        int probes = 0;
        for (int g = 0; g < Groups; g++)
        {
            probes += ProbesIn(g);
        }

        _waiting.ReserveFor(probes);
        CaptureQueue.Reserve(probes);

        for (int slot = 0; slot < count; slot++)
        {
            _waiting.Enlist(group, slot);
        }
    }

    /// <summary>The probe in a slot is now another probe, at <paramref name="position"/>, and never captured.</summary>
    public void MakeNew(int group, int slot, Vector3 position)
    {
        var state = _state[group]!;
        if (state[slot] == Clean)
        {
            _pending[group]++;
        }

        state[slot] = NeverCaptured;
        _waiting.Enlist(group, slot);
        _updated[group]![slot] = 0;
        _position[group]![slot] = position;
    }

    /// <summary>Makes the probe in a slot dirty when it is clean and within <paramref name="range"/> of <paramref name="center"/>.</summary>
    public void DirtyIfNear(int group, int slot, Vector3 center, float range)
    {
        var state = _state[group]!;
        if (state[slot] == Clean && Vector3.DistanceSquared(_position[group]![slot], center) <= range * range)
        {
            state[slot] = Dirty;
            _waiting.Enlist(group, slot);
            _pending[group]++;
        }
    }

    /// <summary>Makes every clean probe dirty.</summary>
    public void DirtyAll()
    {
        for (int group = 0; group < Groups; group++)
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
                    _waiting.Enlist(group, slot);
                    _pending[group]++;
                }
            }
        }
    }

    /// <summary>The probe, which must exist, was captured in <paramref name="frame"/>: it is clean and as old as that frame.</summary>
    public void MarkCaptured(ProbeId id, long frame)
    {
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
        if (state[id.Slot] == NeverCaptured || updated[id.Slot] != frame)
        {
            CaptureQueue.Record(frame, id.Cascade, id.Slot);
        }

        state[id.Slot] = Clean;
        updated[id.Slot] = frame;
    }

    /// <summary>The probe's state, or false when there is no such probe.</summary>
    public bool TryStateOf(ProbeId id, out byte state)
    {
        state = NeverCaptured;
        if (id.Cascade < 0 || id.Cascade >= Groups)
        {
            return false;
        }

        var states = _state[id.Cascade];
        if (states == null || id.Slot < 0 || id.Slot >= states.Length)
        {
            return false;
        }

        state = states[id.Slot];
        return true;
    }

    /// <summary>A probe that isn't clean as a candidate for this frame's batch, ranked by its distance from the camera.</summary>
    public ProbeCandidate CandidateFor(ProbeId id, Vector3 camera, Vector3 forward)
    {
        float rank = ProbeCandidate.Rank(_position[id.Cascade]![id.Slot], id.Cascade, camera, forward);
        return new ProbeCandidate(_state[id.Cascade]![id.Slot], 0, rank, id.Cascade, id.Slot);
    }

    /// <summary>
    /// Whether every probe has had <paramref name="refreshesPerProbe"/> refreshes since the last never-captured or dirty
    /// probe. A frame with such a probe starts the count again.
    /// </summary>
    public bool BounceBuiltUp(int refreshesPerProbe)
    {
        long probes = 0;
        bool pending = false;
        for (int group = 0; group < Groups; group++)
        {
            probes += ProbesIn(group);
            pending |= _pending[group] > 0;
        }

        if (pending)
        {
            _refreshesSinceChange = 0;
        }

        return _refreshesSinceChange >= refreshesPerProbe * probes;
    }

    /// <summary>The probes that aren't clean, after dropping the ones captured since they were listed.</summary>
    public ReadOnlySpan<ProbeId> Unsettled() => _waiting.DropCaptured();
}
