using System.Numerics;

namespace Rendition;

/// <summary>
/// A probe <see cref="ProbeScheduler"/> may update this frame, with what orders it against the others: its tier
/// (never captured, dirty or clean), the frames since its last update, and its scaled distance from the camera.
/// </summary>
internal readonly struct ProbeCandidate(byte tier, long age, float distance, int group, int slot) : IComparable<ProbeCandidate>
{
    // Squared distances are scaled by (1 + cascade / 2) squared, so a finer cascade beats a coarser
    // one at the same distance, and by 0.75 squared for a probe in front of the camera. The fixed grid
    // wins inside its box, so it counts as the finest.
    private static readonly float[] CascadeScale = { 1f, 2.25f, 4f, 6.25f, 1f };
    private const float FrontScale = 0.5625f;

    public readonly byte Tier = tier;
    public readonly long Age = age;
    public readonly float Distance = distance;
    public readonly int Group = group;
    public readonly int Slot = slot;

    // A total order, so the same probes always come out in the same order: the tier, then (for clean
    // probes) the oldest, then the nearest, then the finest cascade, then the lowest slot.
    public bool Before(ProbeCandidate other)
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
    public int CompareTo(ProbeCandidate other)
    {
        if (Before(other))
        {
            return -1;
        }

        return other.Before(this) ? 1 : 0;
    }

    /// <summary>
    /// A probe's squared distance from the camera, scaled so finer cascades and probes in front of the camera come
    /// first. No scaling for the camera's side when <paramref name="forward"/> is zero.
    /// </summary>
    public static float Rank(Vector3 position, int group, Vector3 camera, Vector3 forward)
    {
        var offset = position - camera;
        float distance = offset.LengthSquared() * CascadeScale[group];
        if (forward != Vector3.Zero && Vector3.Dot(offset, forward) > 0f)
        {
            distance *= FrontScale;
        }

        return distance;
    }

    /// <summary>Keeps the best <paramref name="budget"/> candidates offered so far in a heap whose root is the worst of them.</summary>
    public static void Offer(ProbeCandidate[] heap, ref int size, int budget, in ProbeCandidate candidate)
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

    // The heap's root is the worst probe kept so far, so a new probe is compared with the root only.
    private static void SiftUp(ProbeCandidate[] heap, int index)
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

    private static void SiftDown(ProbeCandidate[] heap, int size)
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
}
