namespace Rendition;

/// <summary>
/// A probe that isn't clean as one number, which orders it against the others as <see cref="ProbeCandidate"/> does,
/// lower first: from the top its tier, its scaled distance from the camera, its group and its slot. A probe that isn't
/// clean has no age to order by. A frame may have tens of thousands of them to choose a few from, so choosing
/// compares numbers and never builds a candidate for a probe it doesn't pick.
/// </summary>
internal static class ProbeKeys
{
    private const int TierShift = 62;
    private const int DistanceShift = 30;
    private const int GroupShift = 27;
    private const ulong SlotMask = (1ul << GroupShift) - 1;
    private const int GroupMask = 7;

    /// <summary>
    /// The probe's number. A distance is never negative, and the bits of such a float order as the float does.
    /// </summary>
    public static ulong Of(byte tier, float distance, int group, int slot) =>
        ((ulong)tier << TierShift)
        | ((ulong)BitConverter.SingleToUInt32Bits(distance) << DistanceShift)
        | ((ulong)group << GroupShift)
        | (uint)slot;

    public static ProbeId Id(ulong key) => new((int)(key >> GroupShift) & GroupMask, (int)(key & SlotMask));

    public static ProbeCandidate Candidate(ulong key)
    {
        var id = Id(key);
        float distance = BitConverter.UInt32BitsToSingle((uint)(key >> DistanceShift));
        return new ProbeCandidate((byte)(key >> TierShift), 0, distance, id.Cascade, id.Slot);
    }

    /// <summary>
    /// Brings the lowest <paramref name="budget"/> of the numbers to the front, in no order, and returns how many
    /// that is. It splits the numbers around one of them and goes on with the side the budget ends in, so it costs
    /// a few passes over them whatever order they came in. A heap that kept the best so far cost a sift for nearly
    /// every probe when they came farthest first, as a scrolled cascade lists them.
    /// </summary>
    public static int BringLowestForward(Span<ulong> keys, int budget)
    {
        if (keys.Length <= budget)
        {
            return keys.Length;
        }

        int low = 0;
        int high = keys.Length - 1;
        int last = budget - 1;
        while (low < high)
        {
            ulong pivot = Middle(keys[low], keys[low + (high - low) / 2], keys[high]);
            var (j, i) = Split(keys, low, high, pivot);
            if (last <= j)
            {
                high = j;
            }
            else if (last >= i)
            {
                low = i;
            }
            else
            {
                break;
            }
        }

        return budget;
    }

    // Splits the numbers from `low` to `high` around the pivot. Everything up to `Below` is then no higher than the
    // pivot and everything from `Above` no lower.
    private static (int Below, int Above) Split(Span<ulong> keys, int low, int high, ulong pivot)
    {
        int i = low;
        int j = high;
        while (i <= j)
        {
            while (keys[i] < pivot)
            {
                i++;
            }

            while (keys[j] > pivot)
            {
                j--;
            }

            if (i <= j)
            {
                (keys[i], keys[j]) = (keys[j], keys[i]);
                i++;
                j--;
            }
        }

        return (j, i);
    }

    private static ulong Middle(ulong a, ulong b, ulong c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
}
