using System.Numerics;

namespace Rendition;

/// <summary>
/// The probes that aren't clean, each once, with what a frame needs to choose among them, side by side: which probe,
/// where it is and its tier. So a frame reads these arrays from end to end, and never every probe nor anything else
/// about the ones it reads. A probe leaves the moment it is captured, by the last one taking its place, so the list
/// has no order.
/// </summary>
internal sealed class ProbeWaitList(int groups)
{
    private ProbeId[] _ids = [];
    private Vector3[] _positions = [];
    private byte[] _tiers = [];

    // Per group and slot, one more than the probe's place in the list, or 0 when it isn't listed.
    private readonly int[]?[] _place = new int[]?[groups];

    public int Count { get; private set; }

    /// <summary>How many probes the list holds without growing.</summary>
    public int Room => _ids.Length;

    public ReadOnlySpan<ProbeId> Ids => _ids.AsSpan(0, Count);

    public ReadOnlySpan<Vector3> Positions => _positions.AsSpan(0, Count);

    public ReadOnlySpan<byte> Tiers => _tiers.AsSpan(0, Count);

    /// <summary>Lists a probe, or when it is listed already, changes its tier and where it is.</summary>
    public void Enlist(int group, int slot, byte tier, Vector3 position)
    {
        int[] places = _place[group]!;
        int at = places[slot] - 1;
        if (at < 0)
        {
            ReserveFor(Count + 1);
            at = Count++;
            places[slot] = at + 1;
            _ids[at] = new ProbeId(group, slot);
        }

        _tiers[at] = tier;
        _positions[at] = position;
    }

    /// <summary>Takes a probe off the list, if it is on it: it has been captured.</summary>
    public void Delist(int group, int slot)
    {
        int[] places = _place[group]!;
        int at = places[slot] - 1;
        if (at < 0)
        {
            return;
        }

        places[slot] = 0;
        int last = --Count;
        if (at != last)
        {
            Move(last, at);
        }
    }

    /// <summary>Drops a group's probes, which are being replaced by <paramref name="count"/> new ones, none listed yet.</summary>
    public void DropGroup(int group, int count)
    {
        int kept = 0;
        for (int i = 0; i < Count; i++)
        {
            if (_ids[i].Cascade == group)
            {
                continue;
            }

            if (kept != i)
            {
                Move(i, kept);
            }

            kept++;
        }

        Count = kept;
        _place[group] = new int[count];
    }

    /// <summary>Makes room to list <paramref name="probes"/> probes without growing.</summary>
    public void ReserveFor(int probes)
    {
        if (_ids.Length >= probes)
        {
            return;
        }

        int room = Math.Max(probes, _ids.Length * 2);
        Array.Resize(ref _ids, room);
        Array.Resize(ref _positions, room);
        Array.Resize(ref _tiers, room);
    }

    private void Move(int from, int to)
    {
        var id = _ids[from];
        _ids[to] = id;
        _positions[to] = _positions[from];
        _tiers[to] = _tiers[from];
        _place[id.Cascade]![id.Slot] = to + 1;
    }
}
