using System.Numerics;

namespace Rendition;

/// <summary>
/// Picks a frame's batch for <see cref="ProbeScheduler"/>: the best of the probes that aren't clean, kept to the
/// budget in two small binary heaps, then the clean probes updated longest ago from the capture queue. It never looks
/// at every probe, and once its heaps have grown it allocates nothing: it hands back the same list each call.
/// </summary>
internal sealed class ProbeBatchPicker(ProbeStates states)
{
    // The best dirty probes of the fine groups, and the best of all the rest, each kept to the budget.
    private ProbeCandidate[] _nearHeap = Array.Empty<ProbeCandidate>();
    private ProbeCandidate[] _heap = Array.Empty<ProbeCandidate>();
    private readonly List<ProbeId> _batch = [];

    /// <summary>
    /// The probes to update in <paramref name="frame"/>, best first: never more than <paramref name="budget"/>, none
    /// twice, and at most <paramref name="refreshes"/> of them clean probes refreshed for their age. The list is the
    /// picker's own and the next call refills it.
    /// </summary>
    public IReadOnlyList<ProbeId> Pick(long frame, int budget, Vector3 camera, Vector3 forward, int refreshes)
    {
        _batch.Clear();
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
        int room = Math.Min(budget - size, refreshes);
        size += states.CaptureQueue.TakeOldest(_heap.AsSpan(size, Math.Max(room, 0)), frame, camera, forward);

        // The heaps are rebuilt every call, so they can be sorted in place. The order is total, so any sort gives the
        // same result.
        var near = _nearHeap.AsSpan(0, nearSize);
        var rest = _heap.AsSpan(0, size);
        near.Sort();
        rest.Sort();
        FillBatch(near, rest, budget, refreshes);
        return _batch;
    }

    // Offers every probe that isn't clean to the heaps: dirty probes of the fine groups to the near heap, the rest to
    // the other.
    private void OfferUnsettled(int budget, Vector3 camera, Vector3 forward, ref int nearSize, ref int size)
    {
        foreach (var id in states.Unsettled())
        {
            var candidate = states.CandidateFor(id, camera, forward);
            bool fine = id.Cascade <= 1 || id.Cascade == ProbeStates.GridGroup;
            if (fine && candidate.Tier == ProbeStates.Dirty)
            {
                ProbeCandidate.Offer(_nearHeap, ref nearSize, budget, candidate);
            }
            else
            {
                ProbeCandidate.Offer(_heap, ref size, budget, candidate);
            }
        }
    }

    // Takes the best of the sorted heaps into the batch: the near dirty probes, then the rest, within the budget and
    // with at most `refreshes` clean probes.
    private void FillBatch(ReadOnlySpan<ProbeCandidate> near, ReadOnlySpan<ProbeCandidate> rest, int budget, int refreshes)
    {
        // Never-captured probes sort first in the rest; up to a quarter of the budget is theirs whatever is dirty.
        int waiting = ProbeCandidate.Ahead(rest, ProbeStates.Dirty);
        int nearTaken = Math.Min(near.Length, budget - Math.Min(waiting, budget / 4));
        int restTaken = Math.Min(rest.Length, budget - nearTaken);

        // Clean probes sort last in the rest.
        if (refreshes < restTaken)
        {
            restTaken = Math.Min(restTaken, ProbeCandidate.Ahead(rest, ProbeStates.Clean) + refreshes);
        }

        foreach (var candidate in near[..nearTaken])
        {
            _batch.Add(new ProbeId(candidate.Group, candidate.Slot));
        }

        foreach (var candidate in rest[..restTaken])
        {
            _batch.Add(new ProbeId(candidate.Group, candidate.Slot));
        }
    }
}
