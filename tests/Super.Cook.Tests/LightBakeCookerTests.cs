using System.Numerics;
using System.Text;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using Rendition;
using Super.Testing;

namespace Super.Cook.Tests;

public sealed class LightBakeCookerTests
{
    private static readonly BakeInputs Inputs = new(0x1111111111111111UL, 0x2222222222222222UL, 0x3333333333333333UL, 0x4444444444444444UL);

    private const int Side = 64;

    // The irradiance of texel (x, y) in channel c, a smooth ramp that stays between 0.2 and about 7.
    private static float Ramp(int x, int y, int c) => 0.2f + x * 0.08f + y * 0.03f + c * 0.5f;

    // Rows from `emptyFrom` down are empty, as most of a baked page is.
    private static LightBakePage Irradiance(int width, int height, int emptyFrom)
    {
        var pixels = new byte[width * height * 8];
        for (int y = 0; y < Math.Min(emptyFrom, height); y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    BitConverter.GetBytes((System.Half)Ramp(x, y, c)).CopyTo(pixels, (y * width + x) * 8 + c * 2);
                }

                BitConverter.GetBytes((System.Half)1f).CopyTo(pixels, (y * width + x) * 8 + 6);
            }
        }

        return new LightBakePage(width, height, LightBakePageFormat.Rgba16f, pixels);
    }

    private static LightBakePage Rgba8(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = (byte)(x * 3);
                pixels[i + 1] = (byte)(y * 3);
                pixels[i + 2] = (byte)(x + y);
                pixels[i + 3] = (byte)(255 - x);
            }
        }

        return new LightBakePage(width, height, LightBakePageFormat.Rgba8, pixels);
    }

    private static LightBakeFile Bake(params LightBakePage[] irradiance)
    {
        return new LightBakeFile(
            Inputs, BakeQuality.Preview,
            [(7UL, new LightmapRect(0, new Vector2(0.5f, 0.25f), new Vector2(0.125f, 0.75f)))],
            irradiance, [Rgba8(Side, Side)], [Rgba8(Side, Side)],
            Irradiance(Side, Side, Side), Irradiance(Side, Side, 0),
            new ProbeVolumeSettings(new Vector3(-1f, -2f, -3f), new Vector3(4f, 5f, 6f), 1.5f),
            [(101, 0)], new LightBakeReport([9], 0.0625f, 64, TimeSpan.FromTicks(1234)));
    }

    private static (CookResult Result, byte[]? Cooked) Cook(byte[] source)
    {
        var root = new MemoryRoot();
        root.Add("lighting/space.lightbake", source);
        var store = new MemoryStore();
        var result = new CookPipeline(BuiltInCookers.All, new CookOptions()).Cook([root], store);
        return (result, result.Succeeded ? store.Get(store.Manifest.Assets["lighting/space.lightbake"].Hash) : null);
    }

    private static LightBakeFile CookedBake(LightBakeFile bake)
    {
        var (result, cooked) = Cook(bake.ToBytes());
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        return LightBakeFile.Read(cooked!);
    }

    [Fact]
    public void Cooking_compresses_irradiance_to_bc6h()
    {
        var cooked = CookedBake(Bake(Irradiance(Side, Side, Side), Irradiance(Side, 32, 8)));

        Assert.Equal(2, cooked.Irradiance!.Count);
        Assert.All(cooked.Irradiance, page => Assert.Equal(LightBakePageFormat.Bc6h, page.Format));

        // Hand-worked: 64 by 64 texels are 16 by 16 blocks of 16 bytes, and 64 by 32 are 16 by 8.
        Assert.Equal(16 * 16 * 16, cooked.Irradiance[0].Pixels.Length);
        Assert.Equal(16 * 8 * 16, cooked.Irradiance[1].Pixels.Length);
        Assert.Equal((Side, 32), (cooked.Irradiance[1].Width, cooked.Irradiance[1].Height));
    }

    [Fact]
    public void Cooking_compresses_direction_and_mask_to_bc7()
    {
        var cooked = CookedBake(Bake(Irradiance(Side, Side, Side)));

        var direction = Assert.Single(cooked.Direction!);
        var mask = Assert.Single(cooked.Mask);
        Assert.Equal(LightBakePageFormat.Bc7, direction.Format);
        Assert.Equal(LightBakePageFormat.Bc7, mask.Format);
        Assert.Equal(16 * 16 * 16, direction.Pixels.Length);
        Assert.Equal(16 * 16 * 16, mask.Pixels.Length);

        // The pixels are the page's, to within what BC7 loses on a gentle ramp.
        var decoded = new BcDecoder().DecodeRaw(direction.Pixels, Side, Side, CompressionFormat.Bc7);
        var source = Rgba8(Side, Side).Pixels;
        double error = 0;
        for (int i = 0; i < decoded.Length; i++)
        {
            error += Math.Abs(decoded[i].r - source[i * 4]) + Math.Abs(decoded[i].g - source[i * 4 + 1])
                + Math.Abs(decoded[i].b - source[i * 4 + 2]) + Math.Abs(decoded[i].a - source[i * 4 + 3]);
        }

        Assert.True(error / (decoded.Length * 4) < 2.0, $"The mean error was {error / (decoded.Length * 4):0.00} levels.");
    }

    [Fact]
    public void Cooking_leaves_probes_and_odd_pages_as_they_are()
    {
        var odd = Irradiance(30, 30, 30);
        var bake = Bake(odd);

        var cooked = CookedBake(bake);

        Assert.Equal(LightBakePageFormat.Rgba16f, cooked.ProbeIrradiance!.Format);
        Assert.Equal(bake.ProbeIrradiance!.Pixels, cooked.ProbeIrradiance.Pixels);
        Assert.Equal(LightBakePageFormat.Rgba16f, cooked.ProbeDepth!.Format);
        Assert.Equal(bake.ProbeDepth!.Pixels, cooked.ProbeDepth.Pixels);

        // 30 is not a multiple of 4, so the page keeps the baker's bytes.
        var page = Assert.Single(cooked.Irradiance!);
        Assert.Equal(LightBakePageFormat.Rgba16f, page.Format);
        Assert.Equal(odd.Pixels, page.Pixels);
    }

    [Fact]
    public void Cooking_keeps_transfer_pages_uncompressed_half_floats()
    {
        // 64 by 64 would suit a block format, so only the cooker's choice keeps these as they are.
        var transfer = new LightBakeTransfer(
            Irradiance(Side, Side, Side), Irradiance(Side, Side, 32), Irradiance(Side, Side, 8), Irradiance(Side, Side, Side),
            Rgba8(Side, Side));
        var bake = Bake(Irradiance(Side, Side, Side)) with { Transfer = transfer, ChunkChannels = [(77UL, 1)] };

        var cooked = CookedBake(bake);

        var read = Assert.IsType<LightBakeTransfer>(cooked.Transfer);
        foreach (var (expected, actual) in new[]
        {
            (transfer.SkyRed, read.SkyRed), (transfer.SkyGreen, read.SkyGreen), (transfer.SkyBlue, read.SkyBlue),
            (transfer.Sun, read.Sun),
        })
        {
            Assert.Equal(LightBakePageFormat.Rgba16f, actual.Format);
            Assert.Equal(expected.Pixels, actual.Pixels);
        }

        Assert.Equal(LightBakePageFormat.Rgba8, read.SunTint.Format);
        Assert.Equal(transfer.SunTint.Pixels, read.SunTint.Pixels);
        Assert.Equal(bake.ChunkChannels, cooked.ChunkChannels);
    }

    [Fact]
    public void Cooking_a_chunk_with_no_plain_light_keeps_it_without_irradiance()
    {
        var bake = Bake(Irradiance(Side, Side, Side)) with { Irradiance = null, Direction = null };

        var cooked = CookedBake(bake);

        Assert.Null(cooked.Irradiance);
        Assert.Null(cooked.Direction);
        Assert.Equal(LightBakePageFormat.Bc7, Assert.Single(cooked.Mask).Format);
    }
    [Fact]
    public void Cooking_keeps_the_input_hash()
    {
        var bake = Bake(Irradiance(Side, Side, Side));
        var (result, cooked) = Cook(bake.ToBytes());
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));

        Assert.Equal(Inputs, LightBakeFile.ReadInputs(cooked!));
        var read = LightBakeFile.Read(cooked!);
        Assert.Equal(bake.Quality, read.Quality);
        Assert.Equal(bake.Draws, read.Draws);
        Assert.Equal(bake.Channels, read.Channels);
        Assert.Equal(bake.Probes, read.Probes);
        Assert.Equal(bake.Report.LightsWithoutChannel, read.Report.LightsWithoutChannel);
        Assert.Equal(bake.Report.Time, read.Report.Time);
    }

    [Fact]
    public void Cooked_irradiance_stays_close()
    {
        // The top half is a ramp and the bottom half is empty, as a baked page's unused part is.
        var source = Irradiance(Side, Side, 32);
        var cooked = CookedBake(Bake(source));

        var page = Assert.Single(cooked.Irradiance!);
        var decoded = new BcDecoder().DecodeRawHdr(page.Pixels, Side, Side, CompressionFormat.Bc6U);
        double difference = 0;
        double total = 0;
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < Side; x++)
            {
                var color = decoded[y * Side + x];
                difference += Math.Abs(color.r - Ramp(x, y, 0)) + Math.Abs(color.g - Ramp(x, y, 1)) + Math.Abs(color.b - Ramp(x, y, 2));
                total += Ramp(x, y, 0) + Ramp(x, y, 1) + Ramp(x, y, 2);
            }
        }

        Assert.True(difference / total < 0.02, $"The mean error was {difference / total:P2} of the light.");

        // The empty half comes back empty, not as a block of whatever zeros decode to.
        for (int y = 32; y < Side; y++)
        {
            for (int x = 0; x < Side; x++)
            {
                var color = decoded[y * Side + x];
                Assert.True(color.r == 0f && color.g == 0f && color.b == 0f, $"Texel ({x}, {y}) decoded to {color.r}, {color.g}, {color.b}.");
            }
        }
    }

    [Fact]
    public void Dim_light_beside_black_texels_does_not_come_back_blinding()
    {
        // Every block has two black columns and two columns of dim light, 0.05 and 0.04 on its last row, as the edge of a
        // chart does. BCnEncoder.Net alone writes the black texels of such a block as 65504.
        var pixels = new byte[Side * Side * 8];
        for (int y = 0; y < Side; y++)
        {
            for (int x = 0; x < Side; x++)
            {
                if (x % 4 >= 2)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        BitConverter.GetBytes((System.Half)(y % 4 == 3 ? 0.04f : 0.05f)).CopyTo(pixels, (y * Side + x) * 8 + c * 2);
                    }
                }
            }
        }

        var cooked = CookedBake(Bake(new LightBakePage(Side, Side, LightBakePageFormat.Rgba16f, pixels)));

        var decoded = new BcDecoder().DecodeRawHdr(Assert.Single(cooked.Irradiance!).Pixels, Side, Side, CompressionFormat.Bc6U);
        float brightest = decoded.Max(color => Math.Max(color.r, Math.Max(color.g, color.b)));
        Assert.True(brightest < 0.1f, $"The brightest texel came back as {brightest}, from a page whose brightest is 0.05.");
    }

    [Fact]
    public void Cooking_a_cooked_bake_changes_nothing()
    {
        var once = CookedBake(Bake(Irradiance(Side, Side, Side)));

        var (result, cooked) = Cook(once.ToBytes());

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var twice = LightBakeFile.Read(cooked!);
        Assert.Equal(once.Irradiance![0].Pixels, twice.Irradiance![0].Pixels);
        Assert.Equal(once.Direction![0].Pixels, twice.Direction![0].Pixels);
    }

    [Fact]
    public void A_damaged_bake_is_an_error_naming_the_file()
    {
        var (result, _) = Cook(Encoding.UTF8.GetBytes("this is not a light bake"));

        Assert.False(result.Succeeded);
        var message = Assert.Single(result.Problems, p => p.IsError).Message;
        Assert.Contains("lighting/space.lightbake is not a light bake this can read", message);
        Assert.Contains("magic is wrong", message);
    }
}
