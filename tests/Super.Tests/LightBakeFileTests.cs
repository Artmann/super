using System.Numerics;
using Rendition;

namespace Super.Tests;

public sealed class LightBakeFileTests
{
    private static readonly BakeInputs Inputs = new(0x1111111111111111UL, 0x2222222222222222UL, 0x3333333333333333UL, 0x4444444444444444UL);

    private static LightBakePage Page(int width, int height, LightBakePageFormat format, int seed)
    {
        var pixels = new byte[width * height * 2];
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

        var error = Assert.Throws<InvalidDataException>(() => LightBakeFile.Read(bytes.AsSpan(0, 139)));

        Assert.StartsWith("The .lightbake file is cut off or damaged: a page says", error.Message);
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
