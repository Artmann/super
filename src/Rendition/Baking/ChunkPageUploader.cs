using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// Copies chunk bakes' pages to the GPU a band of rows at a time, at most <see cref="Budget"/> bytes a frame, and puts
/// each finished page in the material table. Part of <see cref="ChunkBakePool"/>.
/// </summary>
internal sealed unsafe class ChunkPageUploader(VulkanContext context, ResourceTable resources, long budget)
{
    private readonly List<Band> _bands = [];

    /// <summary>The most bytes of pages copied in one frame.</summary>
    public long Budget { get; set; } = budget;

    private readonly record struct Band(ChunkBakePool.Entry Entry, ChunkBakePool.Page Page, int FirstRow, int Rows, int Bytes);

    // Copies a band of each waiting page, oldest bake first, up to the budget, and at least one row so even a page
    // wider than the budget moves. A page whose last band goes up joins the material table. When the table is full, the
    // band isn't recorded and the page's bake is added to refused, for the pool to drop: the table would free a refused
    // texture at once, while this frame's and older frames' copies may still be writing it.
    public void Record(
        CommandBuffer cmd, List<ChunkBakePool.Entry> uploading, List<(ChunkBakePool.Entry Entry, string Reason)> refused,
        List<IDisposable> others)
    {
        long total = PlanBands(uploading);
        if (total > 0)
        {
            var staging = GpuBuffer.CreateHostVisible(context, (ulong)total, BufferUsageFlags.TransferSrcBit);
            others.Add(staging);
            CopyBands(cmd, staging, refused);
        }

        _bands.Clear();
    }

    // Fills _bands with this frame's share of each waiting page, and gives their bytes in all.
    private long PlanBands(List<ChunkBakePool.Entry> uploading)
    {
        long total = 0;
        foreach (var entry in uploading)
        {
            foreach (var page in entry.Pages)
            {
                var texture = page.Texture;
                int from = page.RowsDone;
                if (from == texture.Height)
                {
                    continue;
                }

                int unit = texture.RowUnit;
                long units = (Budget - total) / texture.RowsSize(unit);
                if (units < 1 && total > 0)
                {
                    break;
                }

                int rows = (int)Math.Min(Math.Max(units, 1) * unit, texture.Height - from);
                int bytes = texture.RowsSize(from + rows) - texture.RowsSize(from);
                _bands.Add(new Band(entry, page, from, rows, bytes));
                total += bytes;
            }

            if (total >= Budget)
            {
                break;
            }
        }

        return total;
    }

    // Copies each planned band through staging, skipping those of refused bakes.
    private void CopyBands(CommandBuffer cmd, GpuBuffer staging, List<(ChunkBakePool.Entry Entry, string Reason)> refused)
    {
        ulong offset = 0;
        foreach (var band in _bands)
        {
            var page = band.Page;
            bool first = band.FirstRow == 0;
            bool last = band.FirstRow + band.Rows == page.Texture.Height;
            if (IsRefused(refused, band.Entry))
            {
                continue;
            }

            if (last && !resources.HasTextureRoom)
            {
                refused.Add((band.Entry, $"the material table has no room for its {RoleName(page.Role)} page, " +
                    $"since the renderer holds at most {ResourceTable.MaxTextures} textures."));
                continue;
            }

            int start = page.Texture.RowsSize(band.FirstRow);
            page.Source!.Pixels.AsSpan(start, band.Bytes).CopyTo(new Span<byte>((byte*)staging.Mapped + offset, band.Bytes));
            page.Texture.RecordRows(cmd, staging, offset, band.FirstRow, band.Rows, first, last);
            page.RowsDone = band.FirstRow + band.Rows;
            if (last)
            {
                page.Slot = resources.AddTexture(page.Texture, TextureEdges.Clamp).Index;
                page.Source = null;
            }

            offset += (ulong)band.Bytes;
        }
    }

    private static bool IsRefused(List<(ChunkBakePool.Entry Entry, string Reason)> refused, ChunkBakePool.Entry entry)
    {
        foreach (var (held, _) in refused)
        {
            if (ReferenceEquals(held, entry))
            {
                return true;
            }
        }

        return false;
    }

    private static string RoleName(int role) => role switch
    {
        0 => "sky red transfer",
        1 => "sky green transfer",
        2 => "sky blue transfer",
        3 => "sun transfer",
        4 => "sun tint transfer",
        ChunkBakePool.Irradiance => "irradiance",
        ChunkBakePool.Direction => "direction",
        _ => "mask",
    };
}
