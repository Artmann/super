using System.Numerics;

namespace Super;

/// <summary>
/// A set of component type IDs, one bit each, in four words. An archetype is keyed by the mask of its types, and a
/// query matches the archetypes whose mask holds all of its own.
/// </summary>
internal readonly struct ComponentMask : IEquatable<ComponentMask>
{
    /// <summary>The most component types a world holds.</summary>
    public const int MaxTypes = 256;

    private readonly ulong _a;
    private readonly ulong _b;
    private readonly ulong _c;
    private readonly ulong _d;

    private ComponentMask(ulong a, ulong b, ulong c, ulong d)
    {
        _a = a;
        _b = b;
        _c = c;
        _d = d;
    }

    public bool IsEmpty => (_a | _b | _c | _d) == 0;

    public bool Has(int type) => (Word(type >> 6) & (1UL << (type & 63))) != 0;

    public ComponentMask With(int type)
    {
        ulong bit = 1UL << (type & 63);
        return (type >> 6) switch
        {
            0 => new ComponentMask(_a | bit, _b, _c, _d),
            1 => new ComponentMask(_a, _b | bit, _c, _d),
            2 => new ComponentMask(_a, _b, _c | bit, _d),
            _ => new ComponentMask(_a, _b, _c, _d | bit),
        };
    }

    public ComponentMask Without(int type)
    {
        ulong keep = ~(1UL << (type & 63));
        return (type >> 6) switch
        {
            0 => new ComponentMask(_a & keep, _b, _c, _d),
            1 => new ComponentMask(_a, _b & keep, _c, _d),
            2 => new ComponentMask(_a, _b, _c & keep, _d),
            _ => new ComponentMask(_a, _b, _c, _d & keep),
        };
    }

    /// <summary>Whether this mask has every type <paramref name="other"/> has.</summary>
    public bool ContainsAll(in ComponentMask other) =>
        (_a & other._a) == other._a && (_b & other._b) == other._b && (_c & other._c) == other._c && (_d & other._d) == other._d;

    public ComponentMask And(in ComponentMask other) => new(_a & other._a, _b & other._b, _c & other._c, _d & other._d);

    /// <summary>The lowest set type after <paramref name="after"/>, or -1. Pass -1 to start.</summary>
    public int Next(int after)
    {
        int start = after + 1;
        for (int word = start >> 6; word < 4; word++)
        {
            ulong bits = Word(word);
            if (word == start >> 6)
            {
                bits &= ~0UL << (start & 63);
            }

            if (bits != 0)
            {
                return (word << 6) + BitOperations.TrailingZeroCount(bits);
            }
        }

        return -1;
    }

    public bool Equals(ComponentMask other) => _a == other._a && _b == other._b && _c == other._c && _d == other._d;

    public override bool Equals(object? obj) => obj is ComponentMask other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_a, _b, _c, _d);

    private ulong Word(int index) => index switch
    {
        0 => _a,
        1 => _b,
        2 => _c,
        _ => _d,
    };
}
