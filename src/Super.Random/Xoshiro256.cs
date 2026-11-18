using System.Numerics;

namespace Super;

/// <summary>
/// The four words of state of the xoshiro256** generator, and the step that makes 64 bits from them. Kept apart from
/// <see cref="RandomStream"/> so that class is only about turning bits into the numbers a game asks for.
/// </summary>
internal struct Xoshiro256
{
    private ulong _a;
    private ulong _b;
    private ulong _c;
    private ulong _d;

    public Xoshiro256(ulong seed)
    {
        // SplitMix64 spreads one number over the four words of state, as the
        // generator's authors recommend.
        _a = Mix(seed += 0x9E3779B97F4A7C15);
        _b = Mix(seed += 0x9E3779B97F4A7C15);
        _c = Mix(seed += 0x9E3779B97F4A7C15);
        _d = Mix(seed + 0x9E3779B97F4A7C15);
    }

    /// <summary>64 random bits, moving the state on.</summary>
    public ulong Next()
    {
        ulong result = BitOperations.RotateLeft(_b * 5, 7) * 9;
        ulong shifted = _b << 17;
        _c ^= _a;
        _d ^= _b;
        _b ^= _c;
        _a ^= _d;
        _c ^= shifted;
        _d = BitOperations.RotateLeft(_d, 45);
        return result;
    }

    /// <summary>The SplitMix64 mixer, which scrambles a number thoroughly.</summary>
    public static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EB;
        return value ^ (value >> 31);
    }
}
