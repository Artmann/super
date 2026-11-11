using System.Numerics;

namespace Rendition.Gpu.Tests;

// These build the FrameView directly, so they prove the renderer's own checks and not only the view's.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class BakedAreaGpuTests(GpuFixture gpu)
{
    [Fact]
    public void Frame_with_257_chunks_is_explained()
    {
        int errors = gpu.ValidationErrors;
        var chunks = new BakedChunk[257];
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i] = new BakedChunk("room", Matrix4x4.CreateTranslation(i * 20f, 0f, 0f), -Vector3.One, Vector3.One);
        }

        var error = Assert.Throws<ArgumentException>(() => gpu.RenderOneFrame(null, chunks));

        Assert.Contains("257", error.Message);
        Assert.Contains("256", error.Message);
        AFrameStillRenders(errors);
    }

    [Fact]
    public void Frame_with_a_sheared_chunk_is_explained()
    {
        int errors = gpu.ValidationErrors;
        var sheared = Matrix4x4.Identity;
        sheared.M21 = 0.5f;
        BakedChunk[] chunks =
        [
            new("cottage", Matrix4x4.Identity, -Vector3.One, Vector3.One),
            new("barn", sheared, -Vector3.One, Vector3.One),
        ];

        var error = Assert.Throws<ArgumentException>(() => gpu.RenderOneFrame(null, chunks));

        Assert.Contains("Baked chunk 1", error.Message);
        Assert.Contains("\"barn\"", error.Message);
        Assert.Contains("rotation and translation", error.Message);
        AFrameStillRenders(errors);
    }

    [Fact]
    public void Frame_with_a_baked_space_and_turned_chunks_renders()
    {
        int errors = gpu.ValidationErrors;
        var space = new BakedSpace("crypt", new Vector3(-8f, -1f, -12f), new Vector3(8f, 6f, 12f), 1f);
        BakedChunk[] chunks =
        [
            new("cottage", Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(10f, 0f, 3f), -Vector3.One, Vector3.One),
            new("cottage", Matrix4x4.CreateTranslation(-10f, 0f, 3f), -Vector3.One, Vector3.One),
        ];

        gpu.RenderOneFrame(space, chunks);

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // The checks throw before the frame starts, so the renderer is left able to draw the next one.
    private void AFrameStillRenders(int errors)
    {
        gpu.RenderOneFrame();
        Assert.Equal(errors, gpu.ValidationErrors);
    }
}
