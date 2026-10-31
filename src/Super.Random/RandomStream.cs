using System.Numerics;

namespace Super;

/// <summary>
/// One repeatable sequence of random numbers, from <see cref="GameRandom.Stream"/>.
/// It uses the xoshiro256** generator, which is fast, small and well tested.
/// </summary>
public sealed class RandomStream
{
    private ulong _a;
    private ulong _b;
    private ulong _c;
    private ulong _d;

    internal RandomStream(ulong seed)
    {
        // SplitMix64 spreads one number over the four words of state, as the
        // generator's authors recommend.
        _a = Mix(seed += 0x9E3779B97F4A7C15);
        _b = Mix(seed += 0x9E3779B97F4A7C15);
        _c = Mix(seed += 0x9E3779B97F4A7C15);
        _d = Mix(seed + 0x9E3779B97F4A7C15);
    }

    /// <summary>64 random bits. Everything else in this class is built on this.</summary>
    public ulong NextBits()
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

    /// <summary>A number from 0 up to, but not including, 1.</summary>
    public float Float() => (NextBits() >> 40) * (1f / (1 << 24));

    /// <summary>A whole number from min to max, including both, so Between(1, 6) is a die.</summary>
    public int Between(int min, int max)
    {
        if (max < min)
        {
            throw new ArgumentException($"max ({max}) is less than min ({min}).", nameof(max));
        }

        return (int)(min + (long)Below((ulong)((long)max - min + 1)));
    }

    /// <summary>A number from min up to max.</summary>
    public float Between(float min, float max)
    {
        if (max < min)
        {
            throw new ArgumentException($"max ({max}) is less than min ({min}).", nameof(max));
        }

        return min + Float() * (max - min);
    }

    /// <summary>True with the given probability: 0 is never, 1 is always.</summary>
    public bool Chance(float probability) => Float() < probability;

    /// <summary>A coin flip.</summary>
    public bool Bool() => (NextBits() >> 63) != 0;

    /// <summary>Either -1 or 1.</summary>
    public int Sign() => Bool() ? 1 : -1;

    /// <summary>An angle in radians, from 0 up to a full turn.</summary>
    public float Angle() => Float() * MathF.Tau;

    /// <summary>
    /// A bell curve around mean, where about two thirds of results fall within one
    /// deviation of it. Good for things like damage that varies but rarely goes wild.
    /// </summary>
    public float Normal(float mean = 0f, float deviation = 1f)
    {
        // The Box-Muller transform. 1 - Float() avoids taking the log of zero.
        float radius = MathF.Sqrt(-2f * MathF.Log(1f - Float()));
        return mean + deviation * radius * MathF.Cos(Angle());
    }

    /// <summary>A random item. The list can't be empty.</summary>
    public T Pick<T>(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            throw new ArgumentException("There is nothing to pick from.", nameof(items));
        }

        return items[(int)Below((ulong)items.Count)];
    }

    /// <summary>
    /// A random item, where an item with weight 4 comes up twice as often as one with
    /// weight 2. Weight 0 means never. At least one weight must be above zero.
    /// </summary>
    public T PickWeighted<T>(IReadOnlyList<T> items, Func<T, float> weight)
    {
        double total = 0;
        for (int i = 0; i < items.Count; i++)
        {
            float itemWeight = weight(items[i]);
            if (itemWeight < 0f || !float.IsFinite(itemWeight))
            {
                throw new ArgumentException($"Item {i} has the weight {itemWeight}.", nameof(weight));
            }

            total += itemWeight;
        }

        if (total <= 0)
        {
            throw new ArgumentException("At least one item needs a weight above zero.", nameof(items));
        }

        double roll = Float() * total;
        double reached = 0;
        int last = 0;
        for (int i = 0; i < items.Count; i++)
        {
            float itemWeight = weight(items[i]);
            if (itemWeight <= 0f)
            {
                continue;
            }

            last = i;
            reached += itemWeight;
            if (roll < reached)
            {
                return items[i];
            }
        }

        // Rounding can leave the roll just past the end.
        return items[last];
    }

    /// <summary>Puts the items in a random order, in place.</summary>
    public void Shuffle<T>(IList<T> items)
    {
        // Fisher-Yates: every order is equally likely.
        for (int i = items.Count - 1; i > 0; i--)
        {
            int other = (int)Below((ulong)i + 1);
            (items[i], items[other]) = (items[other], items[i]);
        }
    }

    /// <summary>A direction on the flat plane, one unit long.</summary>
    public Vector2 Direction2D()
    {
        float angle = Angle();
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
    }

    /// <summary>A direction in 3D, one unit long, with no direction more likely than another.</summary>
    public Vector3 Direction3D()
    {
        float height = Between(-1f, 1f);
        float radius = MathF.Sqrt(MathF.Max(0f, 1f - height * height));
        var around = Direction2D();
        return new Vector3(around.X * radius, height, around.Y * radius);
    }

    /// <summary>A point in a circle around zero, evenly spread over its area.</summary>
    public Vector2 InsideCircle(float radius = 1f) => Direction2D() * (MathF.Sqrt(Float()) * radius);

    /// <summary>A point in a ball around zero, evenly spread through its volume.</summary>
    public Vector3 InsideSphere(float radius = 1f) => Direction3D() * (MathF.Cbrt(Float()) * radius);

    /// <summary>The SplitMix64 mixer, which scrambles a number thoroughly.</summary>
    internal static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EB;
        return value ^ (value >> 31);
    }

    /// <summary>
    /// An even choice from 0 up to the limit. Lemire's method: a plain % would favour
    /// low numbers slightly, so the few rolls that cause that bias are thrown away.
    /// </summary>
    private ulong Below(ulong limit)
    {
        ulong high = Math.BigMul(NextBits(), limit, out ulong low);
        if (low < limit)
        {
            ulong rejected = (0 - limit) % limit;
            while (low < rejected)
            {
                high = Math.BigMul(NextBits(), limit, out low);
            }
        }

        return high;
    }
}
