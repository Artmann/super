using System.Globalization;
using System.Numerics;
using Rendition;

namespace Super.Tests;

public class CutoutProblemTests
{
    // A 2x1 texture whose two pixels have these alphas.
    private static TextureData Texture(byte first, byte second) =>
        TextureData.FromPixels(2, 1, TextureKind.Color, [255, 255, 255, first, 255, 255, 255, second]);

    private static ModelData Model(float cutoff, TextureData? color, TextureData? surface, float opacity = 1f)
    {
        var textures = new List<TextureData>();
        int Add(TextureData? texture)
        {
            if (texture is null)
            {
                return -1;
            }

            textures.Add(texture);
            return textures.Count - 1;
        }

        var material = new ModelMaterial
        {
            BaseColor = new Vector4(1f, 1f, 1f, opacity),
            AlphaCutoff = cutoff,
            BaseColorTexture = Add(color),
            NormalTexture = -1,
            SurfaceTexture = Add(surface),
            EmissiveTexture = -1,
        };
        return new ModelData
        {
            Vertices = [],
            Indices = [],
            Parts = [],
            Materials = [material],
            Textures = textures.ToArray(),
        };
    }

    [Fact]
    public void Leaf_alpha_in_the_colour_texture_is_enough()
    {
        Assert.Null(Model(0.5f, color: Texture(0, 255), surface: null).CutoutProblem(0));
    }

    [Fact]
    public void Leaf_alpha_in_the_surface_texture_is_enough()
    {
        Assert.Null(Model(0.5f, color: Texture(255, 255), surface: Texture(0, 255)).CutoutProblem(0));
    }

    [Fact]
    public void A_material_without_a_cutoff_has_no_problem()
    {
        Assert.Null(Model(0f, color: null, surface: null).CutoutProblem(0));
    }

    [Fact]
    public void A_cutoff_with_no_texture_to_cut_by_is_explained()
    {
        string? problem = Model(0.5f, color: null, surface: null).CutoutProblem(0);

        Assert.Equal("alpha cutoff 0.5 has nothing to cut by, because the material has no colour or surface texture.", problem);
    }

    [Fact]
    public void A_cutoff_above_no_pixel_is_explained()
    {
        string? problem = Model(0.5f, color: Texture(200, 255), surface: Texture(255, 255)).CutoutProblem(0);

        Assert.Equal("alpha cutoff 0.5 cuts nothing out, because no pixel of its colour or surface texture has alpha below it.", problem);
    }

    [Fact]
    public void A_cutoff_above_every_pixel_is_explained()
    {
        string? problem = Model(0.5f, color: Texture(0, 100), surface: null).CutoutProblem(0);

        Assert.Equal("alpha cutoff 0.5 cuts out every pixel, because none has alpha that high.", problem);
    }

    [Fact]
    public void The_colour_and_surface_alphas_multiply()
    {
        // Neither texture goes below 0.5 alone, but 0.6 times 0.6 is 0.36. 153 is 0.6.
        Assert.Null(Model(0.5f, color: Texture(153, 255), surface: Texture(153, 255)).CutoutProblem(0));
    }

    [Fact]
    public void The_colour_constant_lowers_the_opacity_too()
    {
        // The texture alone is fully opaque, but the colour's alpha of 0.4 is below the cutoff.
        string? problem = Model(0.5f, color: Texture(255, 255), surface: null, opacity: 0.4f).CutoutProblem(0);

        Assert.Equal("alpha cutoff 0.5 cuts out every pixel, because none has alpha that high.", problem);
    }

    [Fact]
    public void The_cutoff_is_written_the_same_in_every_culture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            string? problem = Model(0.5f, color: null, surface: null).CutoutProblem(0);

            Assert.StartsWith("alpha cutoff 0.5 ", problem);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
