using System.Numerics;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
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

    [Theory]
    [InlineData(0.3f)]
    [InlineData(0.5f)]
    public void A_cut_outs_smaller_levels_keep_their_coverage_once_compressed(float cutoff)
    {
        // Thin strands, as of grass or a fence: on the smaller levels the filter leaves most alpha just either side of
        // the cutoff, where plain BC7 moved it across and the strands vanished at a distance.
        const int size = 256;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                pixels[i] = (byte)x;
                pixels[i + 1] = (byte)y;
                pixels[i + 2] = 90;
                pixels[i + 3] = Math.Sin(x * 0.9) * Math.Sin(y * 0.37 + x * 0.13) > 0.55 ? (byte)255 : (byte)0;
            }
        }

        var kept = TextureData.KeepAlphaCoverage(TextureData.FromPixels(size, size, TextureKind.Color, pixels), cutoff);
        TextureData[] textures = [kept];
        TextureCompression.Compress(textures, [new ModelMaterial
        {
            BaseColor = Vector4.One, AlphaCutoff = cutoff,
            BaseColorTexture = 0, NormalTexture = -1, SurfaceTexture = -1, EmissiveTexture = -1, HeightTexture = -1,
            DetailColorTexture = -1, DetailNormalTexture = -1,
        }]);

        Assert.Equal(TextureFormat.Bc7, textures[0].Format);
        int threshold = (int)MathF.Ceiling(cutoff * 255f - 1e-3f);
        // Down to 4 by 4, where a level is one block. Each level's count of passing pixels may be off by 1% of its
        // pixels, or by 2 on the levels too small for that, as BC7 rounds a block's alpha to a few steps.
        for (int level = 1; size >> level >= 4; level++)
        {
            int width = size >> level;
            int before = 0;
            for (int i = 3; i < kept.Mips[level].Length; i += 4)
            {
                if (kept.Mips[level][i] >= threshold)
                {
                    before++;
                }
            }

            int after = new BcDecoder().DecodeRaw(textures[0].Mips[level], width, width, CompressionFormat.Bc7).Count(color => color.a >= threshold);

            Assert.True(before > 0, $"Level {level} has no pixel that passes, so it can't show coverage being kept.");
            Assert.InRange(after, before - Math.Max(2, width * width / 100), before + Math.Max(2, width * width / 100));
        }
    }

    [Fact]
    public void A_compression_that_failed_is_tried_again_the_next_time()
    {
        // In watch mode the process lives on, so a failure must not stay in the memo.
        var texture = Texture(level0: new byte[3]);
        // Three bytes can't hold a 4 by 4 level, so the encoder refuses them.
        var failure = Assert.Throws<AggregateException>(() => TextureCompression.Compress([texture], [], masks: [0]));
        var refused = Assert.IsType<ArgumentException>(Assert.Single(failure.InnerExceptions));
        Assert.StartsWith("A 4x4 RGBA8 image is 64 bytes, not 3.", refused.Message);

        texture.Mips[0] = Texture().Mips[0];
        TextureData[] textures = [texture];
        TextureCompression.Compress(textures, [], masks: [0]);

        Assert.Equal(TextureFormat.Bc7, textures[0].Format);
    }
}
