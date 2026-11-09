using Rendition;
using Super.Cook.Models;

namespace Super.Cook.Tests;

public sealed class TextureCompressionTests
{
    private static TextureData Texture(byte[]? level0 = null) => new()
    {
        Width = 4,
        Height = 4,
        Kind = TextureKind.Color,
        Mips = [level0 ?? Enumerable.Range(0, 4 * 4 * 4).Select(i => (byte)(i * 4)).ToArray()],
    };

    [Fact]
    public void Compressing_one_texture_for_two_models_gives_the_same_compressed_texture()
    {
        // Models of a kit hold the same decoded texture, so the second model gets the first one's compressed copy.
        var texture = Texture();
        TextureData[] first = [texture];
        TextureData[] second = [texture];

        TextureCompression.Compress(first, [], masks: [0]);
        TextureCompression.Compress(second, [], masks: [0]);

        Assert.Equal(TextureFormat.Bc7, first[0].Format);
        Assert.Same(first[0], second[0]);
    }

    [Fact]
    public void A_compression_that_failed_is_tried_again_the_next_time()
    {
        // In watch mode the process lives on, so a failure must not stay in the memo.
        var texture = Texture(level0: new byte[3]);
        // Three bytes can't hold a 4 by 4 level, so the encoder reads past their end.
        var failure = Assert.Throws<AggregateException>(() => TextureCompression.Compress([texture], [], masks: [0]));
        Assert.IsType<IndexOutOfRangeException>(Assert.Single(failure.InnerExceptions));

        texture.Mips[0] = Texture().Mips[0];
        TextureData[] textures = [texture];
        TextureCompression.Compress(textures, [], masks: [0]);

        Assert.Equal(TextureFormat.Bc7, textures[0].Format);
    }
}
