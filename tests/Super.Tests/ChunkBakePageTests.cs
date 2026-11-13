using System.Numerics;
using Rendition;

namespace Super.Tests;

/// <summary>
/// The chunk bake pool's check of a file before any of it goes to the GPU: a damaged or wrongly cooked chunk bake is
/// refused with a message, not uploaded as pages the shaders would read as something else.
/// </summary>
public sealed class ChunkBakePageTests
{
    private const string File = "lighting/shed/00000000000000aa.lightbake";

    private static readonly BakeInputs Inputs = new(1UL, 2UL, 3UL, 4UL);

    // Hand-worked sizes: 8 bytes a texel, 4 bytes a texel, and 16 bytes a 4 by 4 block.
    private static LightBakePage Page(int width, int height, LightBakePageFormat format) => new(
        width, height, format,
        new byte[format switch
        {
            LightBakePageFormat.Rgba16f => width * height * 8,
            LightBakePageFormat.Rgba8 => width * height * 4,
            _ => (width + 3) / 4 * ((height + 3) / 4) * 16,
        }]);

    private static LightBakeTransfer Transfer(int size = 8) => new(
        Page(size, size, LightBakePageFormat.Rgba16f), Page(size, size, LightBakePageFormat.Rgba16f),
        Page(size, size, LightBakePageFormat.Rgba16f), Page(size, size, LightBakePageFormat.Rgba16f),
        Page(size, size, LightBakePageFormat.Rgba8));

    // A chunk with one draw on its only page, its transfer, and plain light only when asked.
    private static LightBakeFile Chunk(bool plainLight, int page = 0) => new(
        Inputs, BakeQuality.Preview,
        [(0x1000UL, new LightmapRect(page, new Vector2(0.5f), Vector2.Zero))],
        plainLight ? [Page(8, 8, LightBakePageFormat.Rgba16f)] : null,
        plainLight ? [Page(8, 8, LightBakePageFormat.Rgba8)] : null,
        [], null, null, null, [], new LightBakeReport([], 0f, 1, TimeSpan.Zero))
    {
        Transfer = Transfer(),
    };

    private static string Refusal(LightBakeFile bake) =>
        Assert.Throws<InvalidDataException>(() => ChunkBakeCheck.CheckPages(bake, File)).Message;

    [Fact]
    public void Chunk_with_only_its_transfer_passes()
    {
        ChunkBakeCheck.CheckPages(Chunk(plainLight: false), File);
    }

    [Fact]
    public void Chunk_with_plain_light_passes()
    {
        ChunkBakeCheck.CheckPages(Chunk(plainLight: true), File);
    }

    [Fact]
    public void Chunk_without_a_transfer_is_refused()
    {
        var bake = Chunk(plainLight: true) with { Transfer = null };

        Assert.Equal($"The chunk bake {File} has no sky and sun transfer, which every chunk's bake needs.", Refusal(bake));
    }

    [Fact]
    public void Sky_transfer_page_that_is_not_rgba16f_is_refused()
    {
        var bake = Chunk(plainLight: false);
        bake = bake with { Transfer = bake.Transfer! with { SkyGreen = Page(8, 8, LightBakePageFormat.Rgba8) } };

        Assert.Equal($"The chunk bake {File}'s sky green transfer page is Rgba8, but it must be Rgba16f.", Refusal(bake));
    }

    [Fact]
    public void Sun_transfer_page_that_is_block_compressed_is_refused()
    {
        var bake = Chunk(plainLight: false);
        bake = bake with { Transfer = bake.Transfer! with { Sun = Page(8, 8, LightBakePageFormat.Bc6h) } };

        Assert.Equal($"The chunk bake {File}'s sun transfer page is Bc6h, but it must be Rgba16f.", Refusal(bake));
    }

    [Fact]
    public void Sun_tint_that_is_not_rgba8_is_refused()
    {
        var bake = Chunk(plainLight: false);
        bake = bake with { Transfer = bake.Transfer! with { SunTint = Page(8, 8, LightBakePageFormat.Rgba16f) } };

        Assert.Equal($"The chunk bake {File}'s sun tint transfer page is Rgba16f, but it must be Rgba8.", Refusal(bake));
    }

    [Fact]
    public void Transfer_pages_of_different_sizes_are_refused()
    {
        var bake = Chunk(plainLight: false);
        bake = bake with { Transfer = bake.Transfer! with { SkyBlue = Page(8, 4, LightBakePageFormat.Rgba16f) } };

        Assert.Equal($"The chunk bake {File}'s sky blue transfer page is 8x4, but its sky red page is 8x8.", Refusal(bake));
    }

    [Fact]
    public void Plain_light_page_of_another_size_than_the_transfer_is_refused()
    {
        var bake = Chunk(plainLight: true) with { Direction = [Page(4, 4, LightBakePageFormat.Rgba8)] };

        Assert.Equal($"The chunk bake {File}'s direction page is 4x4, but its sky red page is 8x8.", Refusal(bake));
    }

    [Fact]
    public void Irradiance_without_direction_is_refused()
    {
        var bake = Chunk(plainLight: true) with { Direction = null };

        Assert.Equal($"The chunk bake {File} has 1 irradiance pages but 0 direction pages.", Refusal(bake));
    }

    [Fact]
    public void Chunk_with_more_than_one_plain_page_is_refused()
    {
        var page = Page(8, 8, LightBakePageFormat.Rgba16f);
        var bake = Chunk(plainLight: true) with { Irradiance = [page, page], Direction = [page, page] };

        Assert.Equal($"The chunk bake {File} has 2 irradiance and 0 mask pages, but a chunk's bake has at most one of each.", Refusal(bake));
    }

    [Fact]
    public void Page_whose_bytes_do_not_fit_its_size_is_refused()
    {
        var bake = Chunk(plainLight: true) with
        {
            Irradiance = [new LightBakePage(8, 8, LightBakePageFormat.Rgba16f, new byte[100])],
        };

        // 8 by 8 texels at 8 bytes each.
        Assert.Equal($"The chunk bake {File}'s 8x8 Rgba16f irradiance page holds 100 bytes, but it needs 512.", Refusal(bake));
    }

    [Fact]
    public void Draw_on_a_page_past_the_first_is_refused()
    {
        var bake = Chunk(plainLight: false, page: 1);

        Assert.Equal($"The chunk bake {File} puts draw 0x0000000000001000 on page 1, but a chunk's bake has one page.", Refusal(bake));
    }
}
