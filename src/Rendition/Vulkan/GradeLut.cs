using System.Numerics;
using System.Runtime.InteropServices;
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
    private const uint GroupSize = 4;

    /// <summary>The artists' tables <see cref="Renderer.CreateColorLut"/> made, and the neutral one.</summary>
    public readonly ColorLuts Luts;

    /// <summary>The table for the tonemap, as one sampled 3D texture at binding 0 of a one-texture set.</summary>
    public readonly DescriptorSet ReadSet;

    private readonly VulkanContext _context;
    private readonly Sampler _sampler;
    private readonly CellVolume _table;
    private readonly DescriptorSetLayout _bakeLayout;
    private readonly DescriptorPool _pool;
    private readonly PipelineLayout _pipelineLayout;
    private readonly LivePipeline _bake;
    private readonly DescriptorSet[] _bakeSets = new DescriptorSet[FrameRing.Count];

    // Nothing is baked to begin with, and its pipeline of 0 matches no wanted bake.
    private BakeKey _baked;
    private BakeKey _wanted;
    private int _slot;

    public GradeLut(VulkanContext context, DescriptorKit kit)
    {
        _context = context;
        _sampler = kit.LinearSampler;
        Luts = new ColorLuts(context);
        _table = new CellVolume(context, (Size, Size, Size), new ClearColorValue(0f, 0f, 0f, 1f));

        _bakeLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
        ]);
        _pool = context.CreateDescriptorPool(
            FrameRing.Count + 1,
            new DescriptorPoolSize(DescriptorType.StorageImage, FrameRing.Count),
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, FrameRing.Count + 1));
        context.AllocateSets(_pool, _bakeLayout, _bakeSets);
        ReadSet = context.AllocateSet(_pool, kit.OneTextureLayout);
        _pipelineLayout = context.CreatePipelineLayout([kit.FrameSetLayout, _bakeLayout]);
        _bake = PipelineFactory.CreateCompute(context, "grade_lut.comp", _pipelineLayout);

        var tableInfo = new DescriptorImageInfo(default, _table.View, ImageLayout.General);
        var readInfo = new DescriptorImageInfo(_sampler, _table.View, ImageLayout.General);
        Span<WriteDescriptorSet> writes = stackalloc WriteDescriptorSet[FrameRing.Count + 1];
        for (int slot = 0; slot < FrameRing.Count; slot++)
        {
            writes[slot] = Descriptors.ImageWrite(_bakeSets[slot], 0, DescriptorType.StorageImage, &tableInfo);
        }

        writes[FrameRing.Count] = Descriptors.ImageWrite(ReadSet, 0, DescriptorType.CombinedImageSampler, &readInfo);
        fixed (WriteDescriptorSet* first = writes)
        {
            context.Api.UpdateDescriptorSets(context.Device, (uint)writes.Length, first, 0, null);
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
            Pipeline = (ulong)_bake.Handle.Handle,
        };
        _slot = slot;
    }

    /// <summary>
    /// Bakes the table again when what <see cref="Take"/> took differs from the last bake, before the tonemap reads it.
    /// It waits on the GPU for nothing: the last frame's reads come first by the barrier, on the same queue.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet)
    {
        _wanted.Pipeline = (ulong)_bake.Handle.Handle;
        if (BakeKey.Same(_wanted, _baked))
        {
            return;
        }

        // The slot's last frame is done by now, so its set is free to change.
        var info = new DescriptorImageInfo(_sampler, new ImageView(_wanted.Lut), ImageLayout.General);
        var write = Descriptors.ImageWrite(_bakeSets[_slot], 1, DescriptorType.CombinedImageSampler, &info);
        var api = _context.Api;
        api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);

        api.MemoryBarrier(cmd, (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit));

        ReadOnlySpan<DescriptorSet> sets = [frameSet, _bakeSets[_slot]];
        fixed (DescriptorSet* first = sets)
        {
            api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, (uint)sets.Length, first, 0, null);
        }

        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _bake);
        const uint groups = (Size + GroupSize - 1) / GroupSize;
        api.CmdDispatch(cmd, groups, groups, groups);

        api.MemoryBarrier(cmd, (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));
        _baked = _wanted;
        Bakes++;
    }

    public void Dispose()
    {
        _bake.Destroy();
        var api = _context.Api;
        api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _bakeLayout, null);
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
