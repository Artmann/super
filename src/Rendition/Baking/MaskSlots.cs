using System.Numerics;

namespace Rendition;

/// <summary>
/// Shares the frame's four shadow mask slots among every bake it draws. Each bake has its own four channels, so a chunk
/// light's channel means nothing on another bake's surfaces; the lighting pass reads a light's frame slot instead, and
/// each bake's surfaces write their channels into the slots their lights hold. The space's Mixed sun keeps slot 0 and
/// its local lights keep their channels. Chunk lights take the free slots greedily, in order of
/// <see cref="LocalLight.Key"/>, so two whose world-space ranges overlap never share one. See "Shadow mask channels"
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
    /// Every Mixed local light that has a channel in its own chunk's bake, in world space. Each takes the first slot
    /// that neither the space nor an earlier chunk light whose range overlaps it holds. The space's lights carry no
    /// position, so their slots are held everywhere.
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
        ReadOnlySpan<(int Key, Vector3 Position, float Range)> chunkLights, Span<int> slots, List<int> unslotted)
    {
        ArgumentNullException.ThrowIfNull(unslotted);
        if (slots.Length < spaceLights.Length + chunkLights.Length)
        {
            throw new ArgumentException(
                $"slots holds {slots.Length} entries for {spaceLights.Length} space and {chunkLights.Length} chunk lights.",
                nameof(slots));
        }

        unslotted.Clear();
        int spaceHeld = spaceSun >= 0 && spaceSun < ShadowMaskChannels.Count ? 1 << spaceSun : 0;
        for (int i = 0; i < spaceLights.Length; i++)
        {
            int channel = spaceLights[i].SpaceChannel;
            slots[i] = channel;
            if (channel >= 0 && channel < ShadowMaskChannels.Count)
            {
                spaceHeld |= 1 << channel;
            }
        }

        var chunkSlots = slots.Slice(spaceLights.Length, chunkLights.Length);
        chunkSlots.Fill(Waiting);

        // The next waiting light by key, ties by index, each time round: no list to sort, so nothing is allocated.
        for (int round = 0; round < chunkLights.Length; round++)
        {
            int light = -1;
            for (int i = 0; i < chunkLights.Length; i++)
            {
                if (chunkSlots[i] == Waiting && (light < 0 || chunkLights[i].Key < chunkLights[light].Key))
                {
                    light = i;
                }
            }

            int taken = spaceHeld;
            var (key, position, range) = chunkLights[light];
            for (int other = 0; other < chunkLights.Length; other++)
            {
                if (chunkSlots[other] < 0)
                {
                    continue;
                }

                float reach = range + chunkLights[other].Range;
                if (Vector3.DistanceSquared(position, chunkLights[other].Position) < reach * reach)
                {
                    taken |= 1 << chunkSlots[other];
                }
            }

            chunkSlots[light] = -1;
            for (int slot = 0; slot < ShadowMaskChannels.Count; slot++)
            {
                if ((taken & (1 << slot)) == 0)
                {
                    chunkSlots[light] = slot;
                    break;
                }
            }

            if (chunkSlots[light] < 0)
            {
                unslotted.Add(key);
            }
        }
    }
}
