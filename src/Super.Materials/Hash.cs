namespace Super.Materials;

/// <summary>Stateless randomness: the same inputs always give the same value.</summary>
internal static class Hash
{
    const ulong Golden = 0x9E3779B97F4A7C15UL;

    public static ulong Of(ulong seed, int a, int b)
    {
        var h = Mix(seed + Golden);

        h = Mix(h + Golden * (uint)a + 1);
        h = Mix(h + Golden * (uint)b + 2);

        return h;
    }

    /// <summary>From 0 up to, but not including, 1.</summary>
    public static float Unit(ulong seed, int a, int b) => (Of(seed, a, b) >> 40) * (1f / (1 << 24));

    // The SplitMix64 finalizer.
    static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;

        return z ^ (z >> 31);
    }
}
