using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The space bake whose lightmaps and probes are on the GPU, kept while it isn't drawn so turning it back on reads
/// nothing. Its pages go in the material table, three to a lightmap page, irradiance, direction and mask, and then its
/// probe atlases, so they live and die with the lightmaps. See "Drawing a bake" in docs/design.md.
/// </summary>
internal sealed class SpaceBake(
    ReadableBakes readable, Func<TextureData, TextureHandle> clampedTexture, Action<TextureHandle> destroyTexture)
{
    private LoadedBake _loaded;

    /// <summary>The combined inputs of the bake loaded, or 0 for none.</summary>
    public ulong Inputs => _loaded.Inputs;

    /// <summary>Each draw's lightmap rectangle, by bake id, or null when the store gives no lightmaps.</summary>
    public Dictionary<ulong, GpuLightmapRect>? Rects => _loaded.Rects;

    /// <summary>Each listed local light's shadow mask channel, by key.</summary>
    public Dictionary<int, int>? Channels => _loaded.Channels;

    /// <summary>The bake's light probes, whose atlases are among its pages, or null.</summary>
    public BakedProbes? Probes => _loaded.Probes;

    /// <summary>
    /// Makes sure the space's fresh bake, in <paramref name="file"/>, has its lightmaps on the GPU, reading it when it
    /// isn't the one already loaded. False, logged once, when it can't be read; then the file counts as missing until the
    /// store reloads.
    /// </summary>
    public bool Load(string file, BakeInputs inputs)
    {
        var store = readable.Inner;
        ulong combined = inputs.Combined;
        if (_loaded.File == file && _loaded.Inputs == combined && ReferenceEquals(_loaded.Store, store)
            && _loaded.Reloads == store.Reloads)
        {
            return true;
        }

        Unload();
        if (!TryRead(store, file, out var bake))
        {
            return false;
        }

        // A store with no Load of its own still has the area baked; its surfaces take their soft light live.
        var pages = _loaded.Pages ?? [];
        Dictionary<ulong, GpuLightmapRect>? rects = null;
        Dictionary<int, int>? channels = null;
        BakedProbes? probes = null;
        if (bake is not null)
        {
            channels = [];
            foreach (var (lightKey, channel) in bake.Channels)
            {
                channels[lightKey] = channel;
            }

            (rects, probes) = UploadPages(bake, pages);
        }

        _loaded = new LoadedBake(file, combined, store, store.Reloads, rects, pages, channels, probes);
        return true;
    }

    /// <summary>
    /// Reads and checks a space's bake. Null for a store with no Load of its own. False, logged once, when it can't be
    /// read; then the file counts as missing until the store reloads.
    /// </summary>
    private bool TryRead(IBakeStore store, string file, out LightBakeFile? bake)
    {
        try
        {
            bake = store.Load(file);
            if (bake is not null)
            {
                BakePages.CheckSpace(bake, file);
            }
            else if (readable.GivesLightmaps(store))
            {
                // Its header was fresh a moment ago, so the file was removed or renamed since, without a reload.
                throw new InvalidDataException("The file is gone, though its header was read as fresh.");
            }

            return true;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The bake {file} can't be read, so its area is lit live: {e.Message}");
            readable.Unreadable.Add(file);
            bake = null;
            return false;
        }
    }

    /// <summary>
    /// Puts a space bake's pages in the material table, three to a lightmap page, irradiance, direction and mask, and
    /// then its probe atlases. Returns each draw's rectangle, and the probes.
    /// </summary>
    private (Dictionary<ulong, GpuLightmapRect> Rects, BakedProbes? Probes) UploadPages(LightBakeFile bake, List<TextureHandle> pages)
    {
        try
        {
            // CheckPages made sure a space's bake has both.
            var irradiancePages = bake.Irradiance!;
            var directionPages = bake.Direction!;
            int count = irradiancePages.Count;
            for (int page = 0; page < count; page++)
            {
                pages.Add(clampedTexture(BakePages.Texture(irradiancePages[page])));
                pages.Add(clampedTexture(BakePages.Texture(directionPages[page])));
                pages.Add(page < bake.Mask.Count ? clampedTexture(BakePages.Texture(bake.Mask[page])) : new TextureHandle(-1));
            }

            var rects = BakePages.Rects(bake, pages);
            BakedProbes? probes = null;
            if (bake is { ProbeIrradiance: { } probeIrradiance, ProbeDepth: { } probeDepth, Probes: { } probeSettings })
            {
                pages.Add(clampedTexture(BakePages.Texture(probeIrradiance)));
                pages.Add(clampedTexture(BakePages.Texture(probeDepth)));
                probes = new BakedProbes(probeSettings, pages[^2].Index, pages[^1].Index);
            }

            return (rects, probes);
        }
        catch
        {
            // Such as running out of device memory part way: the pages made so far go the usual way, and the
            // error goes on, since it isn't the file's fault.
            ReleasePages(pages);
            throw;
        }
    }

    /// <summary>Lets the loaded bake's pages go, once the frames drawing them are done.</summary>
    private void Unload()
    {
        if (_loaded.Pages is { } pages)
        {
            ReleasePages(pages);
        }

        _loaded = _loaded with { File = null, Inputs = 0, Store = null, Rects = null, Channels = null, Probes = null };
    }

    /// <summary>Lets a bake's pages go, once the frames drawing them are done, and empties the list.</summary>
    private void ReleasePages(List<TextureHandle> pages)
    {
        foreach (var page in pages)
        {
            if (page.Index >= 0)
            {
                destroyTexture(page);
            }
        }

        pages.Clear();
    }

    /// <summary>
    /// The bake whose lightmaps are uploaded, and which file, inputs and store reload it came from. Channels is each
    /// listed local light's shadow mask channel, by key. Probes is its light probes, whose atlases are among Pages.
    /// </summary>
    private record struct LoadedBake(
        string? File, ulong Inputs, IBakeStore? Store, int Reloads, Dictionary<ulong, GpuLightmapRect>? Rects, List<TextureHandle> Pages,
        Dictionary<int, int>? Channels, BakedProbes? Probes);

    /// <summary>A bake's light probes: their grid, and the material table slots of their irradiance and depth atlases.</summary>
    public sealed record BakedProbes(ProbeVolumeSettings Settings, int Irradiance, int Depth);
}
