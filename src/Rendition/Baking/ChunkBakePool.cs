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
    private readonly Action<string, string, string> _unreadable;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    // The new bakes of forgotten resident files, read and uploaded while the old one is still drawn, by file.
    private readonly Dictionary<string, Entry> _replacing = new(StringComparer.Ordinal);
    private readonly List<string> _swapped = [];
    private readonly ConcurrentQueue<(Entry Entry, LightBakeFile? Bake, string? Failure)> _loaded = new();
    private readonly List<Entry> _uploading = [];
    private readonly ChunkPageUploader _uploader;
    private readonly List<string> _unused = [];
    private readonly List<(Entry Entry, string Reason)> _refused = [];

    // Counts calls to Record, which the renderer makes once a presented frame. A bake is let go when Record runs
    // without a Use since the one before, so this holds only while every frame draws one view: two views drawn in one
    // frame, each with its own Record, would let go of the first view's chunks on the second and read them again on
    // the next frame. Super draws one view a frame, so it never does. See "Chunk bakes on the GPU" in docs/design.md.
    private int _frame;

    /// <param name="unreadable">Told, on the render thread, of each file that couldn't be read, checked or put on the GPU: the file, what went wrong ("can't be read" or "can't go on the GPU") and why.</param>
    public ChunkBakePool(VulkanContext context, ResourceTable resources, long uploadBudget, Action<string, string, string> unreadable)
    {
        _context = context;
        _resources = resources;
        _uploader = new ChunkPageUploader(context, resources, uploadBudget);
        _unreadable = unreadable;
    }

    /// <summary>
    /// The most bytes of pages copied in one frame, <see cref="ChunkUploadBudget"/> unless a test sets a smaller one to
    /// watch a bake go up over several frames.
    /// </summary>
    internal long UploadBudget
    {
        get => _uploader.Budget;
        set => _uploader.Budget = value;
    }

    /// <summary>The bakes with every page on the GPU.</summary>
    public int Resident { get; private set; }

    /// <summary>The bakes being read or uploaded, replacements of resident ones included.</summary>
    public int Pending => _entries.Count + _replacing.Count - Resident;

    /// <summary>Every bake held, resident or pending. For tests.</summary>
    public IReadOnlyCollection<Entry> Entries => _entries.Values;

    /// <summary>One chunk file's bake: its pages, and once they are all up, each draw's lightmap rectangle.</summary>
    public sealed class Entry(string file)
    {
        public readonly string File = file;
        internal int LastUsed;
        internal LightBakeFile? Bake;

        // Forgotten while resident: still drawn, and read again the next time a copy names it.
        internal bool Stale;
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
    }

    // The roles of a chunk's pages: its five transfer pages, then its plain irradiance, direction and mask.
    internal const int Irradiance = 5;
    internal const int Direction = 6;
    internal const int Mask = 7;

    /// <summary>
    /// Marks <paramref name="file"/>'s bake as named this frame, and gives it when every page is on the GPU. The first
    /// time, starts reading it from <paramref name="store"/> on a worker thread and gives null. Allocates nothing for a
    /// file it already holds. A resident bake that was forgotten is still given, while its replacement is read and
    /// uploaded, until the replacement is resident.
    /// </summary>
    public Entry? Use(string file, IBakeStore store)
    {
        if (!_entries.TryGetValue(file, out var entry))
        {
            entry = new Entry(file);
            _entries.Add(file, entry);
            Read(entry, store);
        }
        else if (entry.Stale)
        {
            entry.Stale = false;
            var replacement = new Entry(file);
            _replacing.Add(file, replacement);
            Read(replacement, store);
        }

        entry.LastUsed = _frame;
        return entry.IsResident ? entry : null;
    }

    private void Read(Entry entry, IBakeStore store)
    {
        var loaded = _loaded;
        string file = entry.File;
        Task.Run(() =>
        {
            try
            {
                var bake = store.Load(file)
                    ?? throw new InvalidDataException("The file is gone, though its header was read as fresh.");
                ChunkBakeCheck.CheckPages(bake, file);
                loaded.Enqueue((entry, bake, null));
            }
            catch (Exception e)
            {
                loaded.Enqueue((entry, null, e.Message));
            }
        });
    }

    private bool IsReplacement(Entry entry) =>
        _replacing.TryGetValue(entry.File, out var replacement) && ReferenceEquals(replacement, entry);

    // Lets go of a bake that couldn't be read or put on the GPU, and of the one it was to replace, so the chunk is lit
    // live as any unreadable chunk is.
    private void Drop(Entry entry, List<int> textures, List<IDisposable> others)
    {
        Release(entry, textures, others);
        if (IsReplacement(entry))
        {
            _replacing.Remove(entry.File);
        }

        if (_entries.Remove(entry.File, out var held) && !ReferenceEquals(held, entry))
        {
            Release(held, textures, others);
        }
    }

    /// <summary>
    /// Records this frame's share of the uploads on <paramref name="cmd"/> and ends the frame: bakes read since the last
    /// frame start uploading, those that failed are reported, and those no copy named this frame are let go. What the
    /// GPU may still read goes in <paramref name="textures"/> and <paramref name="others"/>, which the caller frees
    /// once this frame's fence has signalled.
    /// </summary>
    public void Record(CommandBuffer cmd, List<int> textures, List<IDisposable> others)
    {
        StartLoaded(textures, others);
        ReleaseUnused(textures, others);
        Upload(cmd, others);
        DropRefused(textures, others);
        SwapReplacements(textures, others);
        _frame++;
    }

    // Starts uploading each bake read since the last frame, and drops and reports each that failed.
    private void StartLoaded(List<int> textures, List<IDisposable> others)
    {
        while (_loaded.TryDequeue(out var done))
        {
            var entry = done.Entry;
            bool replacement = IsReplacement(entry);
            if (!replacement && (!_entries.TryGetValue(entry.File, out var held) || !ReferenceEquals(held, entry)))
            {
                // Let go, forgotten again, or cleared by a reload, while it was being read.
                continue;
            }

            if (done.Bake is not { } bake)
            {
                Drop(entry, textures, others);
                _unreadable(entry.File, "can't be read", done.Failure!);
                continue;
            }

            Start(entry, bake);
        }
    }

    // Lets go of the bakes no copy named this frame, and of their replacements.
    private void ReleaseUnused(List<int> textures, List<IDisposable> others)
    {
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
            if (_replacing.Remove(file, out var replacement))
            {
                Release(replacement, textures, others);
            }
        }

        _unused.Clear();
    }

    // A bake whose page the material table had no room for is dropped, the copies already recorded included, and lit
    // live until the store reloads, as an unreadable one is.
    private void DropRefused(List<int> textures, List<IDisposable> others)
    {
        foreach (var (entry, reason) in _refused)
        {
            Drop(entry, textures, others);
            _unreadable(entry.File, "can't go on the GPU", reason);
        }

        _refused.Clear();
    }

    // A replacement with every page up takes the old bake's place, which goes once the frames drawing it are done, so
    // the chunk never draws live light between the two.
    private void SwapReplacements(List<int> textures, List<IDisposable> others)
    {
        foreach (var (file, replacement) in _replacing)
        {
            if (replacement.IsResident)
            {
                _swapped.Add(file);
            }
        }

        foreach (string file in _swapped)
        {
            _replacing.Remove(file, out var replacement);
            if (_entries.TryGetValue(file, out var old))
            {
                replacement!.LastUsed = old.LastUsed;
                Release(old, textures, others);
            }

            _entries[file] = replacement!;
        }

        _swapped.Clear();
    }

    /// <summary>Lets every bake go, as after the content reloads, from a frame that has already been submitted.</summary>
    public void Clear(List<int> textures, List<IDisposable> others)
    {
        foreach (var entry in _entries.Values)
        {
            Release(entry, textures, others);
        }

        foreach (var entry in _replacing.Values)
        {
            Release(entry, textures, others);
        }

        _entries.Clear();
        _replacing.Clear();
    }

    /// <summary>
    /// Has one file's bake read again, as when its file was written over, the next time a copy names it. Nothing else
    /// is touched. A resident bake is still drawn until the new one is resident, then swapped for it, so the chunk never
    /// flickers to live light; a replacement already being read for it is dropped. A bake not yet resident is let go
    /// the same deferred way as eviction, and its read's result ignored. Reads no file and waits for nothing.
    /// </summary>
    public void Forget(string file, List<int> textures, List<IDisposable> others)
    {
        if (_replacing.Remove(file, out var replacement))
        {
            Release(replacement, textures, others);
        }

        if (!_entries.TryGetValue(file, out var entry))
        {
            return;
        }

        if (entry.IsResident)
        {
            entry.Stale = true;
            return;
        }

        _entries.Remove(file);
        Release(entry, textures, others);
    }

    /// <summary>Frees the pages that never reached the material table, which frees the rest itself. Call once the GPU is idle.</summary>
    public void Dispose()
    {
        foreach (var entry in _entries.Values.Concat(_replacing.Values))
        {
            foreach (var page in entry.Pages)
            {
                if (page.Slot < 0)
                {
                    page.Texture.Dispose();
                }
            }
        }

        _entries.Clear();
        _replacing.Clear();
        _uploading.Clear();
    }

    // Makes the images for a bake just read, for Upload to fill.
    private void Start(Entry entry, LightBakeFile bake)
    {
        try
        {
            AddPages(entry, bake);
        }
        catch
        {
            Abandon(entry);
            throw;
        }

        entry.Bake = bake;
        _uploading.Add(entry);
    }

    private void AddPages(Entry entry, LightBakeFile bake)
    {
        var transfer = bake.Transfer!;
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

    // Lets go of a bake whose images couldn't all be made, such as by running out of device memory part way: nothing
    // of it is on the GPU yet.
    private void Abandon(Entry entry)
    {
        foreach (var page in entry.Pages)
        {
            page.Texture.Dispose();
        }

        entry.Pages.Clear();
        if (IsReplacement(entry))
        {
            _replacing.Remove(entry.File);
        }
        else
        {
            _entries.Remove(entry.File);
        }
    }

    private void AddPage(Entry entry, LightBakePage source, int role)
    {
        var texture = new GpuTexture(_context, source.Width, source.Height, TextureKind.Linear, 1, 1,
            format: Renderer.PageFormat(source.Format));
        entry.Pages.Add(new Page(source, texture, role));
    }

    // Records this frame's share of the waiting pages, then finishes each bake whose pages are all up.
    private void Upload(CommandBuffer cmd, List<IDisposable> others)
    {
        if (_uploading.Count == 0)
        {
            return;
        }

        _uploader.Record(cmd, _uploading, _refused, others);
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
        var (irradiance, direction) = TakeSlots(entry);
        entry.Rects = Rects(entry.Bake!, irradiance, direction);
        entry.Channels = [.. entry.Bake!.ChunkChannels];
        entry.Bake = null;
        entry.IsResident = true;
        Resident++;
    }

    // Sets the entry's transfer and mask slots, and gives the slots of its plain irradiance and direction pages, or -1.
    private static (int Irradiance, int Direction) TakeSlots(Entry entry)
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

        return (irradiance, direction);
    }

    private static Dictionary<ulong, GpuLightmapRect> Rects(LightBakeFile bake, int irradiance, int direction)
    {
        var rects = new Dictionary<ulong, GpuLightmapRect>();
        foreach (var (bakeId, rect) in bake.Draws)
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

        return rects;
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
            else
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
