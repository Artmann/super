using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The grade baked into a table: grade_lut.comp works out, for <see cref="Size"/> cubed levels of light, what the grade,
/// the curve, the tones and the artist's table make of each, and the tonemap reads it once a pixel. It bakes only when
/// what goes into it changes, compared on the CPU, so a still grade costs one look-up a pixel and nothing else. The
/// artists' tables it reads are in <see cref="Luts"/>. See "The grade's table" in docs/design.md.
/// </summary>
internal sealed unsafe class GradeLut : IDisposable
{
    /// <summary>The table's cells a side. Must match GRADE_LUT_SIZE in grade_lut.glsl.</summary>
    public const int Size = 64;

    // Must match local_size in grade_lut.comp.
    private const int GroupSize = 4;

    /// <summary>The artists' tables <see cref="RendererLuts.Create"/> made, and the neutral one.</summary>
    public readonly ColorLuts Luts;

    /// <summary>The table for the tonemap, as one sampled 3D texture at binding 0 of a one-texture set.</summary>
    public readonly DescriptorSet ReadSet;

    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly CellVolume _table;
    private readonly DescriptorPool _pool;
    private readonly ComputePass _bake;
    private readonly CommandList _commands = new();

    // One group a frame slot, as the artist's table the bake reads can change between bakes.
    private readonly ImageGroup[] _bakeGroups = new ImageGroup[FrameRing.Count];

    // Nothing is baked to begin with, and its pipeline of 0 matches no wanted bake.
    private BakeKey _baked;
    private BakeKey _wanted;
    private int _slot;

    /// <summary>
    /// The table and its bake. The tonemap, which hasn't moved to the GPU layer, reads it through <see cref="ReadSet"/>,
    /// made outside the layer; the bake borrows the frame's set layout.
    /// </summary>
    public GradeLut((VulkanContext Context, VulkanGpu Gpu) device, DescriptorKit kit)
    {
        var (context, gpu) = device;
        _context = context;
        _gpu = gpu;
        Luts = new ColorLuts(context);
        _table = new CellVolume(context, (Size, Size, Size), new ClearColorValue(0f, 0f, 0f, 1f));

        _pool = context.CreateDescriptorPool(1, new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 1));
        ReadSet = context.AllocateSet(_pool, kit.OneTextureLayout);
        var readInfo = new DescriptorImageInfo(kit.LinearSampler, _table.View, ImageLayout.General);
        var write = Descriptors.ImageWrite(ReadSet, 0, DescriptorType.CombinedImageSampler, &readInfo);
        context.Api.UpdateDescriptorSets(context.Device, 1, &write, 0, null);

        _bake = new ComputePass(gpu, "grade_lut.comp", [kit.FrameSetLayout, default]);
        var linear = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear));
        for (int slot = 0; slot < FrameRing.Count; slot++)
        {
            _bakeGroups[slot] = new ImageGroup(gpu, linear);
        }
    }

    /// <summary>How many times the table has been baked, for tests that check it bakes only on a change.</summary>
    public int Bakes { get; private set; }

    /// <summary>
    /// Takes the grade frame slot <paramref name="slot"/> draws with, as <see cref="PostGrade.Without"/> left it. Throws,
    /// with nothing recorded yet, when its table wasn't made by <see cref="ColorLuts.Create"/>.
    /// </summary>
    public void Take(in PostSettings post, int slot)
    {
        ImageView lut = Luts.View(post.Grade.Lut);
        var (lift, gain) = PostGrade.Tint(post);
        _wanted = new BakeKey
        {
            Tone = PostGrade.Tone(post),
            // Only contrast and saturation: exposure, the vignette and the bloom are worked out a pixel at a time.
            Grade = new Vector4(0f, 1f + post.Contrast, 1f + post.Saturation, 0f),
            Lift = lift with { W = 0f },
            Gain = gain,
            Lut = lut.Handle,
            Pipeline = (ulong)_gpu.Current(_bake.Pipeline).Handle,
        };
        _slot = slot;
    }

    /// <summary>
    /// Destroys an artist's table through <see cref="ColorLuts.Destroy"/> and forgets the last bake, so a table made
    /// later whose view has the same handle still bakes.
    /// </summary>
    public void DestroyLut(ColorLut lut, RetiredSlot retired)
    {
        Luts.Destroy(lut, retired);
        _baked = default;
    }

    /// <summary>
    /// Bakes the table again when what <see cref="Take"/> took differs from the last bake, before the tonemap reads it.
    /// It waits on the GPU for nothing: the bake is a compute pass, which comes after the last frame's reads.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet)
    {
        _wanted.Pipeline = (ulong)_gpu.Current(_bake.Pipeline).Handle;
        if (BakeKey.Same(_wanted, _baked))
        {
            return;
        }

        // The slot's last frame is done by now, so its group is free to be made again.
        var group = _bakeGroups[_slot];
        group.Make(_bake.Pipeline, 1,
        [
            new GroupImage("grade", _table.View, Use: GroupImageUse.Written),
            new GroupImage("artistLut", new ImageView(_wanted.Lut), Use: GroupImageUse.Stored),
        ]);

        const int groups = (Size + GroupSize - 1) / GroupSize;
        _commands.Reset();
        _bake.Record(_commands, [_gpu.Adopt(frameSet), group.Id], [], (groups, groups, groups));
        _gpu.Play(_commands, cmd);
        _baked = _wanted;
        Bakes++;
    }

    public void Dispose()
    {
        foreach (var group in _bakeGroups)
        {
            group.Dispose();
        }

        _bake.Dispose();
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _table.Dispose();
        Luts.Dispose();
    }

    /// <summary>Everything the bake reads, so the same bytes mean the same table.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BakeKey
    {
        public FrameToneBlock Tone;
        public Vector4 Grade;
        public Vector4 Lift;
        public Vector4 Gain;
        public ulong Lut;
        // A shader reloaded while the game runs makes a new pipeline, which must bake again.
        public ulong Pipeline;

        public static bool Same(in BakeKey a, in BakeKey b) =>
            MemoryMarshal.AsBytes(new ReadOnlySpan<BakeKey>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<BakeKey>(in b)));
    }
}
