using System.Numerics;

namespace Rendition;

/// <summary>
/// Picks a frame's batch for <see cref="ProbeScheduler"/>: the best of the probes that aren't clean, each turned into
/// one number and the lowest of them brought forward, then the clean probes updated longest ago from the capture
/// queue. It never looks at every probe, only the ones that wait, and once its arrays have grown it allocates
/// nothing: it hands back the same list each call.
/// </summary>
internal sealed class ProbeBatchPicker(ProbeStates states)
{
    // The dirty probes of the fine groups, and all the rest that wait, as the numbers of ProbeKeys, so choosing
    // among them compares numbers.
    private ulong[] _nearKeys = Array.Empty<ulong>();
    private ulong[] _restKeys = Array.Empty<ulong>();

    // The best of the rest as candidates, with room after them for the clean probes that fill what they leave.
    private ProbeCandidate[] _rest = Array.Empty<ProbeCandidate>();
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

        if (_rest.Length < budget)
        {
            _rest = new ProbeCandidate[budget];
        }

        var (nearCount, restCount) = NumberUnsettled(camera, forward);
        int nearSize = ProbeKeys.BringLowestForward(_nearKeys.AsSpan(0, nearCount), budget);
        int size = ProbeKeys.BringLowestForward(_restKeys.AsSpan(0, restCount), budget);
        for (int i = 0; i < size; i++)
        {
            _rest[i] = ProbeKeys.Candidate(_restKeys[i]);
        }

        // Clean probes come after every other, so they only fill what the rest leaves of the budget.
        int room = Math.Min(budget - size, refreshes);
        size += states.CaptureQueue.TakeOldest(_rest.AsSpan(size, Math.Max(room, 0)), frame, camera, forward);

        // The numbers are made again every call, so they can be sorted in place. The order is total, so any sort
        // gives the same result.
        var near = _nearKeys.AsSpan(0, nearSize);
        var rest = _rest.AsSpan(0, size);
        near.Sort();
        rest.Sort();
        FillBatch(near, rest, budget, refreshes);
        return _batch;
    }

    // Gives every probe that isn't clean its number for this frame: dirty probes of the fine groups among the near
    // numbers, the rest among the others. Returns how many of each.
    private (int Near, int Others) NumberUnsettled(Vector3 camera, Vector3 forward)
    {
        var waiting = states.Waiting;
        var ids = waiting.Ids;
        var positions = waiting.Positions;
        var tiers = waiting.Tiers;
        if (_restKeys.Length < ids.Length)
        {
            _restKeys = new ulong[waiting.Room];
            _nearKeys = new ulong[waiting.Room];
        }

        int near = 0;
        int rest = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            float rank = ProbeCandidate.Rank(positions[i], id.Cascade, camera, forward);
            ulong key = ProbeKeys.Of(tiers[i], rank, id.Cascade, id.Slot);
            bool fine = id.Cascade <= 1 || id.Cascade == ProbeStates.GridGroup;
            if (fine && tiers[i] == ProbeStates.Dirty)
            {
                _nearKeys[near++] = key;
            }
            else
            {
                _restKeys[rest++] = key;
            }
        }

        return (near, rest);
    }

    // Takes the best of the sorted probes into the batch: the near dirty probes, then the rest, within the budget and
    // with at most `refreshes` clean probes.
    private void FillBatch(ReadOnlySpan<ulong> near, ReadOnlySpan<ProbeCandidate> rest, int budget, int refreshes)
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

        foreach (ulong key in near[..nearTaken])
        {
            _batch.Add(ProbeKeys.Id(key));
        }

        foreach (var candidate in rest[..restTaken])
        {
            _batch.Add(new ProbeId(candidate.Group, candidate.Slot));
        }
    }
}
