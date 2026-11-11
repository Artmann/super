using System.Numerics;
using Rendition;

namespace Super.Tests;

public class TextureEdgesTests
{
    private static ModelMaterial Material() => new()
    {
        BaseColor = Vector4.One,
        BaseColorTexture = 0,
        NormalTexture = -1,
        SurfaceTexture = -1,
        EmissiveTexture = -1,
    };

    private static ModelData Model(params ModelMaterialMasks?[] masks) => new()
    {
        Vertices = [],
        Indices = [],
        Parts = [],
        Materials = masks.Select(_ => Material()).ToArray(),
        Textures = [.. Enumerable.Range(0, 4).Select(_ => TextureData.FromPixels(1, 1, TextureKind.Color, [255, 255, 255, 255]))],
        MaterialMasks = masks,
    };

    private static readonly MaterialLayer Layer = new(Vector3.One, 0.5f, 1f);

    [Fact]
    public void Only_textures_a_material_uses_as_masks_clamp()
    {
        // The second material's masks are texture 2 and the third's texture 3. Textures 0 and 1 are colour.
        var model = Model(null, new ModelMaterialMasks(2, Layer, Layer), new ModelMaterialMasks(3, Layer, Layer));

        Assert.Equal(TextureEdges.Repeat, model.EdgesOf(0));
        Assert.Equal(TextureEdges.Repeat, model.EdgesOf(1));
        Assert.Equal(TextureEdges.Clamp, model.EdgesOf(2));
        Assert.Equal(TextureEdges.Clamp, model.EdgesOf(3));
    }

    [Fact]
    public void A_model_without_masks_repeats_every_texture()
    {
        var model = Model(null, null);

        Assert.Equal(TextureEdges.Repeat, model.EdgesOf(0));
        Assert.Equal(TextureEdges.Repeat, model.EdgesOf(3));
    }
}
