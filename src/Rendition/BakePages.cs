using System.Numerics;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>A bake file's pages: the checks a space's bake passes before it is drawn, and how a page goes on the GPU.</summary>
internal static class BakePages
{
    /// <summary>
    /// Throws when a bake's pages don't fit together, as a damaged file's might not, or when it has no irradiance and
    /// direction pages, which a space needs (only a chunk may leave them out).
    /// </summary>
    public static void CheckSpace(LightBakeFile bake, string file)
    {
        if (bake is not { Irradiance: { } irradiance, Direction: { } direction })
        {
            throw new InvalidDataException(
                $"The bake {file} has no irradiance and direction pages, but a space's bake needs them: only a chunk's may leave them out.");
        }

        if (direction.Count != irradiance.Count || bake.Mask.Count > irradiance.Count)
        {
            throw new InvalidDataException(
                $"The bake has {irradiance.Count} irradiance pages but {direction.Count} direction and {bake.Mask.Count} mask pages.");
        }

        var probePages = CheckProbes(bake);
        foreach (var page in irradiance.Concat(direction).Concat(bake.Mask).Concat(probePages))
        {
            CheckSize(page);
        }

        foreach (var (bakeId, rect) in bake.Draws)
        {
            if (rect.Page < 0 || rect.Page >= irradiance.Count)
            {
                throw new InvalidDataException(
                    $"Draw 0x{bakeId:X16} is on page {rect.Page}, but the bake has {irradiance.Count} pages.");
            }
        }
    }

    /// <summary>The texture format a page's pixels are in. Throws for a format no reader knows.</summary>
    public static TextureFormat Format(LightBakePageFormat format) => format switch
    {
        LightBakePageFormat.Rgba16f => TextureFormat.Rgba16f,
        LightBakePageFormat.Rgba8 => TextureFormat.Rgba8,
        LightBakePageFormat.Bc6h => TextureFormat.Bc6h,
        LightBakePageFormat.Bc7 => TextureFormat.Bc7,
        _ => throw new InvalidDataException($"A page has the unknown format {(int)format}."),
    };

    /// <summary>A page as a texture. Linear, so RGBA8 and BC7 pages are read as they were written rather than as sRGB colour.</summary>
    public static TextureData Texture(LightBakePage page) => new()
    {
        Width = page.Width,
        Height = page.Height,
        Kind = TextureKind.Linear,
        Format = Format(page.Format),
        Mips = [page.Pixels],
    };

    /// <summary>
    /// Each of the bake's draws' rectangles, on the material table slots of its page's irradiance, direction and mask,
    /// which <paramref name="pages"/> holds three to a lightmap page.
    /// </summary>
    public static Dictionary<ulong, GpuLightmapRect> Rects(LightBakeFile bake, List<TextureHandle> pages)
    {
        Dictionary<ulong, GpuLightmapRect> rects = [];
        foreach (var (bakeId, rect) in bake.Draws)
        {
            rects[bakeId] = new GpuLightmapRect
            {
                ScaleOffset = new Vector4(rect.Scale, rect.Offset.X, rect.Offset.Y),
                Irradiance = pages[rect.Page * 3].Index,
                Direction = pages[rect.Page * 3 + 1].Index,
                Mask = pages[rect.Page * 3 + 2].Index,
                Page = rect.Page,
            };
        }

        return rects;
    }

    // Throws when a page's pixels aren't as many bytes as its size and format need.
    private static void CheckSize(LightBakePage page)
    {
        if (page.Width < 1 || page.Height < 1
            || page.Pixels.Length != TextureData.LevelSize(Format(page.Format), page.Width, page.Height))
        {
            throw new InvalidDataException($"A {page.Width}x{page.Height} {page.Format} page holds {page.Pixels.Length} bytes.");
        }
    }

    /// <summary>
    /// Throws when a bake's light probes don't fit together: their atlases and grid come all or none, the grid holds at
    /// most <see cref="BakeFiles.MaxSpaceProbes"/>, and each atlas is as wide as the live volume's and tall enough for
    /// every probe's tile, so probes.glsl reads them as it reads the live ones. Gives the atlases, to check as pages.
    /// </summary>
    private static LightBakePage[] CheckProbes(LightBakeFile bake)
    {
        if (bake is { ProbeIrradiance: null, ProbeDepth: null, Probes: null })
        {
            return [];
        }

        if (bake is not { ProbeIrradiance: { } irradiance, ProbeDepth: { } depth, Probes: { } settings })
        {
            throw new InvalidDataException(
                "The bake has some of its light probes' irradiance atlas, depth atlas and grid, but not all three.");
        }

        if (!float.IsFinite(settings.Spacing) || settings.Spacing <= 0f || !float.IsFinite(settings.Min.X + settings.Min.Y + settings.Min.Z)
            || !float.IsFinite(settings.Max.X + settings.Max.Y + settings.Max.Z))
        {
            throw new InvalidDataException($"The bake's light probes are {settings.Spacing} m apart in {settings.Min} to {settings.Max}.");
        }

        long total = ProbeGrid.Total(settings);
        if (total > BakeFiles.MaxSpaceProbes)
        {
            throw new InvalidDataException($"The bake has {total} light probes, more than the {BakeFiles.MaxSpaceProbes} a space may have.");
        }

        int rows = (int)((total + BakedProbeAtlas.Columns - 1) / BakedProbeAtlas.Columns);
        CheckAtlas(irradiance, "irradiance", BakedProbeAtlas.IrradianceAtlasSize, rows * (ProbeVolume.IrradianceTexels + 2));
        CheckAtlas(depth, "depth", BakedProbeAtlas.DepthAtlasSize, rows * (ProbeVolume.DepthTexels + 2));
        return [irradiance, depth];
    }

    private static void CheckAtlas(LightBakePage atlas, string name, int width, int minimumHeight)
    {
        if (atlas.Width != width || atlas.Height < minimumHeight)
        {
            throw new InvalidDataException(
                $"The bake's {atlas.Width}x{atlas.Height} probe {name} atlas should be {width} wide and at least {minimumHeight} tall.");
        }
    }
}
