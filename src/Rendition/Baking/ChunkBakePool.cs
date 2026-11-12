using System.Collections.Concurrent;
using System.Numerics;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// The chunk bakes on the GPU, one per file however many copies of the chunk a frame names. A fresh chunk's bake is
/// read on a worker thread, checked there, and uploaded on the render thread at most <see cref="ChunkUploadBudget"/>
/// bytes a frame, so a frame never waits on a file. A bake no copy names in a frame is let go at the end of it. See
/// "Chunk bakes on the GPU" in docs/design.md.
/// </summary>
internal sealed unsafe class ChunkBakePool : IDisposable
{
    /// <summary>The most bytes of chunk lightmap pages copied to the GPU in one frame. A page larger than this goes up over several.</summary>
    public const long ChunkUploadBudget = 8L * 1024 * 1024;

    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly long _uploadBudget;
    private readonly Action<string, string> _unreadable;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(Entry Entry, LightBakeFile? Bake, string? Failure)> _loaded = new();
    private readonly List<Entry> _uploading = [];
    private readonly List<Band> _bands = [];
    private readonly List<string> _unused = [];
    private int _frame;

    /// <param name="unreadable">Told, on the render thread, of each file that couldn't be read or checked, and why.</param>
    public ChunkBakePool(VulkanContext context, ResourceTable resources, long uploadBudget, Action<string, string> unreadable)
    {
        _context = context;
        _resources = resources;
        _uploadBudget = uploadBudget;
        _unreadable = unreadable;
    }

    /// <summary>The bakes with every page on the GPU.</summary>
    public int Resident { get; private set; }

    /// <summary>The bakes being read or uploaded.</summary>
    public int Pending => _entries.Count - Resident;

    /// <summary>Every bake held, resident or pending. For tests.</summary>
    public IReadOnlyCollection<Entry> Entries => _entries.Values;

    /// <summary>One chunk file's bake: its pages, and once they are all up, each draw's lightmap rectangle.</summary>
    public sealed class Entry(string file)
    {
        public readonly string File = file;
        internal int LastUsed;
        internal LightBakeFile? Bake;
        internal readonly List<Page> Pages = [];

        public bool IsResident { get; internal set; }

        /// <summary>Each draw's rectangle on the chunk's lightmap page, by bake id. Set once resident.</summary>
        public Dictionary<ulong, GpuLightmapRect>? Rects { get; internal set; }

        /// <summary>The material table slots of the sky red, green, blue, sun and sun tint transfer pages, in that order, once resident.</summary>
        public int[] Transfer { get; } = [-1, -1, -1, -1, -1];

        /// <summary>The material table slot of the chunk's shadow mask page once resident, or -1 when it has none.</summary>
        public int MaskPage { get; internal set; } = -1;

        /// <summary>The bake's mask channel for each of its Mixed lights, by chunk-space light hash, once resident.</summary>
        public (ulong LightHash, int Channel)[] Channels { get; internal set; } = [];
    }

    internal sealed class Page(LightBakePage source, GpuTexture texture, int role)
    {
        public LightBakePage? Source = source;
        public readonly GpuTexture Texture = texture;
        public readonly int Role = role;
        public int RowsDone;
        public int Slot = -1;

        // Set when the material table freed the texture while refusing it.
        public bool Freed;
    }

    private readonly record struct Band(Page Page, int FirstRow, int Rows, int Bytes);

    // The roles of a chunk's pages: its five transfer pages, then its plain irradiance, direction and mask.
    private const int Irradiance = 5;
    private const int Direction = 6;
    private const int Mask = 7;

    /// <summary>
    /// Marks <paramref name="file"/>'s bake as named this frame, and gives it when every page is on the GPU. The first
    /// time, starts reading it from <paramref name="store"/> on a worker thread and gives null. Allocates nothing for a
    /// file it already holds.
    /// </summary>
    public Entry? Use(string file, IBakeStore store)
    {
        if (!_entries.TryGetValue(file, out var entry))
        {
            entry = new Entry(file);
            _entries.Add(file, entry);
            var loaded = _loaded;
            Task.Run(() =>
            {
                try
                {
                    var bake = store.Load(file)
                        ?? throw new InvalidDataException("The file is gone, though its header was read as fresh.");
                    CheckPages(bake, file);
                    loaded.Enqueue((entry, bake, null));
                }
                catch (Exception e)
                {
                    loaded.Enqueue((entry, null, e.Message));
                }
            });
        }

        entry.LastUsed = _frame;
        return entry.IsResident ? entry : null;
    }

    /// <summary>
    /// Records this frame's share of the uploads on <paramref name="cmd"/> and ends the frame: bakes read since the last
    /// frame start uploading, those that failed are reported, and those no copy named this frame are let go. What the
    /// GPU may still read goes in <paramref name="textures"/> and <paramref name="others"/>, which the caller frees
    /// once this frame's fence has signalled.
    /// </summary>
    public void Record(CommandBuffer cmd, List<int> textures, List<IDisposable> others)
    {
        while (_loaded.TryDequeue(out var done))
        {
            var entry = done.Entry;
            if (!_entries.TryGetValue(entry.File, out var held) || !ReferenceEquals(held, entry))
            {
                // Let go, or cleared by a reload, while it was being read.
                continue;
            }

            if (done.Bake is not { } bake)
            {
                _entries.Remove(entry.File);
                _unreadable(entry.File, done.Failure!);
                continue;
            }

            Start(entry, bake);
        }

        foreach (var (file, entry) in _entries)
        {
            if (entry.LastUsed != _frame)
            {
                _unused.Add(file);
            }
        }

        foreach (string file in _unused)
        {
            Release(_entries[file], textures, others);
            _entries.Remove(file);
        }

        _unused.Clear();
        Upload(cmd, others);
        _frame++;
    }

    /// <summary>Lets every bake go, as after the content reloads, from a frame that has already been submitted.</summary>
    public void Clear(List<int> textures, List<IDisposable> others)
    {
        foreach (var entry in _entries.Values)
        {
            Release(entry, textures, others);
        }

        _entries.Clear();
    }

    /// <summary>Frees the pages that never reached the material table, which frees the rest itself. Call once the GPU is idle.</summary>
    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            foreach (var page in entry.Pages)
            {
                if (page.Slot < 0 && !page.Freed)
                {
                    page.Texture.Dispose();
                }
            }
        }

        _entries.Clear();
        _uploading.Clear();
    }

    /// <summary>
    /// Throws when a chunk's bake can't be drawn as its pages say: it must have its sky and sun transfer, each transfer
    /// page in the format the shader reads it as and all the size of the sky red page, at most one plain page of each
    /// kind that size, pages whose bytes fit their size, and every draw on its one page.
    /// </summary>
    /// <exception cref="InvalidDataException">The bake is damaged or wrongly made.</exception>
    internal static void CheckPages(LightBakeFile bake, string file)
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

        foreach (var (page, name) in plainPages.Concat(transferPages.Select(p => (p.Page, p.Name + " transfer"))))
        {
            int needed = TextureData.LevelSize(Renderer.PageFormat(page.Format), page.Width, page.Height);
            if (page.Pixels.Length != needed)
            {
                throw new InvalidDataException(
                    $"The chunk bake {file}'s {page.Width}x{page.Height} {page.Format} {name} page holds {page.Pixels.Length} bytes, but it needs {needed}.");
            }
        }

        foreach (var (bakeId, rect) in bake.Draws)
        {
            if (rect.Page != 0)
            {
                throw new InvalidDataException(
                    $"The chunk bake {file} puts draw 0x{bakeId:X16} on page {rect.Page}, but a chunk's bake has one page.");
            }
        }
    }

    // Makes the images for a bake just read, for Upload to fill.
    private void Start(Entry entry, LightBakeFile bake)
    {
        var transfer = bake.Transfer!;
        try
        {
            AddPage(entry, transfer.SkyRed, 0);
            AddPage(entry, transfer.SkyGreen, 1);
            AddPage(entry, transfer.SkyBlue, 2);
            AddPage(entry, transfer.Sun, 3);
            AddPage(entry, transfer.SunTint, 4);
            if (bake.Irradiance is { Count: 1 } irradiance)
            {
                AddPage(entry, irradiance[0], Irradiance);
                AddPage(entry, bake.Direction![0], Direction);
            }

            if (bake.Mask.Count == 1)
            {
                AddPage(entry, bake.Mask[0], Mask);
            }
        }
        catch
        {
            // Such as running out of device memory part way: nothing of it is on the GPU yet.
            foreach (var page in entry.Pages)
            {
                page.Texture.Dispose();
            }

            entry.Pages.Clear();
            _entries.Remove(entry.File);
            throw;
        }

        entry.Bake = bake;
        _uploading.Add(entry);
    }

    private void AddPage(Entry entry, LightBakePage source, int role)
    {
        var texture = new GpuTexture(_context, source.Width, source.Height, TextureKind.Linear, 1, 1,
            format: Renderer.PageFormat(source.Format));
        entry.Pages.Add(new Page(source, texture, role));
    }

    // Copies a band of each waiting page, oldest bake first, up to the budget, and at least one row so even a page
    // wider than the budget moves. A page whose last band goes up joins the material table.
    private void Upload(CommandBuffer cmd, List<IDisposable> others)
    {
        if (_uploading.Count == 0)
        {
            return;
        }

        long total = 0;
        foreach (var entry in _uploading)
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
                long units = (_uploadBudget - total) / texture.RowsSize(unit);
                if (units < 1 && total > 0)
                {
                    break;
                }

                int rows = (int)Math.Min(Math.Max(units, 1) * unit, texture.Height - from);
                int bytes = texture.RowsSize(from + rows) - texture.RowsSize(from);
                _bands.Add(new Band(page, from, rows, bytes));
                total += bytes;
            }

            if (total >= _uploadBudget)
            {
                break;
            }
        }

        var staging = GpuBuffer.CreateHostVisible(_context, (ulong)total, BufferUsageFlags.TransferSrcBit);
        others.Add(staging);
        ulong offset = 0;
        try
        {
            foreach (var band in _bands)
            {
                var page = band.Page;
                int start = page.Texture.RowsSize(band.FirstRow);
                page.Source!.Pixels.AsSpan(start, band.Bytes).CopyTo(new Span<byte>((byte*)staging.Mapped + offset, band.Bytes));
                bool first = band.FirstRow == 0;
                bool last = band.FirstRow + band.Rows == page.Texture.Height;
                page.Texture.RecordRows(cmd, staging, offset, band.FirstRow, band.Rows, first, last);
                page.RowsDone = band.FirstRow + band.Rows;
                if (last)
                {
                    try
                    {
                        page.Slot = _resources.AddTexture(page.Texture, TextureEdges.Clamp).Index;
                    }
                    catch
                    {
                        // A full table frees the texture itself, so neither Release nor Dispose may free it again.
                        page.Freed = true;
                        throw;
                    }

                    page.Source = null;
                }

                offset += (ulong)band.Bytes;
            }
        }
        finally
        {
            _bands.Clear();
        }
        for (int i = _uploading.Count - 1; i >= 0; i--)
        {
            var entry = _uploading[i];
            if (entry.Pages.TrueForAll(page => page.Slot >= 0))
            {
                Finish(entry);
                _uploading.RemoveAt(i);
            }
        }
    }

    // Every page is up: the rectangles point at the plain pages, or at none when the chunk has no plain light.
    private void Finish(Entry entry)
    {
        int irradiance = -1;
        int direction = -1;
        foreach (var page in entry.Pages)
        {
            if (page.Role < Irradiance)
            {
                entry.Transfer[page.Role] = page.Slot;
            }
            else if (page.Role == Irradiance)
            {
                irradiance = page.Slot;
            }
            else if (page.Role == Direction)
            {
                direction = page.Slot;
            }
            else if (page.Role == Mask)
            {
                entry.MaskPage = page.Slot;
            }
        }

        var rects = new Dictionary<ulong, GpuLightmapRect>();
        foreach (var (bakeId, rect) in entry.Bake!.Draws)
        {
            rects[bakeId] = new GpuLightmapRect
            {
                ScaleOffset = new Vector4(rect.Scale, rect.Offset.X, rect.Offset.Y),
                Irradiance = irradiance,
                Direction = direction,
                // The chunk's mask is read through its GpuChunk instead, whose table turns the frame's slots into the
                // bake's channels. A view with no chunk data, such as a reflection capture, reads it as fully lit.
                Mask = -1,
                Page = 0,
            };
        }

        entry.Rects = rects;
        entry.Channels = [.. entry.Bake.ChunkChannels];
        entry.Bake = null;
        entry.IsResident = true;
        Resident++;
    }

    // Lets a bake's pages go: those in the material table once the frames drawing them are done, the rest with the
    // copies that may still be filling them.
    private void Release(Entry entry, List<int> textures, List<IDisposable> others)
    {
        foreach (var page in entry.Pages)
        {
            if (page.Slot >= 0)
            {
                _resources.MarkTextureDying(page.Slot);
                textures.Add(page.Slot);
            }
            else if (!page.Freed)
            {
                others.Add(page.Texture);
            }
        }

        entry.Pages.Clear();
        _uploading.Remove(entry);
        if (entry.IsResident)
        {
            entry.IsResident = false;
            Resident--;
        }
    }
}
