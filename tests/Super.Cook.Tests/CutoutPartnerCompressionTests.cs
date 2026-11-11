using System.Numerics;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using Rendition;
using Super.Cook.Models;

namespace Super.Cook.Tests;

/// <summary>A cut-out whose colour and surface both carry alpha, which the shader cuts by their product.</summary>
public sealed class CutoutPartnerCompressionTests
{
    private const int Size = 128;
    private const float Cutoff = 0.5f;

    // Thin strands, as in TextureCompressionTests: on the smaller levels most alpha sits near the cutoff.
    private static TextureData Strands()
    {
        var pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int i = (y * Size + x) * 4;
                pixels[i] = (byte)x;
                pixels[i + 1] = (byte)y;
                pixels[i + 2] = 90;
                pixels[i + 3] = Math.Sin(x * 0.9) * Math.Sin(y * 0.37 + x * 0.13) > 0.55 ? (byte)255 : (byte)0;
            }
        }

        return TextureData.FromPixels(Size, Size, TextureKind.Color, pixels);
    }

    // A surface whose alpha is the same partial value everywhere.
    private static TextureData Surface(byte alpha)
    {
        var pixels = new byte[Size * Size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;
            pixels[i + 1] = 120;
            pixels[i + 2] = 0;
            pixels[i + 3] = alpha;
        }

        return TextureData.FromPixels(Size, Size, TextureKind.Linear, pixels);
    }

    private static ModelMaterial Material(int color, int surface, float cutoff = Cutoff) => new()
    {
        BaseColor = Vector4.One, AlphaCutoff = cutoff,
        BaseColorTexture = color, NormalTexture = -1, SurfaceTexture = surface, EmissiveTexture = -1, HeightTexture = -1,
        DetailColorTexture = -1, DetailNormalTexture = -1,
    };

    private static byte[] DecodedAlpha(TextureData compressed, int level)
    {
        int width = Size >> level;
        return new BcDecoder().DecodeRaw(compressed.Mips[level], width, width, CompressionFormat.Bc7).Select(c => c.a).ToArray();
    }

    [Fact]
    public void A_surface_under_a_colour_with_alpha_is_compressed_whole()
    {
        // The strands are the surface's alpha here, and the colour has partial alpha too. Stretching the surface's
        // alpha from the cutoff would move the product the shader cuts by, so it's encoded as a plain texture is.
        var surface = Strands();
        var color = Surface(204);
        TextureData[] textures = [color, surface];

        TextureCompression.Compress(textures, [Material(color: 0, surface: 1)]);

        var plain = TextureCompression.Compress(surface, TextureFormat.Bc7);
        for (int level = 0; Size >> level >= 4; level++)
        {
            Assert.Equal(plain.Mips[level], textures[1].Mips[level]);
        }
    }

    [Fact]
    public void A_colour_over_a_surface_with_alpha_keeps_the_products_coverage_once_compressed()
    {
        // At a cutoff of 0.5 over a surface alpha of 204, a colour byte passes from ceil(127.5 * 255 / 204) = 160 on,
        // not from 128 as on its own.
        var surface = Surface(204);
        var color = TextureData.KeepAlphaCoverage(Strands(), Cutoff, surface);
        TextureData[] textures = [color, surface];

        TextureCompression.Compress(textures, [Material(color: 0, surface: 1)]);

        for (int level = 1; Size >> level >= 4; level++)
        {
            int width = Size >> level;
            int before = 0;
            for (int i = 3; i < color.Mips[level].Length; i += 4)
            {
                if (color.Mips[level][i] >= 160)
                {
                    before++;
                }
            }

            int after = DecodedAlpha(textures[0], level).Count(alpha => alpha >= 160);
            int tolerance = Math.Max(2, width * width / 100);

            Assert.True(before > 0, $"Level {level} has no pixel that passes, so it can't show coverage being kept.");
            Assert.InRange(after, before - tolerance, before + tolerance);
        }
    }

    [Fact]
    public void Two_cut_outs_that_want_different_coverage_from_one_texture_are_warned_about()
    {
        var warnings = new List<string>();
        TextureData[] textures = [Strands()];

        TextureCompression.Compress(textures, [Material(color: 0, surface: -1, cutoff: 0.3f), Material(color: 0, surface: -1, cutoff: 0.5f)],
            warn: warnings.Add);

        Assert.Equal(
            "texture 0 is cut out differently by materials 0 and 1 (cutoff 0.3, then cutoff 0.5); it keeps the coverage of material 0.",
            Assert.Single(warnings));
    }

    [Fact]
    public void Two_cut_outs_that_agree_on_a_texture_are_not_warned_about()
    {
        var warnings = new List<string>();
        TextureData[] textures = [Strands()];

        TextureCompression.Compress(textures, [Material(color: 0, surface: -1), Material(color: 0, surface: -1)], warn: warnings.Add);

        Assert.Empty(warnings);
    }
}
