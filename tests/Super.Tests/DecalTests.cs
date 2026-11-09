using System.Globalization;
using System.Numerics;
using Rendition;
using Rendition.Passes;
using Super.Testing;

namespace Super.Tests;

/// <summary>Decals: what a view accepts, and how they are packed for the GPU.</summary>
public class DecalTests
{
    private static readonly Surface Grime = new() { Color = new Vector3(0.3f, 0.25f, 0.2f) };

    [Fact]
    public void A_257th_decal_is_refused()
    {
        var view = new View();
        for (int i = 0; i < 256; i++)
        {
            view.Decal(i, Matrix4x4.Identity, Grime);
        }

        var error = Assert.Throws<InvalidOperationException>(() => view.Decal(256, Matrix4x4.Identity, Grime));

        Assert.Equal("At most 256 decals fit in a frame.", error.Message);
        Assert.Equal(256, view.DrawnDecals.Length);
    }

    [Fact]
    public void Packing_more_than_256_decals_is_refused()
    {
        var decals = new DecalDraw[257];
        Array.Fill(decals, new DecalDraw(Matrix4x4.Identity, new MaterialHandle(0), new DecalBlend()));

        var error = Assert.Throws<ArgumentException>(() => DecalBatch.Pack(decals, new GpuDecal[257]));

        Assert.StartsWith("A frame can draw at most 256 decals, but it was given 257.", error.Message);
    }

    [Theory]
    [InlineData(nameof(DecalBlend.Color), 1.5f, "1.5")]
    [InlineData(nameof(DecalBlend.Normal), -0.25f, "-0.25")]
    [InlineData(nameof(DecalBlend.Roughness), float.NaN, "NaN")]
    [InlineData(nameof(DecalBlend.AngleFade), 2f, "2")]
    public void Blend_weights_outside_0_to_1_are_refused(string field, float weight, string shown)
    {
        var blend = field switch
        {
            nameof(DecalBlend.Color) => new DecalBlend { Color = weight },
            nameof(DecalBlend.Normal) => new DecalBlend { Normal = weight },
            nameof(DecalBlend.Roughness) => new DecalBlend { Roughness = weight },
            _ => new DecalBlend { AngleFade = weight },
        };
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var view = new View();

            var error = Assert.Throws<ArgumentException>(() => view.Decal(1, Matrix4x4.Identity, Grime, blend));

            Assert.StartsWith($"The decal's blend {field} is {shown}, but it must be from 0 to 1.", error.Message);
            Assert.Equal("blend", error.ParamName);
            Assert.Empty(view.DrawnDecals.ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Blend_weights_of_exactly_0_and_1_are_accepted()
    {
        var view = new View();

        view.Decal(1, Matrix4x4.Identity, Grime, new DecalBlend(0f, 1f, 0f, 1f));

        Assert.Equal(new DecalBlend(0f, 1f, 0f, 1f), view.DrawnDecals[0].Blend);
    }

    [Fact]
    public void A_decal_matrix_that_is_not_invertible_is_refused()
    {
        var view = new View();
        var flat = Matrix4x4.CreateScale(2f, 0f, 1f);

        var error = Assert.Throws<ArgumentException>(() => view.Decal(1, flat, Grime));

        Assert.StartsWith("The decal's box can't be inverted: it is squashed flat on some axis", error.Message);
        Assert.Equal("box", error.ParamName);
        Assert.Empty(view.DrawnDecals.ToArray());
    }

    [Fact]
    public void A_decal_matrix_holding_a_number_that_is_not_finite_is_refused()
    {
        var view = new View();
        var box = Matrix4x4.CreateTranslation(float.NaN, 0f, 0f);

        var error = Assert.Throws<ArgumentException>(() => view.Decal(1, box, Grime));

        Assert.StartsWith("The decal's box can't be inverted", error.Message);
    }

    [Fact]
    public void Decals_pack_the_inverse_box_and_the_weights()
    {
        var box = Matrix4x4.CreateScale(2f, 0.5f, 4f) * Matrix4x4.CreateTranslation(3f, -1f, 7f);
        var blend = new DecalBlend(0.25f, 0.5f, 0.75f, 0.1f);
        var packed = new GpuDecal[1];

        DecalBatch.Pack([new DecalDraw(box, new MaterialHandle(9), blend)], packed);

        // Worked by hand: undo the move, then the scale.
        var inverse = packed[0].Inverse;
        Assert.Equal(new Vector3(-3f / 2f, 1f / 0.5f, -7f / 4f), inverse.Translation);
        Assert.Equal(0.5f, inverse.M11, 1e-6f);
        Assert.Equal(2f, inverse.M22, 1e-6f);
        Assert.Equal(0.25f, inverse.M33, 1e-6f);
        Assert.Equal(box, packed[0].Box);
        Assert.Equal(new Vector4(0.25f, 0.5f, 0.75f, 0.1f), packed[0].Weights);
        Assert.Equal(9u, packed[0].Material);
    }

    [Fact]
    public void A_decal_without_a_blend_lays_on_the_whole_material_and_fades_past_60_degrees()
    {
        var view = new View();

        view.Decal(1, Matrix4x4.Identity, Grime);

        Assert.Equal(new DecalBlend(1f, 1f, 1f, 0.5f), view.DrawnDecals[0].Blend);
        Assert.Equal(new DecalBlend(1f, 1f, 1f, 0.5f), new DecalBlend());
    }

    [Fact]
    public void A_decal_key_can_be_placed_once_a_frame()
    {
        var view = new View();
        view.Decal(4, Matrix4x4.Identity, Grime);

        var error = Assert.Throws<ArgumentException>(() => view.Decal(4, Matrix4x4.Identity, Grime));

        Assert.StartsWith("Decal 4 was already placed this frame. Give each decal its own key.", error.Message);
        Assert.Single(view.DrawnDecals.ToArray());
    }

    [Fact]
    public void A_decal_with_a_surface_shader_is_refused()
    {
        var content = new Content(new MemoryContent());
        var view = new View { Content = content };
        var shaded = new Surface { Shader = content.Shader("ripples") };

        var error = Assert.Throws<ArgumentException>(() => view.Decal(1, Matrix4x4.Identity, shaded));

        Assert.StartsWith("A decal can't use a surface shader.", error.Message);
    }

    [Fact]
    public void A_decal_naming_a_texture_the_content_never_named_is_refused()
    {
        var view = new View { Content = new Content(new MemoryContent()) };
        var surface = new Surface { NormalTexture = new TextureRef(40) };

        var error = Assert.Throws<ArgumentException>(() => view.Decal(1, Matrix4x4.Identity, surface));

        Assert.StartsWith("NormalTexture names 40, but nothing was named with that index.", error.Message);
        Assert.Empty(view.DrawnDecals.ToArray());
    }
}
