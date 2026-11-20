using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// How much of each flaring light is seen, worked out on the GPU by lens_flare_visibility.comp from the depth in a
/// small disc round the light, and kept in a buffer lens_flare.vert reads, so nothing comes back to the CPU. Under
/// anti-aliasing the buffer keeps the last four frames' visibility and the flare takes the most of them, so the jitter
/// of a light seen through leaves doesn't make it flicker. A cut starts the four frames afresh.
/// </summary>
internal sealed unsafe class LensFlareVisibility : IDisposable
{
    /// <summary>The floats each light has in the buffer: four frames, the visibility drawn with, and pads.</summary>
    private const int SourceFloats = 8;

    /// <summary>The frames whose visibility is kept. Must match the ring in lens_flare_visibility.comp.</summary>
    private const int HeldFrames = 4;

    /// <summary>How far a light may move on screen in a frame, as a fraction of it, before its kept visibility is dropped, as on a cut.</summary>
    private const float JumpDistance = 0.1f;

    private const int HoldFlag = 1;
    private const int ResetFlag = 2;

    private const ulong BufferSize = LensFlareSettings.MaxSources * SourceFloats * sizeof(float);

    /// <summary>Must match Constants in lens_flare_visibility.comp.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 Source0;
        public Vector4 Source1;
        public Vector4 Source2;
        public Vector4 Source3;
        public float Aspect;
        public int Count;
        public int Slot;
        public int Flags;
    }

    private readonly VulkanContext _context;
    private readonly GpuBuffer _buffer;
    private readonly DescriptorPool _pool;
    private readonly PipelineLayout _layout;
    private readonly LivePipeline _pipeline;
    private readonly Sampler _depthSampler;
    private readonly Vector2[] _lastUvs = new Vector2[LensFlareSettings.MaxSources];

    // How many lights the last recorded frame looked at, 0 when it drew no flare.
    private int _lastCount;
    private uint _frame;
    private int _flags;

    public LensFlareVisibility(VulkanContext context, Sampler depthSampler)
    {
        _context = context;
        _depthSampler = depthSampler;
        var api = context.Api;
        _buffer = new GpuBuffer(
            context, BufferSize, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);

        (SetLayout, _pool, Set) = context.CreateStateSet(ShaderStageFlags.VertexBit, new DescriptorBufferInfo(_buffer.Handle, 0, BufferSize));
        _layout = context.CreatePipelineLayout([SetLayout], new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(Constants)));
        _pipeline = PipelineFactory.CreateCompute(context, "lens_flare_visibility.comp", _layout);

        // Nothing seen until the first frame looks, which starts the frames afresh anyway.
        var buffer = _buffer.Handle;
        context.SubmitAndWait(cmd =>
        {
            api.CmdFillBuffer(cmd, buffer, 0, BufferSize, 0);
            api.MemoryBarrier(cmd, (PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit),
                (PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.VertexShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit));
        });
    }

    /// <summary>The buffer, which the compute and vertex shaders read, and the scene's depth, which the compute shader reads.</summary>
    public DescriptorSetLayout SetLayout { get; }

    /// <summary>The set of <see cref="SetLayout"/>.</summary>
    public DescriptorSet Set { get; }

    /// <summary>Points the taps at the scene's depth. Only call it while no frame in flight uses the set.</summary>
    public void SetDepth(ImageView depth)
    {
        var image = new DescriptorImageInfo(_depthSampler, depth, ImageLayout.DepthReadOnlyOptimal);
        var write = Descriptors.ImageWrite(Set, 1, DescriptorType.CombinedImageSampler, &image);
        _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    /// <summary>
    /// Takes this frame's lights. <paramref name="hold"/> keeps the most of the last four frames, for anti-aliasing.
    /// The frames start afresh without a last picture, after a frame with no flare, when the number of lights changes
    /// or when one jumps across the screen.
    /// </summary>
    public void Take(ReadOnlySpan<FlareSource> sources, bool hold, bool historyReady)
    {
        bool reset = !historyReady || sources.Length != _lastCount;
        for (int i = 0; i < sources.Length && !reset; i++)
        {
            reset = Vector2.Distance(sources[i].Uv, _lastUvs[i]) > JumpDistance;
        }

        _flags = (hold ? HoldFlag : 0) | (reset ? ResetFlag : 0);
    }

    /// <summary>Records the taps for the lights <see cref="Take"/> was given, after the scene's depth is drawn.</summary>
    public void Record(CommandBuffer cmd, ReadOnlySpan<FlareSource> sources, float aspect)
    {
        var api = _context.Api;

        // Depth was drawn and then sampled, by fragment or, under FSR, compute shaders, and last frame's flare read the buffer.
        api.MemoryBarrier(cmd,
            (PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit | PipelineStageFlags2.FragmentShaderBit
                | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.VertexShaderBit,
                AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit));

        var constants = new Constants
        {
            Source0 = Pack(sources, 0),
            Source1 = Pack(sources, 1),
            Source2 = Pack(sources, 2),
            Source3 = Pack(sources, 3),
            Aspect = aspect,
            Count = sources.Length,
            Slot = (int)(_frame++ % HeldFrames),
            Flags = _flags,
        };
        var set = Set;
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
        api.CmdPushConstants(cmd, _layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(Constants), &constants);
        api.CmdDispatch(cmd, (uint)sources.Length, 1, 1);

        api.MemoryBarrier(cmd,
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.VertexShaderBit, AccessFlags2.ShaderStorageReadBit));

        _lastCount = sources.Length;
        for (int i = 0; i < sources.Length; i++)
        {
            _lastUvs[i] = sources[i].Uv;
        }
    }

    /// <summary>Notes a frame that drew no flare, so the next one starts its frames afresh.</summary>
    public void Skip() => _lastCount = 0;

    public void Dispose()
    {
        var api = _context.Api;
        _pipeline.Destroy();
        api.DestroyPipelineLayout(_context.Device, _layout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, SetLayout, null);
        _buffer.Dispose();
    }

    private static Vector4 Pack(ReadOnlySpan<FlareSource> sources, int index)
    {
        if (index >= sources.Length)
        {
            return Vector4.Zero;
        }

        var source = sources[index];
        return new Vector4(source.Uv, source.Depth, source.Radius);
    }
}
