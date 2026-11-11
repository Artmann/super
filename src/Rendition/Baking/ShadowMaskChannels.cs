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
    /// Fills <paramref name="sunChannels"/> and <paramref name="lightChannels"/> with a channel from 0 to
    /// <see cref="Count"/> - 1 for each light that gets one, and -1 for a light that doesn't. Only a Mixed light takes
    /// a channel, and a local light only when it is in the space's box, as <see cref="BakeAreas.OwnerOfLight"/> reads
    /// it. The first Mixed sun takes channel 0; any other Mixed sun gets none. A local light that is Mixed and in the
    /// space but finds every channel taken by an overlapping light keeps real-time shadows only, and its
    /// <see cref="LocalLight.Key"/> is added to <paramref name="unassigned"/>, in ascending order, after clearing it.
    /// The result depends on the keys, not on the order of <paramref name="lights"/>, unless two lights share a key.
    /// </summary>
    public static void Assign(BakedSpace space, ReadOnlySpan<DirectionalLight> suns, ReadOnlySpan<LocalLight> lights,
        Span<int> sunChannels, Span<int> lightChannels, List<int> unassigned)
    {
        ArgumentNullException.ThrowIfNull(unassigned);
        if (sunChannels.Length < suns.Length)
        {
            throw new ArgumentException(
                $"sunChannels holds {sunChannels.Length} entries for {suns.Length} suns.", nameof(sunChannels));
        }

        if (lightChannels.Length < lights.Length)
        {
            throw new ArgumentException(
                $"lightChannels holds {lightChannels.Length} entries for {lights.Length} lights.", nameof(lightChannels));
        }

        unassigned.Clear();

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

        // Each member is its key and its index, so sorting goes by key and a shared key falls back to the index.
        var members = new List<(int Key, int Index)>();
        for (var i = 0; i < lights.Length; i++)
        {
            lightChannels[i] = -1;
            if (lights[i].Mode == LightMode.Mixed &&
                BakeAreas.OwnerOfLight(lights[i].Position, space, []) == BakeAreas.Space)
            {
                members.Add((lights[i].Key, i));
            }
        }

        members.Sort();

        for (var m = 0; m < members.Count; m++)
        {
            var light = members[m].Index;
            var taken = 0;
            for (var earlier = 0; earlier < m; earlier++)
            {
                var other = members[earlier].Index;
                if (lightChannels[other] >= 0 && RangesOverlap(lights[light], lights[other]))
                {
                    taken |= 1 << lightChannels[other];
                }
            }

            for (var channel = firstFree; channel < Count; channel++)
            {
                if ((taken & (1 << channel)) == 0)
                {
                    lightChannels[light] = channel;
                    break;
                }
            }

            if (lightChannels[light] < 0)
            {
                unassigned.Add(lights[light].Key);
            }
        }
    }

    private static bool RangesOverlap(in LocalLight a, in LocalLight b)
    {
        var reach = a.Range + b.Range;
        return Vector3.DistanceSquared(a.Position, b.Position) < reach * reach;
    }
}
