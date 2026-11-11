using Rendition;

namespace Super.Tests;

public class StableHasherTests
{
    [Fact]
    public void Same_values_hash_the_same_in_a_new_hasher()
    {
        ulong first = new StableHasher().Add(1).Add(2.5f).Add("a").Finish();
        ulong second = new StableHasher().Add(1).Add(2.5f).Add("a").Finish();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Known_input_gives_a_known_hash()
    {
        // XxHash64.HashToUInt64 with seed 0 of the bytes 01 00 00 00, the int 1 in little-endian order. Written out
        // as a number so a change to the seed, the byte order or the hash itself fails here.
        const ulong expected = 0xF42F94001FCB5351;

        Assert.Equal(expected, new StableHasher().Add(1).Finish());
    }

    [Fact]
    public void Floats_closer_than_a_tenth_of_a_millimetre_hash_the_same()
    {
        Assert.Equal(new StableHasher().Add(1.00002f).Finish(), new StableHasher().Add(1.00004f).Finish());
        Assert.NotEqual(new StableHasher().Add(1.0000f).Finish(), new StableHasher().Add(1.0002f).Finish());
    }

    [Fact]
    public void Negative_zero_hashes_as_zero()
    {
        Assert.Equal(new StableHasher().Add(0f).Finish(), new StableHasher().Add(-0f).Finish());
    }

    [Fact]
    public void String_length_is_part_of_the_hash()
    {
        ulong split = new StableHasher().Add("ab").Add("c").Finish();
        ulong other = new StableHasher().Add("a").Add("bc").Finish();

        Assert.NotEqual(split, other);
    }
}
