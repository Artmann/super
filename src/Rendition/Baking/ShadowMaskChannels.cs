using System.Numerics;

namespace Rendition;

/// <summary>
/// Decides which of a space's four shadow mask channels each Mixed light writes. The sun, when Mixed, takes channel 0.
/// Mixed point and spot lights in the space are coloured greedily, in order of <see cref="LocalLight.Key"/>, so two
/// lights whose ranges overlap never share a channel. See "Shadow mask channels" in docs/design.md.
/// </summary>
public static class ShadowMaskChannels
{
    /// <summary>The channels in a shadow mask.</summary>
    public const int Count = 4;

    /// <summary>
    /// Gives each of <paramref name="suns"/> and <paramref name="lights"/> a channel from 0 to <see cref="Count"/> - 1,
    /// or -1 for a light that gets none. Only a Mixed light takes a channel, and a local light only when it is in the
    /// space's box, as <see cref="BakeAreas.OwnerOfLight"/> reads it. The first Mixed sun takes channel 0; any other
    /// Mixed sun gets none. A local light that is Mixed and in the space but finds every channel taken by an
    /// overlapping light keeps real-time shadows only, and its <see cref="LocalLight.Key"/> is in
    /// <see cref="SpaceMaskChannels.Unassigned"/>, in ascending order. The result depends on the keys, not on the order
    /// of <paramref name="lights"/>, unless two lights share a key.
    /// </summary>
    public static SpaceMaskChannels Assign(
        BakedSpace space, ReadOnlySpan<DirectionalLight> suns, ReadOnlySpan<LocalLight> lights)
    {
        var sunChannels = new int[suns.Length];
        var lightChannels = new int[lights.Length];
        var unassigned = new List<int>();
        var firstFree = AssignSuns(suns, sunChannels);

        // Each member is its key and its index, so sorting goes by key and a shared key falls back to the index.
        var members = new List<(int Key, int Index, Vector3 Position)>();
        for (var i = 0; i < lights.Length; i++)
        {
            lightChannels[i] = -1;
            if (lights[i].Mode == LightMode.Mixed &&
                BakeAreas.OwnerOfLight(lights[i].Position, space, []) == BakeAreas.Space)
            {
                members.Add((lights[i].Key, i, lights[i].Position));
            }
        }

        members.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Index.CompareTo(b.Index));
        Colour(lights, members, firstFree, lightChannels, unassigned);
        return new SpaceMaskChannels(sunChannels, lightChannels, [.. unassigned]);
    }

    // The first Mixed sun takes channel 0 and every other sun none. Returns the first channel left for local lights.
    private static int AssignSuns(ReadOnlySpan<DirectionalLight> suns, Span<int> sunChannels)
    {
        var firstFree = 0;
        for (var i = 0; i < suns.Length; i++)
        {
            sunChannels[i] = -1;
            if (suns[i].Mode == LightMode.Mixed && firstFree == 0)
            {
                sunChannels[i] = 0;
                firstFree = 1;
            }
        }

        return firstFree;
    }

    /// <summary>
    /// Gives the chunk's Mixed local lights channels 0..3 by greedy colouring of their range overlap in chunk space, in
    /// order of their chunk-space hash. There is no sun channel. Lights left without one go in
    /// <paramref name="unassigned"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="channels"/> gets -1 for a light that has none. A light is the chunk's when it is Mixed and its
    /// position, taken into the chunk's own space with <see cref="BakeAreas.IntoChunk"/>, is inside the chunk's box.
    /// Members go in order of <see cref="BakeInputHash.Light"/> with the chunk's transform (ties by index), not of
    /// <see cref="LocalLight.Key"/>: each placed copy of a chunk gives its lights other keys, but the same hash. Ranges
    /// are compared from the chunk-space positions too, so a copy's rounding at its place changes who overlaps less often, though not never at an exact boundary.
    /// The <see cref="LocalLight.Key"/> of every light left without a channel is added to
    /// <paramref name="unassigned"/>, in ascending order, after clearing it.
    /// </remarks>
    public static void AssignChunk(
        BakedChunk chunk, ReadOnlySpan<LocalLight> lights, Span<int> channels, List<int> unassigned)
    {
        ArgumentNullException.ThrowIfNull(unassigned);
        if (channels.Length < lights.Length)
        {
            throw new ArgumentException(
                $"channels holds {channels.Length} entries for {lights.Length} lights.", nameof(channels));
        }

        unassigned.Clear();

        var members = new List<(ulong Hash, int Index, Vector3 Position)>();
        for (var i = 0; i < lights.Length; i++)
        {
            channels[i] = -1;
            if (lights[i].Mode == LightMode.Mixed && InChunk(chunk, lights[i].Position) is { } position)
            {
                members.Add((BakeInputHash.Light(lights[i], chunk.Transform), i, position));
            }
        }

        members.Sort((a, b) => a.Hash != b.Hash ? a.Hash.CompareTo(b.Hash) : a.Index.CompareTo(b.Index));
        Colour(lights, members, 0, channels, unassigned);
        unassigned.Sort();
    }

    // The position in the chunk's own space, or null when that is outside the chunk's box.
    private static Vector3? InChunk(BakedChunk chunk, Vector3 position)
    {
        var (x, y, z) = BakeAreas.IntoChunk(chunk.Transform, position.X, position.Y, position.Z, 1.0);
        if (x < chunk.Min.X || x > chunk.Max.X
            || y < chunk.Min.Y || y > chunk.Max.Y
            || z < chunk.Min.Z || z > chunk.Max.Z)
        {
            return null;
        }

        return new Vector3((float)x, (float)y, (float)z);
    }

    // Greedy colouring of the members, in their order: each takes the first channel from firstFree on that no earlier
    // member whose range overlaps it holds, or none, and then its key goes in unassigned.
    private static void Colour<TOrder>(ReadOnlySpan<LocalLight> lights, List<(TOrder Order, int Index, Vector3 Position)> members,
        int firstFree, Span<int> channels, List<int> unassigned)
    {
        for (var m = 0; m < members.Count; m++)
        {
            var light = members[m].Index;
            var taken = 0;
            for (var earlier = 0; earlier < m; earlier++)
            {
                var other = members[earlier].Index;
                var reach = lights[light].Range + lights[other].Range;
                if (channels[other] >= 0 && Vector3.DistanceSquared(members[m].Position, members[earlier].Position) < reach * reach)
                {
                    taken |= 1 << channels[other];
                }
            }

            channels[light] = FirstFree(taken, firstFree);
            if (channels[light] < 0)
            {
                unassigned.Add(lights[light].Key);
            }
        }
    }

    // The lowest channel from first on that is not in taken, or -1 when all are.
    private static int FirstFree(int taken, int first)
    {
        for (var channel = first; channel < Count; channel++)
        {
            if ((taken & (1 << channel)) == 0)
            {
                return channel;
            }
        }

        return -1;
    }
}

/// <summary>
/// The shadow mask channels <see cref="ShadowMaskChannels.Assign"/> gave a space's lights: one for each sun and each
/// local light, in the order given, -1 for a light with none, and the keys of the local lights that wanted one but
/// found none free, ascending.
/// </summary>
public sealed record SpaceMaskChannels(int[] Suns, int[] Lights, int[] Unassigned);
