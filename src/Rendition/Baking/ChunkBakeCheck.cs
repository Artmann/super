namespace Rendition;

/// <summary>
/// Checks a chunk's bake before <see cref="ChunkBakePool"/> puts it on the GPU, on the worker thread that read it.
/// </summary>
internal static class ChunkBakeCheck
{
    /// <summary>
    /// Throws when a chunk's bake can't be drawn as its pages say: it must have its sky and sun transfer, each transfer
    /// page in the format the shader reads it as and all the size of the sky red page, at most one plain page of each
    /// kind that size, pages whose bytes fit their size, and every draw on its one page.
    /// </summary>
    /// <exception cref="InvalidDataException">The bake is damaged or wrongly made.</exception>
    internal static void CheckPages(LightBakeFile bake, string file)
    {
        var transferPages = CheckTransfer(bake, file);
        var size = bake.Transfer!.SkyRed;
        var plainPages = CheckPlainPages(bake, file, size);
        CheckBytes(plainPages.Concat(transferPages.Select(p => (p.Page, p.Name + " transfer"))), file);
        CheckDrawsOnOnePage(bake, file);
    }

    // The transfer pages, by name, once each is in the format the shader reads it as and the size of the sky red page.
    private static (LightBakePage Page, string Name)[] CheckTransfer(LightBakeFile bake, string file)
    {
        if (bake.Transfer is not { } transfer)
        {
            throw new InvalidDataException($"The chunk bake {file} has no sky and sun transfer, which every chunk's bake needs.");
        }

        (LightBakePage Page, string Name, LightBakePageFormat Format)[] transferPages =
        [
            (transfer.SkyRed, "sky red", LightBakePageFormat.Rgba16f),
            (transfer.SkyGreen, "sky green", LightBakePageFormat.Rgba16f),
            (transfer.SkyBlue, "sky blue", LightBakePageFormat.Rgba16f),
            (transfer.Sun, "sun", LightBakePageFormat.Rgba16f),
            (transfer.SunTint, "sun tint", LightBakePageFormat.Rgba8),
        ];
        foreach (var (page, name, format) in transferPages)
        {
            if (page.Format != format)
            {
                throw new InvalidDataException($"The chunk bake {file}'s {name} transfer page is {page.Format}, but it must be {format}.");
            }
        }

        var size = transfer.SkyRed;
        if (size.Width < 1 || size.Height < 1)
        {
            throw new InvalidDataException($"The chunk bake {file}'s sky red transfer page is {size.Width}x{size.Height}, which holds no texels.");
        }

        foreach (var (page, name, _) in transferPages)
        {
            if (page.Width != size.Width || page.Height != size.Height)
            {
                throw new InvalidDataException(
                    $"The chunk bake {file}'s {name} transfer page is {page.Width}x{page.Height}, but its sky red page is {size.Width}x{size.Height}.");
            }
        }

        return [.. transferPages.Select(p => (p.Page, p.Name))];
    }

    // The plain pages, by name, once there is at most one of each kind, as many direction pages as irradiance ones, and
    // each is the size of the sky red page.
    private static List<(LightBakePage Page, string Name)> CheckPlainPages(
        LightBakeFile bake, string file, LightBakePage size)
    {
        int irradiance = bake.Irradiance?.Count ?? 0;
        int direction = bake.Direction?.Count ?? 0;
        if (direction != irradiance)
        {
            throw new InvalidDataException(
                $"The chunk bake {file} has {irradiance} irradiance pages but {direction} direction pages.");
        }

        // A chunk lit only by the sky and suns has no plain light, so no irradiance page, but the baker still writes
        // its mask page.
        if (irradiance > 1 || bake.Mask.Count > 1)
        {
            throw new InvalidDataException(
                $"The chunk bake {file} has {irradiance} irradiance and {bake.Mask.Count} mask pages, but a chunk's bake has at most one of each.");
        }

        var plainPages = new List<(LightBakePage Page, string Name)>();
        if (irradiance == 1)
        {
            plainPages.Add((bake.Irradiance![0], "irradiance"));
            plainPages.Add((bake.Direction![0], "direction"));
        }

        if (bake.Mask.Count == 1)
        {
            plainPages.Add((bake.Mask[0], "mask"));
        }

        foreach (var (page, name) in plainPages)
        {
            if (page.Width != size.Width || page.Height != size.Height)
            {
                throw new InvalidDataException(
                    $"The chunk bake {file}'s {name} page is {page.Width}x{page.Height}, but its sky red page is {size.Width}x{size.Height}.");
            }
        }

        return plainPages;
    }

    private static void CheckBytes(IEnumerable<(LightBakePage Page, string Name)> pages, string file)
    {
        foreach (var (page, name) in pages)
        {
            int needed = TextureData.LevelSize(Renderer.PageFormat(page.Format), page.Width, page.Height);
            if (page.Pixels.Length != needed)
            {
                throw new InvalidDataException(
                    $"The chunk bake {file}'s {page.Width}x{page.Height} {page.Format} {name} page holds {page.Pixels.Length} bytes, but it needs {needed}.");
            }
        }
    }

    private static void CheckDrawsOnOnePage(LightBakeFile bake, string file)
    {
        foreach (var (bakeId, rect) in bake.Draws)
        {
            if (rect.Page != 0)
            {
                throw new InvalidDataException(
                    $"The chunk bake {file} puts draw 0x{bakeId:X16} on page {rect.Page}, but a chunk's bake has one page.");
            }
        }
    }
}
