using System.Buffers;
using System.Numerics;

namespace Rendition.Passes;

/// <summary>
/// Puts a pass's draws in the order they're drawn in, with draws that look the same next to each other, so each
/// look goes to the GPU as one instanced draw.
/// </summary>
internal static class DrawOrder
{
    private const int NoGroup = -1;

    /// <summary>
    /// The draws of one look, and what orders the looks: the mesh first, so one mesh's parts stay together and it's
    /// bound once, then the part, so a mesh's parts are drawn in the order the mesh holds them, which is the order a
    /// model was made to be drawn in and costs the least overdraw, then the rest of the look mixed into one number,
    /// then where the look first came in the list. Nothing here changes from run to run, so neither does the order.
    /// </summary>
    private struct Look : IComparable<Look>
    {
        public uint Mesh;
        public uint FirstIndex;
        public ulong Mixed;

        /// <summary>The place in the order of the first draw with this look, which the rest are compared with.</summary>
        public int First;
        public int Count;

        public readonly int CompareTo(Look other)
        {
            if (Mesh != other.Mesh)
            {
                return Mesh < other.Mesh ? -1 : 1;
            }

            if (FirstIndex != other.FirstIndex)
            {
                return FirstIndex < other.FirstIndex ? -1 : 1;
            }

            if (Mixed != other.Mixed)
            {
                return Mixed < other.Mixed ? -1 : 1;
            }

            return First.CompareTo(other.First);
        }
    }

    /// <summary>
    /// Reorders <paramref name="order"/>, the indices of the draws to make, so that draws that look the same meet,
    /// in the order they came in, and writes each look's run of them into <paramref name="batches"/>.
    /// <para>
    /// No draw is compared with another to sort them. Each finds its look in a small table, by its look mixed into a
    /// number, and is counted there; only the looks are then sorted, and there are few of them, a shoal of thousands
    /// being one. Each draw then goes straight to its place in its look's run.
    /// </para>
    /// </summary>
    public static void Group(ReadOnlySpan<DrawItem> draws, Span<int> order, List<DrawBatch> batches)
    {
        batches.Clear();
        int count = order.Length;
        if (count == 0)
        {
            return;
        }

        // The table has at least twice as many slots as there can be looks, so a search in it is short.
        int slots = (int)BitOperations.RoundUpToPowerOf2((uint)count * 2);
        int[] table = ArrayPool<int>.Shared.Rent(slots);
        int[] lookOf = ArrayPool<int>.Shared.Rent(count);
        int[] placed = ArrayPool<int>.Shared.Rent(count);
        Look[] looks = ArrayPool<Look>.Shared.Rent(count);
        table.AsSpan(0, slots).Fill(NoGroup);
        int lookCount = Count(draws, order, new LookTable(table.AsSpan(0, slots), looks), lookOf);

        // Sorting the looks moves them, so each draw finds where its look's run starts by the look's old number.
        int[] starts = table;
        var sorted = looks.AsSpan(0, lookCount);
        for (int look = 0; look < lookCount; look++)
        {
            // Until the runs are laid out, the look's old number rides in the slot its start will take.
            starts[look] = look;
        }

        sorted.Sort(starts.AsSpan(0, lookCount));
        LayOut(sorted, starts.AsSpan(0, lookCount), batches);
        for (int place = 0; place < count; place++)
        {
            placed[starts[lookOf[place]]++] = order[place];
        }

        placed.AsSpan(0, count).CopyTo(order);
        ArrayPool<Look>.Shared.Return(looks);
        ArrayPool<int>.Shared.Return(placed);
        ArrayPool<int>.Shared.Return(lookOf);
        ArrayPool<int>.Shared.Return(table);
    }

    /// <summary>
    /// Whether two draws differ only in what each copy has of its own, where it stands and how it swims, so they can
    /// share an instanced draw. A skinned draw shares with nothing, since its skin is its own.
    /// </summary>
    public static bool SameLook(in DrawItem a, in DrawItem b) =>
        a.Bones.Count == 0 && b.Bones.Count == 0
        && a.Mesh == b.Mesh
        && a.FirstIndex == b.FirstIndex
        && a.IndexCount == b.IndexCount
        && a.Material == b.Material
        && a.Tint == b.Tint
        && a.Ghost == b.Ghost
        && (a.Dither != 0f) == (b.Dither != 0f);

    // Gives each sorted look its run, one after another, as a batch, and turns `oldNumbers`, which holds each
    // sorted look's number from before the sort, into where the run of the look with that old number starts.
    private static void LayOut(ReadOnlySpan<Look> sorted, Span<int> oldNumbers, List<DrawBatch> batches)
    {
        Span<int> firsts = sorted.Length <= 256 ? stackalloc int[sorted.Length] : new int[sorted.Length];
        int next = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            batches.Add(new DrawBatch(next, sorted[i].Count));
            firsts[oldNumbers[i]] = next;
            next += sorted[i].Count;
        }

        firsts.CopyTo(oldNumbers);
    }

    // Finds each draw's look, counting the draws of each, writes the look of the draw at each place into `lookOf`,
    // and returns how many looks there are.
    private static int Count(ReadOnlySpan<DrawItem> draws, ReadOnlySpan<int> order, LookTable table, Span<int> lookOf)
    {
        int lookCount = 0;
        for (int place = 0; place < order.Length; place++)
        {
            ref readonly var draw = ref draws[order[place]];
            // A skinned draw shares with nothing, so its place is mixed in, or a crowd of one skinned model would
            // all land on one slot and each search the whole crowd.
            ulong mixed = draw.Bones.Count == 0 ? Mixed(draw) : Mix(Mixed(draw), (uint)place);
            int look = table.Find(draws, order, mixed, draw);
            if (look == NoGroup)
            {
                look = lookCount++;
                table.Looks[look] = new Look { Mesh = (uint)draw.Mesh.Index, FirstIndex = draw.FirstIndex, Mixed = mixed, First = place };
                table.Claim(mixed, look);
            }

            table.Looks[look].Count++;
            lookOf[place] = look;
        }

        return lookCount;
    }

    /// <summary>The looks found so far, and the slots that find a look by its mixed number.</summary>
    private readonly ref struct LookTable(Span<int> slots, Span<Look> looks)
    {
        private readonly Span<int> _slots = slots;

        public readonly Span<Look> Looks = looks;

        // The look in the table that `draw` shares, or NoGroup. Two looks can mix to the same number, so the draw is
        // compared with the look's first draw before it joins it.
        public int Find(ReadOnlySpan<DrawItem> draws, ReadOnlySpan<int> order, ulong mixed, in DrawItem draw)
        {
            int mask = _slots.Length - 1;
            for (int slot = (int)(mixed >> 32) & mask; _slots[slot] != NoGroup; slot = (slot + 1) & mask)
            {
                ref readonly var look = ref Looks[_slots[slot]];
                if (look.Mixed == mixed && SameLook(draws[order[look.First]], draw))
                {
                    return _slots[slot];
                }
            }

            return NoGroup;
        }

        public void Claim(ulong mixed, int look)
        {
            int mask = _slots.Length - 1;
            int slot = (int)(mixed >> 32) & mask;
            while (_slots[slot] != NoGroup)
            {
                slot = (slot + 1) & mask;
            }

            _slots[slot] = look;
        }
    }

    // Everything SameLook compares, mixed into one number.
    private static ulong Mixed(in DrawItem draw)
    {
        ulong look = Mix(Mix((uint)draw.Mesh.Index, draw.FirstIndex), draw.IndexCount);
        look = Mix(look, (uint)draw.Material.Index);
        if (draw.Dither != 0f)
        {
            look = Mix(look, 1);
        }

        if (draw.Tint is { } tint)
        {
            look = Mix(look, Bits(tint.X));
            look = Mix(look, Bits(tint.Y));
            look = Mix(look, Bits(tint.Z) + 1);
        }

        if (draw.Ghost is { } ghost)
        {
            look = Mix(look, Bits(ghost.Color.X));
            look = Mix(look, Bits(ghost.Color.Y));
            look = Mix(look, Bits(ghost.Color.Z));
            look = Mix(look, Bits(ghost.Opacity) + 1);
        }

        return look;
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    // Folds one more value into a running hash, by a multiply that carries every bit towards the top.
    private static ulong Mix(ulong hash, uint value) => (hash ^ value) * 0x9E3779B97F4A7C15ul;
}
