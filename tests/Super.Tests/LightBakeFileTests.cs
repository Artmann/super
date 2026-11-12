using System.Numerics;
using Rendition;

namespace Super.Tests;

public sealed class LightBakeFileTests
{
    private static readonly BakeInputs Inputs = new(0x1111111111111111UL, 0x2222222222222222UL, 0x3333333333333333UL, 0x4444444444444444UL);

    // The layout, worked by hand: a 41 byte header, then the draw count and the draws at 28 bytes each, then the
    // irradiance count, then the first irradiance page whose header is width, height, format, raw length and
    // compressed length at 4, 4, 1, 4 and 4 bytes.
    private const int HeaderSize = 41;
    private const int DrawBytes = 28;
    private const int PageHeaderBytes = 17;
    private const int DrawCountOffset = HeaderSize;

    private static int FirstPageOffset(int draws) => HeaderSize + 4 + draws * DrawBytes + 4;

    private static byte[] Patched(byte[] bytes, int offset, int value)
    {
        var copy = (byte[])bytes.Clone();
        BitConverter.GetBytes(value).CopyTo(copy, offset);
        return copy;
    }

    private static LightBakePage Page(int width, int height, LightBakePageFormat format, int seed)
    {
        // Hand-worked sizes: 8 bytes a texel, 4 bytes a texel, and 16 bytes a 4 by 4 block.
        var size = format switch
        {
            LightBakePageFormat.Rgba16f => width * height * 8,
            LightBakePageFormat.Rgba8 => width * height * 4,
            _ => (width + 3) / 4 * ((height + 3) / 4) * 16,
        };
        var pixels = new byte[size];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 31 + seed);
        }

        return new LightBakePage(width, height, format, pixels);
    }

    private static LightBakeFile Build(bool withProbes)
    {
        return new LightBakeFile(
            Inputs, BakeQuality.Preview,
            [
                (0xAABBCCDDEEFF0011UL, new LightmapRect(0, new Vector2(0.5f, 0.25f), new Vector2(0.125f, 0.75f))),
                (7UL, new LightmapRect(2, new Vector2(1f, 1f), new Vector2(0f, 0f))),
            ],
            [Page(4, 2, LightBakePageFormat.Rgba16f, 1), Page(2, 2, LightBakePageFormat.Bc6h, 2)],
            [Page(3, 1, LightBakePageFormat.Rgba8, 3)],
            [Page(1, 1, LightBakePageFormat.Bc7, 4)],
            withProbes ? Page(2, 3, LightBakePageFormat.Rgba16f, 5) : null,
            withProbes ? Page(3, 3, LightBakePageFormat.Rgba8, 6) : null,
            withProbes ? new ProbeVolumeSettings(new Vector3(-1f, -2f, -3f), new Vector3(4f, 5f, 6f), 1.5f) : null,
            [(101, 0), (-5, 3)],
            new LightBakeReport([9, 12], 0.0625f, 64, TimeSpan.FromTicks(123456789)));
    }

    private static void AssertPages(IReadOnlyList<LightBakePage> expected, IReadOnlyList<LightBakePage> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            AssertPage(expected[i], actual[i]);
        }
    }

    private static void AssertPage(LightBakePage? expected, LightBakePage? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Format, actual.Format);
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_reads_back_what_was_written(bool withProbes)
    {
        var written = Build(withProbes) with { Quality = withProbes ? BakeQuality.Final : BakeQuality.Preview };

        var read = LightBakeFile.Read(written.ToBytes());

        Assert.Equal(written.Inputs, read.Inputs);
        Assert.Equal(written.Quality, read.Quality);
        Assert.Equal(written.Draws, read.Draws);
        AssertPages(written.Irradiance, read.Irradiance);
        AssertPages(written.Direction, read.Direction);
        AssertPages(written.Mask, read.Mask);
        AssertPage(written.ProbeIrradiance, read.ProbeIrradiance);
        AssertPage(written.ProbeDepth, read.ProbeDepth);
        Assert.Equal(written.Probes, read.Probes);
        Assert.Equal(withProbes, read.Probes is not null);
        Assert.Equal(written.Channels, read.Channels);
        Assert.Equal(written.Report.LightsWithoutChannel, read.Report.LightsWithoutChannel);
        Assert.Equal(written.Report.InvalidTexelShare, read.Report.InvalidTexelShare);
        Assert.Equal(written.Report.Samples, read.Report.Samples);
        Assert.Equal(written.Report.Time, read.Report.Time);
    }

    [Fact]
    public void Final_quality_reads_back_as_final()
    {
        var written = Build(false) with { Quality = BakeQuality.Final };

        Assert.Equal(BakeQuality.Final, LightBakeFile.Read(written.ToBytes()).Quality);
    }

    [Fact]
    public void Reading_inputs_does_not_need_the_pages()
    {
        var bytes = Build(true).ToBytes();
        var headerOnly = bytes.AsSpan(0, 41).ToArray();

        Assert.Equal(Inputs, LightBakeFile.ReadInputs(headerOnly));
        Assert.Equal(Inputs, LightBakeFile.ReadInputs(bytes));
    }

    [Fact]
    public void Reading_inputs_from_less_than_a_header_throws()
    {
        var bytes = Build(false).ToBytes();

        var error = Assert.Throws<InvalidDataException>(() => LightBakeFile.ReadInputs(bytes.AsSpan(0, 20)));

        Assert.Equal("The .lightbake header is cut off: the file is 20 bytes and the header takes 41.", error.Message);
    }

    [Fact]
    public void A_file_cut_off_after_the_header_cannot_be_read_whole()
    {
        var bytes = Build(true).ToBytes();

        var error = Assert.Throws<InvalidDataException>(() => LightBakeFile.Read(bytes.AsSpan(0, 41)));

        Assert.Equal("The .lightbake file is cut off: it ends at byte 41 before all its parts were read.", error.Message);
    }

    [Fact]
    public void A_file_cut_off_inside_a_page_cannot_be_read()
    {
        var bytes = Build(false).ToBytes();

        // Cut so that 17 bytes of the first page's pixels are left, fewer than the 64 it stores.
        var cut = FirstPageOffset(2) + PageHeaderBytes + 17;

        var error = Assert.Throws<InvalidDataException>(() => LightBakeFile.Read(bytes.AsSpan(0, cut)));

        Assert.StartsWith("The .lightbake file is cut off or damaged: a page says", error.Message);
    }

    [Fact]
    public void A_count_larger_than_the_file_throws_instead_of_allocating()
    {
        var bytes = Patched(Build(false).ToBytes(), DrawCountOffset, int.MaxValue);

        var error = Assert.Throws<InvalidDataException>(() => LightBakeFile.Read(bytes));

        Assert.StartsWith($"The .lightbake file is cut off or damaged: it says {int.MaxValue} draws but only ", error.Message);
    }

    [Fact]
    public void A_raw_length_that_does_not_fit_the_page_size_throws_instead_of_allocating()
    {
        // The first page is 4 by 2 Rgba16f, so 64 bytes; its raw length field is 9 bytes into the page.
        var bytes = Patched(Build(false).ToBytes(), FirstPageOffset(2) + 9, int.MaxValue);

        var error = Assert.Throws<InvalidDataException>(() => LightBakeFile.Read(bytes));

        Assert.Equal(
            $"The .lightbake page is damaged: a 4 by 2 Rgba16f page takes 64 bytes but the file says {int.MaxValue}.",
            error.Message);
    }

    [Fact]
    public void A_negative_page_size_throws()
    {
        var bytes = Patched(Build(false).ToBytes(), FirstPageOffset(2), -4);

        var error = Assert.Throws<InvalidDataException>(() => LightBakeFile.Read(bytes));

        Assert.Equal("The .lightbake page is damaged: it is -4 by 2 texels, and a size can't be negative.", error.Message);
    }

    [Theory]
    [InlineData(0, 2049, "2049 by 2")]
    [InlineData(4, 16000, "4 by 16000")]
    public void A_lightmap_page_larger_than_any_bake_makes_throws_instead_of_allocating(int field, int size, string named)
    {
        // The first page is 4 by 2 Rgba16f; its width is the page's first field and its height the next. Its raw length
        // is patched to match, so only the size is wrong.
        var bytes = Build(false).ToBytes();
        bytes = Patched(bytes, FirstPageOffset(2) + field, size);
        int width = field == 0 ? size : 4;
        int height = field == 0 ? 2 : size;
        bytes = Patched(bytes, FirstPageOffset(2) + 9, width * height * 8);

        var error = Assert.Throws<InvalidDataException>(() => LightBakeFile.Read(bytes));

        Assert.Equal(
            $"The .lightbake page is damaged: it is {named} texels, but a lightmap page is at most 2048 a side.",
            error.Message);
    }

    [Theory]
    [InlineData(true, 641, "probe irradiance atlas is at most 640")]
    [InlineData(false, 1153, "probe depth atlas is at most 1152")]
    public void A_probe_atlas_larger_than_any_bake_makes_throws(bool irradiance, int width, string limit)
    {
        // 64 columns of 8 + 2 irradiance texels make 640, and of 16 + 2 depth texels 1152.
        var atlas = new LightBakePage(width, 1, LightBakePageFormat.Rgba8, new byte[width * 4]);
        var file = Build(true);
        file = irradiance ? file with { ProbeIrradiance = atlas } : file with { ProbeDepth = atlas };

        var error = Assert.Throws<InvalidDataException>(() => LightBakeFile.Read(file.ToBytes()));

        Assert.Equal($"The .lightbake page is damaged: it is {width} by 1 texels, but a {limit} a side.", error.Message);
    }

    [Fact]
    public void Pages_as_large_as_a_bake_makes_read()
    {
        var file = Build(true) with
        {
            Irradiance = [new LightBakePage(2048, 1, LightBakePageFormat.Rgba8, new byte[2048 * 4])],
            ProbeIrradiance = new LightBakePage(640, 1, LightBakePageFormat.Rgba8, new byte[640 * 4]),
            ProbeDepth = new LightBakePage(1, 1152, LightBakePageFormat.Rgba8, new byte[1152 * 4]),
        };

        var read = LightBakeFile.Read(file.ToBytes());

        Assert.Equal(2048, read.Irradiance[0].Width);
        Assert.Equal(640, read.ProbeIrradiance!.Width);
        Assert.Equal(1152, read.ProbeDepth!.Height);
    }

    [Fact]
    public void A_page_that_inflates_to_more_than_it_says_throws()
    {
        // One 1 by 1 Rgba8 page (4 bytes) whose stored pixels are swapped for 8 compressed bytes.
        var file = Build(false) with
        {
            Draws = [],
            Irradiance = [new LightBakePage(1, 1, LightBakePageFormat.Rgba8, new byte[4])],
            Direction = [],
            Mask = [],
        };
        var bytes = file.ToBytes();
        var pageAt = FirstPageOffset(0);
        var oldCompressed = BitConverter.ToInt32(bytes, pageAt + 13);
        using var squeezed = new MemoryStream();
        using (var brotli = new System.IO.Compression.BrotliStream(squeezed, System.IO.Compression.CompressionLevel.Optimal, true))
        {
            brotli.Write(new byte[8]);
        }

        var swapped = bytes[..(pageAt + 13)]
            .Concat(BitConverter.GetBytes((int)squeezed.Length))
            .Concat(squeezed.ToArray())
            .Concat(bytes[(pageAt + PageHeaderBytes + oldCompressed)..])
            .ToArray();

        var error = Assert.Throws<InvalidDataException>(() => LightBakeFile.Read(swapped));

        Assert.Equal("The .lightbake page is damaged: its pixels inflate to more than the 4 bytes it says.", error.Message);
    }

    [Theory]
    [InlineData(LightBakePageFormat.Bc6h, 5, 5, 64)]
    [InlineData(LightBakePageFormat.Bc7, 4, 4, 16)]
    [InlineData(LightBakePageFormat.Rgba8, 3, 2, 24)]
    public void A_page_of_every_format_has_the_size_its_format_expects(LightBakePageFormat format, int width, int height, int bytes)
    {
        var page = new LightBakePage(width, height, format, new byte[bytes]);
        var file = Build(false) with { Irradiance = [page] };

        var read = LightBakeFile.Read(file.ToBytes());

        Assert.Equal(bytes, read.Irradiance[0].Pixels.Length);
    }

    [Fact]
    public void A_wrong_magic_throws_naming_the_magic()
    {
        var bytes = Build(false).ToBytes();
        bytes[0] = 0x58;

        var expected = "The .lightbake magic is wrong: found 0x4B414258, expected 0x4B41424C.";
        Assert.Equal(expected, Assert.Throws<InvalidDataException>(() => LightBakeFile.Read(bytes)).Message);
        Assert.Equal(expected, Assert.Throws<InvalidDataException>(() => LightBakeFile.ReadInputs(bytes)).Message);
    }

    [Fact]
    public void A_wrong_version_throws_naming_the_version()
    {
        var bytes = Build(false).ToBytes();
        bytes[4] = 2;

        var expected = "The .lightbake version is wrong: found 2, expected 1.";
        Assert.Equal(expected, Assert.Throws<InvalidDataException>(() => LightBakeFile.Read(bytes)).Message);
        Assert.Equal(expected, Assert.Throws<InvalidDataException>(() => LightBakeFile.ReadInputs(bytes)).Message);
    }

    [Fact]
    public void Pages_are_compressed()
    {
        var flat = new LightBakePage(256, 256, LightBakePageFormat.Rgba8, new byte[256 * 256 * 4]);
        var file = Build(false) with { Irradiance = [flat], Direction = [], Mask = [] };

        var size = file.ToBytes().Length;

        Assert.True(size < flat.Pixels.Length / 100, $"A flat {flat.Pixels.Length} byte page made a {size} byte file.");
    }
}
