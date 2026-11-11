using System.Globalization;

namespace Rendition;

/// <summary>
/// Makes and reads <see cref="DrawItem.BakeId"/>s. An id is an instance's stable hash with its top bit set, so it is
/// never zero, and its low 12 bits replaced by the part's number within the instance. See "Bake ids" in docs/design.md.
/// </summary>
public static class BakeIds
{
    /// <summary>
    /// The highest part number an id can hold, so parts run from 0 to it. It is also the most parts one static model
    /// can have: a static model of more parts throws when drawn.
    /// </summary>
    public const int MaxParts = 4095;

    private const ulong PartMask = 0xFFF;
    private const ulong Static = 1ul << 63;

    /// <summary>The id of one part of an instance, from the instance's <see cref="StableHasher"/> hash.</summary>
    public static ulong Of(ulong instanceHash, int part)
    {
        if ((uint)part > MaxParts)
        {
            throw new ArgumentOutOfRangeException(nameof(part), part, string.Create(CultureInfo.InvariantCulture,
                $"A bake id's part must be from 0 to {MaxParts}, not {part}."));
        }

        return (instanceHash & ~PartMask) | Static | (uint)part;
    }

    /// <summary>The id with its part cleared, the same for every part of one instance.</summary>
    public static ulong Instance(ulong bakeId) => bakeId & ~PartMask;

    /// <summary>The part's number within its instance.</summary>
    public static int Part(ulong bakeId) => (int)(bakeId & PartMask);

    /// <summary>
    /// Throws for a static draw that moves on its own, with bones or swim, since a bake holds it where it stands. Reads
    /// one field per draw when no draw is static.
    /// </summary>
    internal static void CheckStaticDraws(ReadOnlySpan<DrawItem> draws)
    {
        for (int i = 0; i < draws.Length; i++)
        {
            ref readonly var draw = ref draws[i];
            if (draw.BakeId == 0)
            {
                continue;
            }

            if (draw.Bones.Count != 0)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                    $"Draw {i} is static (BakeId 0x{draw.BakeId:X16}) but has {draw.Bones.Count} bones. A static draw can't be skinned; set its BakeId to 0."),
                    nameof(draws));
            }

            if (draw.Swim.Amplitude != 0f)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                    $"Draw {i} is static (BakeId 0x{draw.BakeId:X16}) but has a swim of amplitude {draw.Swim.Amplitude}. A static draw can't swim; set its BakeId to 0."),
                    nameof(draws));
            }
        }
    }
}
