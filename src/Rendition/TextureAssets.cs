using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The renderer's textures and texture arrays, and the overlay's atlas: made by handing their bytes to the next frame,
/// and let go of two frames after they're destroyed. Part of <see cref="RendererAssets"/>.
/// </summary>
internal sealed class TextureAssets(
    SceneStores stores,
    Action<Func<BakeJob, bool>> stopBakesThat,
    FinishStage finish)
{
    /// <summary>See <see cref="RendererAssets.CreateTexture"/>.</summary>
    public TextureHandle Create(TextureData data, TextureEdges edges)
    {
        var texture = new GpuTexture(stores.Context, new TextureShape(data.Width, data.Height, data.Kind, (uint)data.Mips.Length, Format: data.Format));
        stores.Uploads.Add(texture.CreateStaging([data]), texture);
        try
        {
            return stores.Resources.AddTexture(texture, edges);
        }
        catch
        {
            // A full table has already let the texture go, so its copy must never ride a frame.
            stores.Uploads.Cancel(texture);
            throw;
        }
    }

    /// <summary>See <see cref="RendererAssets.CreateTextureArray"/>.</summary>
    public TextureArrayHandle CreateArray(ReadOnlySpan<TextureData> layers)
    {
        if (layers.IsEmpty)
        {
            throw new ArgumentException("A texture array needs at least one layer.", nameof(layers));
        }

        var first = layers[0];
        for (int i = 1; i < layers.Length; i++)
        {
            var layer = layers[i];
            if (layer.Width != first.Width || layer.Height != first.Height || layer.Kind != first.Kind || layer.Format != first.Format
                || layer.Mips.Length != first.Mips.Length)
            {
                throw new ArgumentException(
                    $"Layer {i} is {layer.Width}x{layer.Height} {layer.Kind} {layer.Format} with {layer.Mips.Length} mips, " +
                    $"but layer 0 is {first.Width}x{first.Height} {first.Kind} {first.Format} with {first.Mips.Length} mips.",
                    nameof(layers));
            }
        }

        var texture = new GpuTexture(stores.Context, new TextureShape(first.Width, first.Height, first.Kind, (uint)first.Mips.Length, (uint)layers.Length, first.Format) { Array = true });
        stores.Uploads.Add(texture.CreateStaging(layers), texture);
        return stores.Resources.AddTextureArray(texture);
    }

    /// <summary>See <see cref="RendererAssets.DestroyTexture"/>.</summary>
    public void Destroy(TextureHandle texture)
    {
        if (stores.Resources.Defaults.Holds(texture) || !stores.Resources.Textures.IsLive(texture.Index))
        {
            throw new ArgumentException("The texture does not exist.", nameof(texture));
        }

        QueueDestroy(texture.Index);
    }

    /// <summary>See <see cref="RendererAssets.DestroyTextureArray"/>.</summary>
    public void DestroyArray(TextureArrayHandle array)
    {
        if (stores.Resources.Defaults.Holds(array) || !stores.Resources.TextureArrays.IsLive(array.Index))
        {
            throw new ArgumentException("The texture array does not exist.", nameof(array));
        }

        stores.Uploads.Cancel(stores.Resources.TextureArrays.MarkDying(array.Index));
        stores.Retired.AfterLastSubmitted.FreedTextureArrays.Add(array.Index);
    }

    /// <summary>Lets go of a texture a model's material names, unless it's a default or already gone.</summary>
    public void DestroyIfLive(int slot)
    {
        var handle = new TextureHandle(slot);
        if (slot >= 0 && !stores.Resources.Defaults.Holds(handle) && stores.Resources.Textures.IsLive(handle.Index))
        {
            QueueDestroy(slot);
        }
    }

    /// <summary>See <see cref="RendererAssets.SetOverlayAtlas"/>.</summary>
    public void SetOverlayAtlas(TextureData data) => finish.SetOverlayAtlas(data);

    /// <summary>Marks a live texture as gone, drops any copy still waiting for it, and queues its release.</summary>
    private void QueueDestroy(int slot)
    {
        stores.ShadowWorld.Changed();
        // A background bake whose materials read it would trace a freed image.
        stopBakesThat(job => BakeReads(job, slot));

        stores.Uploads.Cancel(stores.Resources.Textures.MarkDying(slot));
        stores.Retired.AfterLastSubmitted.FreedTextures.Add(slot);
    }

    // Whether a material of one of the job's draws names the texture in this slot.
    private bool BakeReads(BakeJob job, int slot)
    {
        foreach (ref readonly var draw in job.Area.Draws.AsSpan())
        {
            if (!stores.Resources.Materials.IsLive(draw.Material))
            {
                continue;
            }

            foreach (int texture in stores.Resources.Materials.TexturesOf(draw.Material))
            {
                if (texture == slot)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
