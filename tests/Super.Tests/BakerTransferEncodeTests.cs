using System.Runtime.InteropServices;
using Rendition;

namespace Super.Tests;

public sealed class BakerTransferEncodeTests
{
    [Fact]
    public void Coloured_sun_transfer_is_split_into_a_tint_and_coefficients_that_multiply_back()
    {
        // A page of 2 x 1 texels. The first holds a sun's transfer summed by luminance, S = (2, 0.5, -0.4, 0.3), and its
        // colour summed apart, T = (0.8, 0.2, 0.1): red-ish light. The second is a texel nothing reached.
        float[] sun = [2f, 0.5f, -0.4f, 0.3f];
        var transfer = new float[2 * Baker.TransferFloats];
        for (int c = 0; c < 4; c++)
        {
            transfer[12 + c] = sun[c];
        }

        transfer[16] = 0.8f;
        transfer[17] = 0.2f;
        transfer[18] = 0.1f;
        var traced = new TracedPage(0, 0, 0, 2, 1, new float[8], new float[8], new float[8], [true, false],
            [true, false], [true, false]) { Transfer = transfer };

        var encoded = Baker.EncodeTransfer(traced, 2);

        // The tint is T over its largest channel, 0.8: 1, 0.25 and 0.125, as bytes 255, 64 (63.75) and 32 (31.875).
        var tint = encoded.SunTint.Pixels;
        Assert.Equal(LightBakePageFormat.Rgba8, encoded.SunTint.Format);
        Assert.Equal(new byte[] { 255, 64, 32 }, tint[..3]);

        // T's luminance is 0.2126 * 0.8 + 0.7152 * 0.2 + 0.0722 * 0.1 = 0.32034, so each colour's share of S is
        // T_c / 0.32034, and the stored coefficients are S * 0.8 / 0.32034 = S * 2.49734, C0 = 4.99469.
        var halves = MemoryMarshal.Cast<byte, Half>(encoded.Sun.Pixels.AsSpan());
        float[] stored = [(float)halves[0], (float)halves[1], (float)halves[2], (float)halves[3]];
        Assert.Equal(4.99469f, stored[0], 0.005f);

        // Tint times the stored coefficients gives each colour's own transfer, S * T_c / 0.32034, back.
        float[] colour = [0.8f, 0.2f, 0.1f];
        for (int channel = 0; channel < 3; channel++)
        {
            for (int c = 0; c < 4; c++)
            {
                float expected = sun[c] * colour[channel] / 0.32034f;
                float actual = tint[channel] / 255f * stored[c];
                Assert.True(MathF.Abs(actual - expected) <= 0.01f * MathF.Abs(expected) + 1e-3f,
                    $"Colour {channel}, coefficient {c}: tint times sun is {actual}, not {expected}.");
            }
        }

        // The texel nothing reached holds no sun and a white tint, so it neither lights nor tints anything.
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, tint[4..8]);
        for (int c = 4; c < 8; c++)
        {
            Assert.Equal(0f, (float)halves[c]);
        }
    }
}
