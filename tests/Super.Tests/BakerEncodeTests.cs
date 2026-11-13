using System.Runtime.InteropServices;
using Rendition;

namespace Super.Tests;

public sealed class BakerEncodeTests
{
    [Fact]
    public void Irradiance_out_of_a_halfs_range_is_held_in_it()
    {
        // One row of four texels, each with one red value the half float can't hold as it is, and a green of 2.5 that
        // it can.
        float[] red = [float.NaN, -3f, 1e6f, float.PositiveInfinity];
        var irradiance = new float[16];
        for (int texel = 0; texel < 4; texel++)
        {
            irradiance[texel * 4] = red[texel];
            irradiance[texel * 4 + 1] = 2.5f;
            irradiance[texel * 4 + 2] = 0f;
        }

        var traced = new TracedPage(
            0, 0, 0, 4, 1, irradiance, new float[16], new float[16], [true, true, true, true],
            [true, true, true, true], [true, true, true, true]);

        var halves = MemoryMarshal.Cast<byte, Half>(BakeEncoding.Encode(traced).Irradiance.Pixels.AsSpan());

        // Not a number and below zero are no light; past 65504, the largest half, is held at 65504.
        float[] expected = [0f, 0f, 65504f, 65504f];
        for (int texel = 0; texel < 4; texel++)
        {
            Assert.Equal(expected[texel], (float)halves[texel * 4]);
            Assert.Equal(2.5f, (float)halves[texel * 4 + 1]);
        }
    }
}
