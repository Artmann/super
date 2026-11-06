using System.IO.Compression;
using System.Numerics;
using System.Text;
using Rendition;
using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

public class ImageTests
{
    private static readonly byte[] Wide = Png(4, 2);

    /// <summary>The smallest valid PNG of a size, all opaque white.</summary>
    internal static byte[] Png(int width, int height)
    {
        using var file = new MemoryStream();
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10]);

        byte[] header = new byte[13];
        WriteBig(header, 0, (uint)width);
        WriteBig(header, 4, (uint)height);
        header[8] = 8;
        header[9] = 6;
        Chunk(file, "IHDR", header);

        using var raw = new MemoryStream();
        using (var deflate = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] row = new byte[1 + width * 4];
            Array.Fill(row, (byte)255, 1, width * 4);
            for (int y = 0; y < height; y++)
            {
                deflate.Write(row);
            }
        }

        Chunk(file, "IDAT", raw.ToArray());
        Chunk(file, "IEND", []);
        return file.ToArray();
    }

    private static void WriteBig(byte[] target, int at, uint value)
    {
        target[at] = (byte)(value >> 24);
        target[at + 1] = (byte)(value >> 16);
        target[at + 2] = (byte)(value >> 8);
        target[at + 3] = (byte)value;
    }

    private static void Chunk(Stream file, string name, byte[] data)
    {
        byte[] body = [.. Encoding.ASCII.GetBytes(name), .. data];
        byte[] number = new byte[4];
        WriteBig(number, 0, (uint)data.Length);
        file.Write(number);
        file.Write(body);
        uint crc = 0xFFFFFFFF;
        foreach (byte value in body)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        WriteBig(number, 0, crc ^ 0xFFFFFFFF);
        file.Write(number);
    }

    private static GameImage Load(byte[] png)
    {
        var files = new MemoryContent().Add("a.png", png);
        return new Content(files).Image("a.png");
    }

    private static OverlayQuad Draw(UiView view)
    {
        var tree = new UiTree();
        tree.Update(view, 800, 600);
        return Assert.Single(tree.Quads.ToArray());
    }

    [Fact]
    public void An_image_knows_its_size_without_decoding()
    {
        var image = Load(Wide);
        Assert.Equal(4, image.Width);
        Assert.Equal(2, image.Height);
    }

    [Fact]
    public void Naming_an_image_twice_gives_the_same_one()
    {
        var content = new Content(new MemoryContent().Add("a.png", Wide));
        Assert.Same(content.Image("a.png"), content.Image("a.png"));
    }

    [Fact]
    public void A_missing_image_says_which_one()
    {
        var content = new Content(new MemoryContent());
        var error = Assert.Throws<ContentException>(() => content.Image("gone.png"));
        Assert.Contains("gone.png", error.Message);
    }

    [Fact]
    public void A_file_that_is_no_image_says_so()
    {
        var content = new Content(new MemoryContent().Add("a.png", [1, 2, 3]));
        var error = Assert.Throws<ContentException>(() => content.Image("a.png"));
        Assert.Contains("a.png", error.Message);
    }

    [Fact]
    public void A_painted_image_has_the_size_it_was_painted_at()
    {
        var image = new Content(new MemoryContent()).Image(3, 2, new byte[24]);
        Assert.Equal((3, 2), (image.Width, image.Height));
        Assert.Equal(new Vector2(3f, 2f), Draw(new Image(image)).Size);
    }

    [Fact]
    public void Each_painting_is_a_new_image()
    {
        var content = new Content(new MemoryContent());
        Assert.NotSame(content.Image(1, 1, new byte[4]), content.Image(1, 1, new byte[4]));
    }

    [Fact]
    public void Pixels_must_fill_the_painted_image()
    {
        var content = new Content(new MemoryContent());
        var error = Assert.Throws<ArgumentException>(() => content.Image(2, 2, new byte[12]));
        Assert.Contains("16 bytes", error.Message);
    }

    [Fact]
    public void A_cooked_image_gives_its_pixels()
    {
        var pixels = Load(Wide).ReadPixels();
        Assert.Equal(4 * 2 * 4, pixels.Length);
        Assert.All(pixels, value => Assert.Equal(255, value));
    }

    [Fact]
    public void A_painted_image_gives_back_its_pixels()
    {
        byte[] painted = [1, 2, 3, 4];
        Assert.Equal(painted, new Content(new MemoryContent()).Image(1, 1, painted).ReadPixels());
    }

    [Fact]
    public void An_image_can_be_let_go_once()
    {
        var content = new Content(new MemoryContent());
        var image = content.Image(1, 1, new byte[4]);
        content.Unload(image);
        Assert.Throws<ArgumentException>(() => content.Unload(image));
    }

    [Fact]
    public void Naming_an_image_after_letting_it_go_makes_a_new_one()
    {
        var content = new Content(new MemoryContent().Add("a.png", Wide));
        var first = content.Image("a.png");
        content.Unload(first);
        Assert.NotSame(first, content.Image("a.png"));
    }

    [Fact]
    public void An_image_draws_at_its_natural_size()
    {
        Assert.Equal(new Vector2(4f, 2f), Draw(new Image(Load(Wide))).Size);
    }

    [Fact]
    public void A_frame_decides_the_size()
    {
        Assert.Equal(new Vector2(48f, 48f), Draw(new Image(Load(Wide)).Frame(48f, 48f)).Size);
    }

    [Fact]
    public void A_frame_reaches_through_a_corner_radius_and_padding()
    {
        var view = new Image(Load(Wide)).CornerRadius(4f).Padding(2f).Frame(48f, 48f);
        Assert.Equal(new Vector2(44f, 44f), Draw(view).Size);
    }

    [Fact]
    public void One_length_keeps_the_shape()
    {
        Assert.Equal(new Vector2(40f, 20f), Draw(new Image(Load(Wide)).Frame(width: 40f)).Size);
    }

    [Fact]
    public void Contain_shows_all_of_the_image_centered()
    {
        var quad = Draw(new Image(Load(Wide), ImageFit.Contain).Frame(40f, 40f));
        Assert.Equal(new Vector2(40f, 20f), quad.Size);
        Assert.Equal(new Vector2(380f, 290f), quad.Position);
        Assert.Equal(Vector2.One, quad.UvMax);
    }

    [Fact]
    public void Cover_fills_the_frame_and_crops_the_sides()
    {
        var quad = Draw(new Image(Load(Wide), ImageFit.Cover).Frame(40f, 40f));
        Assert.Equal(new Vector2(40f, 40f), quad.Size);
        Assert.Equal(new Vector2(0.25f, 0f), quad.UvMin);
        Assert.Equal(new Vector2(0.75f, 1f), quad.UvMax);
    }

    [Fact]
    public void A_tint_is_the_quad_color()
    {
        var quad = Draw(new Image(Load(Wide), Tint: new Color(1f, 0.5f, 0f)));
        Assert.Equal(new Vector4(1f, 0.5f, 0f, 1f), quad.Color);
    }

    [Fact]
    public void The_renderer_fills_in_which_texture_each_quad_uses()
    {
        var image = Load(Wide);
        var tree = new UiTree();
        tree.Update(new Image(image), 800, 600);
        Assert.Equal(0, tree.Quads[0].TextureIndex);
        tree.BindImages(_ => 7);
        Assert.Equal(7, tree.Quads[0].TextureIndex);
    }

    [Fact]
    public void Mips_halve_any_shape_down_to_one_pixel()
    {
        var mips = ImageMips.Build(4, 1, new byte[16]);
        Assert.Equal([16, 8, 4], mips.Select(mip => mip.Length));
    }

    [Fact]
    public void Invisible_pixels_lend_no_color_to_a_mip()
    {
        byte[] pixels = [200, 100, 50, 255, 0, 0, 0, 0];
        var mips = ImageMips.Build(2, 1, pixels);
        Assert.Equal([200, 100, 50, 127], mips[1]);
    }
}
