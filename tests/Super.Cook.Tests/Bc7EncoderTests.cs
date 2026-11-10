using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using Super.Cook.Models;

namespace Super.Cook.Tests;

/// <summary>The cook's own BC7 encoder, read back with BCnEncoder.Net's decoder, which follows the format's spec.</summary>
public sealed class Bc7EncoderTests
{
    private static byte[] Decode(byte[] blocks, int width, int height)
    {
        var colors = new BcDecoder().DecodeRaw(blocks, width, height, CompressionFormat.Bc7);
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < colors.Length; i++)
        {
            rgba[i * 4] = colors[i].r;
            rgba[i * 4 + 1] = colors[i].g;
            rgba[i * 4 + 2] = colors[i].b;
            rgba[i * 4 + 3] = colors[i].a;
        }

        return rgba;
    }

    /// <summary>The peak signal to noise ratio over the channels from <paramref name="first"/> to <paramref name="last"/>, in dB.</summary>
    private static double Psnr(byte[] expected, byte[] actual, int first = 0, int last = 3)
    {
        double sum = 0;
        long count = 0;
        for (int i = 0; i < expected.Length; i += 4)
        {
            for (int c = first; c <= last; c++)
            {
                double d = expected[i + c] - actual[i + c];
                sum += d * d;
                count++;
            }
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / (sum / count));
    }

    /// <summary>A smooth picture with a little noise, as a material graph makes, 64 pixels square.</summary>
    private static byte[] Picture(Func<int, int, int, byte> channel)
    {
        var rgba = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    rgba[(y * 64 + x) * 4 + c] = channel(x, y, c);
                }
            }
        }

        return rgba;
    }

    private static byte Noise(int x, int y, int c) => (byte)((x * 7919 + y * 104729 + c * 31) % 9);

    [Fact]
    public void A_block_of_one_colour_comes_back_within_one_step()
    {
        byte[] rgba = Picture((_, _, c) => (byte)(c == 3 ? 255 : 40 + c * 50));

        byte[] decoded = Decode(Bc7Encoder.Encode(rgba, 64, 64), 64, 64);

        // Mode 6 keeps 7 bits and a shared low bit a channel, so a flat colour is at most one step off.
        for (int i = 0; i < rgba.Length; i++)
        {
            Assert.InRange(decoded[i] - rgba[i], -1, 1);
        }
    }

    [Fact]
    public void An_opaque_picture_keeps_its_alpha_exactly_and_its_colour_closely()
    {
        byte[] rgba = Picture((x, y, c) => c == 3 ? (byte)255 : (byte)(x * 2 + y * c + Noise(x, y, c)));

        byte[] decoded = Decode(Bc7Encoder.Encode(rgba, 64, 64), 64, 64);

        for (int i = 3; i < decoded.Length; i += 4)
        {
            Assert.Equal(255, decoded[i]);
        }

        Assert.True(Psnr(rgba, decoded, 0, 2) > 40, $"PSNR {Psnr(rgba, decoded, 0, 2):F2}");
    }

    [Fact]
    public void A_mask_whose_four_channels_vary_apart_keeps_each_closely()
    {
        // As a baked mask: the channels are separate maps, so no one line through the block fits them all.
        byte[] rgba = Picture((x, y, c) => c switch
        {
            0 => (byte)(x * 4),
            1 => (byte)(y * 4),
            2 => (byte)((x * y) % 256),
            _ => (byte)(255 - x * 3),
        });

        byte[] decoded = Decode(Bc7Encoder.Encode(rgba, 64, 64), 64, 64);

        Assert.True(Psnr(rgba, decoded) > 30, $"PSNR {Psnr(rgba, decoded):F2}");
    }

    /// <summary>
    /// Every rotation of mode 5, with the first pixel at either end of the colour line and of the separate channel's
    /// line, so each anchor's swap happens with the other's and without it.
    /// </summary>
    public static TheoryData<int, bool, bool> Rotations()
    {
        var data = new TheoryData<int, bool, bool>();
        for (int rotation = 0; rotation < 4; rotation++)
        {
            foreach (bool colourReversed in new[] { false, true })
            {
                foreach (bool separateReversed in new[] { false, true })
                {
                    data.Add(rotation, colourReversed, separateReversed);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Rotations))]
    public void A_block_with_one_channel_apart_is_written_as_mode_5_with_that_channel_rotated_into_alpha(
        int rotation, bool colourReversed, bool separateReversed)
    {
        // Mode 5's rotation swaps alpha with red (1), green (2) or blue (3) before decoding, so that channel has its own line.
        int separate = rotation == 0 ? 3 : rotation - 1;
        int[] steps = [0, 21, 43, 64];
        static int Between(int low, int high, int weight) => ((64 - weight) * low + weight * high + 32) >> 6;

        // The other three channels lie on one line, at its four steps across the block, with ends that 7 bits hold.
        // The separate channel steps down the block instead, so no single line through all four fits.
        (int Low, int High)[] colourEnds = [(20, 241), (241, 20), (60, 180)];
        var rgba = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++)
        {
            int across = colourReversed ? 3 - i % 4 : i % 4;
            int down = separateReversed ? 3 - i / 4 : i / 4;
            int colour = 0;
            for (int c = 0; c < 4; c++)
            {
                rgba[i * 4 + c] = c == separate
                    ? (byte)Between(10, 250, steps[down])
                    : (byte)Between(colourEnds[colour].Low, colourEnds[colour++].High, steps[across]);
            }
        }

        byte[] block = Bc7Encoder.Encode(rgba, 4, 4);

        // Mode 5 is a 1 in bit 5 with the bits below it clear; the rotation is the next two bits.
        Assert.Equal(0x20, block[0] & 0x3f);
        Assert.Equal(rotation, block[0] >> 6);
        byte[] decoded = Decode(block, 4, 4);
        for (int i = 0; i < rgba.Length; i++)
        {
            Assert.True(Math.Abs(decoded[i] - rgba[i]) <= 2, $"Channel {i % 4} of pixel {i / 4} is {decoded[i]}, not {rgba[i]}.");
        }
    }

    [Fact]
    public void A_picture_whose_size_isnt_whole_blocks_repeats_its_edge()
    {
        byte[] rgba = new byte[5 * 3 * 4];
        for (int i = 0; i < rgba.Length; i++)
        {
            rgba[i] = (byte)(i % 4 == 3 ? 255 : 100 + i);
        }

        byte[] blocks = Bc7Encoder.Encode(rgba, 5, 3);

        // Two blocks across, one down.
        Assert.Equal(2 * 16, blocks.Length);
        byte[] decoded = Decode(blocks, 5, 3);
        Assert.True(Psnr(rgba, decoded) > 35, $"PSNR {Psnr(rgba, decoded):F2}");
    }

    [Fact]
    public void The_same_pixels_always_give_the_same_bytes()
    {
        byte[] rgba = Picture((x, y, c) => (byte)(x * 3 + y * 5 + c * 40 + Noise(x, y, c)));

        Assert.Equal(Bc7Encoder.Encode(rgba, 64, 64), Bc7Encoder.Encode(rgba, 64, 64));
    }

    [Fact]
    public void Pixels_that_dont_fill_the_size_are_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => Bc7Encoder.Encode(new byte[10], 4, 4));

        Assert.StartsWith("A 4x4 RGBA8 image is 64 bytes, not 10.", error.Message);
    }
}
