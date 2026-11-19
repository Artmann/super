namespace Rendition.Gpu.Tests;

// Texture arrays are made and let go of through Renderer.Assets, like textures.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class TextureArrayGpuTests(GpuFixture gpu)
{
    [Fact]
    public void A_destroyed_texture_array_cannot_be_destroyed_again()
    {
        var layer = TextureData.FromPixels(1, 1, TextureKind.Color, [128, 128, 128, 255]);
        var array = gpu.Renderer.Assets.CreateTextureArray([layer, layer]);
        gpu.Renderer.Assets.DestroyTextureArray(array);

        var error = Assert.Throws<ArgumentException>(() => gpu.Renderer.Assets.DestroyTextureArray(array));

        Assert.StartsWith("The texture array does not exist.", error.Message);
    }
}
