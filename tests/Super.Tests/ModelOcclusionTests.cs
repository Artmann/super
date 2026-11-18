using System.Numerics;
using Rendition;

namespace Super.Tests;

/// <summary>
/// A model's occlusion textures, which its file keeps in a section after the textures, left out when no material has one.
/// </summary>
public class ModelOcclusionTests
{
    private static ModelMaterial Material(MaterialFlags flags = MaterialFlags.None) => new()
    {
        BaseColor = Vector4.One,
        BaseColorTexture = 0,
        NormalTexture = -1,
        SurfaceTexture = -1,
        EmissiveTexture = -1,
        HeightTexture = -1,
        DetailColorTexture = -1,
        DetailNormalTexture = -1,
        Flags = flags,
    };

    private static ModelData Model(int[] occlusion, params ModelMaterial[] materials) => new()
    {
        Vertices = [],
        Indices = [],
        Parts = [],
        Materials = materials,
        Textures = [.. Enumerable.Range(0, 3).Select(i => TextureData.FromPixels(1, 1, TextureKind.Linear, [(byte)(i * 100), 0, 0, 255]))],
        MaterialOcclusion = occlusion,
    };

    [Fact]
    public void Each_materials_occlusion_texture_comes_back_from_the_file()
    {
        var model = Model([-1, 2], Material(), Material(MaterialFlags.OcclusionOnUv2));

        var read = ModelFile.Read(new MemoryStream(model.ToBytes()), "a.model");

        Assert.Equal([-1, 2], read.MaterialOcclusion);
        Assert.Equal(-1, read.OcclusionOf(0));
        Assert.Equal(2, read.OcclusionOf(1));
        Assert.Equal(MaterialFlags.OcclusionOnUv2, read.Materials[1].Flags);
    }

    // A model without occlusion must keep the bytes it had before the section existed, since its bake ids are made from
    // them: the section only adds to the end.
    [Fact]
    public void A_model_without_occlusion_writes_the_same_bytes_as_one_with_it_up_to_the_section()
    {
        byte[] without = Model([], Material(), Material()).ToBytes();
        byte[] with = Model([-1, 2], Material(), Material()).ToBytes();

        // One byte marks the section, then the array's length and its two entries, four bytes each.
        Assert.Equal(without.Length + 1 + 4 + 2 * 4, with.Length);
        Assert.Equal(without, with[..without.Length]);
        Assert.Empty(ModelFile.Read(new MemoryStream(without), "a.model").MaterialOcclusion);
        Assert.Equal(-1, ModelFile.Read(new MemoryStream(without), "a.model").OcclusionOf(1));
    }

    [Fact]
    public void An_unknown_section_after_the_textures_is_damage()
    {
        byte[] bytes = [.. Model([], Material()).ToBytes(), 7];

        var error = Assert.Throws<InvalidDataException>(() => ModelFile.Read(new MemoryStream(bytes), "a.model"));

        Assert.Equal("a.model is damaged: it has the unknown section 7 after its textures. Cook it again.", error.Message);
    }

    [Fact]
    public void Occlusion_for_some_materials_but_not_all_is_refused()
    {
        var model = Model([2], Material(), Material());

        var error = Assert.Throws<InvalidOperationException>(() => model.ToBytes());

        Assert.Equal(
            "The model has 2 materials and 1 occlusion textures. Give one to each material, -1 for none, or none at all.",
            error.Message);
    }

    [Fact]
    public void Only_occlusion_on_the_second_uv_set_clamps()
    {
        // Material 0 reads texture 1 through its first set, which tiles; material 1 reads texture 2 through its second.
        var model = Model([1, 2], Material(), Material(MaterialFlags.OcclusionOnUv2));

        Assert.Equal(TextureEdges.Repeat, model.EdgesOf(0));
        Assert.Equal(TextureEdges.Repeat, model.EdgesOf(1));
        Assert.Equal(TextureEdges.Clamp, model.EdgesOf(2));
    }
}
