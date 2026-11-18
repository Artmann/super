using System.Numerics;

namespace Rendition;

/// <summary>
/// Shares the frame's four shadow mask slots among every bake it draws. Each bake has its own four channels, so a chunk
/// light's channel means nothing on another bake's surfaces; the lighting pass reads a light's frame slot instead, and
/// each bake's surfaces write their channels into the slots their lights hold. The space's Mixed sun keeps slot 0 and
/// its local lights keep their channels. Chunk lights take the free slots greedily, in order of
/// <see cref="LocalLight.Key"/>, so two whose world-space ranges overlap, or two of one placed chunk, never share one. See "Shadow mask channels"
/// in docs/design.md.
/// </summary>
internal static class MaskSlots
{
    // Marks a chunk light not yet given a slot, while Assign goes through them.
    private const int Waiting = -2;

    /// <summary>Fills slots per light, or -1, and the frame's unslotted lights. Allocation-free with the given scratch.</summary>
    /// <param name="spaceSun">The slot the space's Mixed sun holds, 0, or -1 when the space has none drawn.</param>
    /// <param name="spaceLights">The space's Mixed local lights with a channel in its bake. Each keeps it as its slot.</param>
    /// <param name="chunkLights">
    /// Every Mixed local light that has a channel in its own chunk's bake, with the index of the placed chunk that owns
    /// it, in world space. Each takes the first slot that neither the space, nor an earlier light of the same placed
    /// chunk, nor an earlier chunk light whose range overlaps it holds. The space's lights carry no position, so their
    /// slots are held everywhere.
    /// </param>
    /// <param name="slots">
    /// Gets each space light's slot, then each chunk light's, or -1 for a chunk light that found none. Must hold at
    /// least both counts.
    /// </param>
    /// <param name="unslotted">
    /// Cleared, then given the <see cref="LocalLight.Key"/> of each chunk light that found no slot, ascending. It
    /// allocates nothing when its capacity holds them.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="slots"/> is too short.</exception>
    public static void Assign(int spaceSun, ReadOnlySpan<(int Key, int SpaceChannel)> spaceLights,
        ReadOnlySpan<(int Key, int Chunk, Vector3 Position, float Range)> chunkLights, Span<int> slots, List<int> unslotted)
    {
        ArgumentNullException.ThrowIfNull(unslotted);
        if (slots.Length < spaceLights.Length + chunkLights.Length)
        {
            throw new ArgumentException(
                $"slots holds {slots.Length} entries for {spaceLights.Length} space and {chunkLights.Length} chunk lights.",
                nameof(slots));
        }

        unslotted.Clear();
        int spaceHeld = HoldSpaceSlots(spaceSun, spaceLights, slots);
        var chunkSlots = slots.Slice(spaceLights.Length, chunkLights.Length);
        chunkSlots.Fill(Waiting);

        // The next waiting light by key, ties by index, each time round: no list to sort, so nothing is allocated.
        for (int round = 0; round < chunkLights.Length; round++)
        {
            int light = NextWaiting(chunkLights, chunkSlots);
            chunkSlots[light] = FirstFree(spaceHeld | TakenNear(light, chunkLights, chunkSlots));
            if (chunkSlots[light] < 0)
            {
                unslotted.Add(chunkLights[light].Key);
            }
        }
    }

    /// <summary>Gives each space light its own channel as its slot, and returns the slots the space holds as bits.</summary>
    private static int HoldSpaceSlots(int spaceSun, ReadOnlySpan<(int Key, int SpaceChannel)> spaceLights, Span<int> slots)
    {
        int held = spaceSun >= 0 && spaceSun < ShadowMaskChannels.Count ? 1 << spaceSun : 0;
        for (int i = 0; i < spaceLights.Length; i++)
        {
            int channel = spaceLights[i].SpaceChannel;
            slots[i] = channel;
            if (channel >= 0 && channel < ShadowMaskChannels.Count)
            {
                held |= 1 << channel;
            }
        }

        return held;
    }

    /// <summary>The waiting chunk light with the lowest key, the first one on a tie.</summary>
    private static int NextWaiting(ReadOnlySpan<(int Key, int Chunk, Vector3 Position, float Range)> chunkLights, Span<int> chunkSlots)
    {
        int light = -1;
        for (int i = 0; i < chunkLights.Length; i++)
        {
            if (chunkSlots[i] == Waiting && (light < 0 || chunkLights[i].Key < chunkLights[light].Key))
            {
                light = i;
            }
        }

        return light;
    }

    /// <summary>The slots, as bits, already held by chunk lights that <paramref name="light"/> can't share one with.</summary>
    private static int TakenNear(int light, ReadOnlySpan<(int Key, int Chunk, Vector3 Position, float Range)> chunkLights, Span<int> chunkSlots)
    {
        int taken = 0;
        var (_, chunk, position, range) = chunkLights[light];
        for (int other = 0; other < chunkLights.Length; other++)
        {
            if (chunkSlots[other] < 0)
            {
                continue;
            }

            // One placed chunk has one table, which can name only one of its channels per slot, so its lights
            // never share a slot however far apart. Copies of a chunk each have their own table.
            float reach = range + chunkLights[other].Range;
            if (chunkLights[other].Chunk == chunk
                || Vector3.DistanceSquared(position, chunkLights[other].Position) < reach * reach)
            {
                taken |= 1 << chunkSlots[other];
            }
        }

        return taken;
    }

    /// <summary>The lowest slot not in <paramref name="taken"/>, or -1 when all are.</summary>
    private static int FirstFree(int taken)
    {
        for (int slot = 0; slot < ShadowMaskChannels.Count; slot++)
        {
            if ((taken & (1 << slot)) == 0)
            {
                return slot;
            }
        }

        return -1;
    }
}
