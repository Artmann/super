using System.Numerics;
using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The reflection probes, each with its cube in <see cref="ProbeCubes"/> and one mip per roughness step. A probe added or
/// updated waits in line for its capture, and the renderer takes one a frame from the front with <see cref="TakeNext"/>,
/// captures it with <see cref="ReflectionCapturePass"/> and blurs the capture into the probe's cube with
/// <see cref="RecordFilter"/>, all inside the frame. A probe shows from the frame of its first capture, and until a new one
/// lands it keeps its last. A removed probe's cube goes to the next one added, and when none is free the array grows.
/// Each frame <see cref="Prepare"/> picks and orders the captured probes the view sees.
/// </summary>
internal sealed unsafe class ReflectionProbes : IDisposable
{
    private const int MaxProbes = ReflectionProbeOrder.MaxProbes;
    private const int Mips = ProbeCubes.Mips;

    /// <summary>Must match the Filter block in Shaders/reflection_probe.comp.</summary>
    private struct FilterConstants
    {
        public float Roughness;
    }

    private readonly VulkanContext _context;
    private readonly Sampler _sampler;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;

    // One set per mip for each frame in flight, each written as its frame records, when its last use is done.
    private readonly DescriptorSet[] _sets;
    private readonly PipelineLayout _pipelineLayout;
    private readonly LivePipeline _pipeline;

    // What each captured probe showed at its last capture, or null for a cube nobody has captured into yet.
    private readonly ReflectionProbe?[] _slots = new ReflectionProbe?[MaxProbes];

    // Which cubes belong to a probe, captured or still waiting.
    private readonly bool[] _held = new bool[MaxProbes];
    private readonly List<(int Slot, ReflectionProbe Probe)> _waiting = [];

    /// <summary>The array every probe's cube is in.</summary>
    public ProbeCubes Cubes { get; }

    /// <summary>The probes the last <see cref="Prepare"/> found in view, in the order they blend.</summary>
    public ReflectionProbeOrder Order { get; } = new();

    /// <summary>How many probes there are, captured or waiting for their first capture.</summary>
    public int Count { get; private set; }

    public ReflectionProbes(VulkanContext context, Sampler sampler, int framesInFlight)
    {
        _context = context;
        _sampler = sampler;
        Cubes = new ProbeCubes(context);

        _setLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
        ]);
        uint sets = (uint)(framesInFlight * Mips);
        _pool = context.CreateDescriptorPool(
            sets,
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, sets),
            new DescriptorPoolSize(DescriptorType.StorageImage, sets));
        _sets = new DescriptorSet[sets];
        context.AllocateSets(_pool, _setLayout, _sets);

        _pipelineLayout = context.CreatePipelineLayout(
            [_setLayout],
            new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FilterConstants)));
        _pipeline = PipelineFactory.CreateCompute(context, "reflection_probe.comp", _pipelineLayout);
    }

    /// <summary>
    /// Writes the captured probes the view sees to <paramref name="gpu"/> in the order they blend, and says how many.
    /// <see cref="Order"/> keeps the depths their boxes span.
    /// </summary>
    public int Prepare(in Matrix4x4 view, in Matrix4x4 projection, Span<GpuReflectionProbe> gpu)
    {
        Order.Prepare(_slots.AsSpan(0, Cubes.Capacity), view, projection, gpu);
        return Order.Count;
    }

    /// <summary>
    /// Takes a cube for a new probe, the first free one, and puts the probe at the back of the line for a capture. When
    /// none is free the array grows first, which waits for the GPU.
    /// </summary>
    public ReflectionProbeHandle Add(in ReflectionProbe probe)
    {
        if (Count == MaxProbes)
        {
            throw new InvalidOperationException($"Only {MaxProbes} reflection probes are supported.");
        }

        int slot = Array.IndexOf(_held, false, 0, Cubes.Capacity);
        if (slot < 0)
        {
            slot = Cubes.Capacity;
            Cubes.Grow();
        }

        _held[slot] = true;
        Count++;
        _waiting.Add((slot, probe));
        return new ReflectionProbeHandle(slot);
    }

    /// <summary>
    /// Asks for a new capture of a probe, placed as <paramref name="probe"/> says. A probe already waiting keeps its
    /// place in line and takes the new placement; any other goes to the back.
    /// </summary>
    public void Update(ReflectionProbeHandle handle, in ReflectionProbe probe)
    {
        CheckHeld(handle);
        int at = WaitingAt(handle.Index);
        if (at >= 0)
        {
            _waiting[at] = (handle.Index, probe);
        }
        else
        {
            _waiting.Add((handle.Index, probe));
        }
    }

    /// <summary>Whether a probe is in line for a capture that hasn't been drawn yet.</summary>
    public bool Waiting(ReflectionProbeHandle handle)
    {
        CheckHeld(handle);
        return WaitingAt(handle.Index) >= 0;
    }

    /// <summary>
    /// Takes the probe at the front of the line, for this frame's capture, or returns false when none waits. Call
    /// <see cref="RecordFilter"/> for it in the same frame.
    /// </summary>
    public bool TakeNext(out int slot, out ReflectionProbe probe)
    {
        if (_waiting.Count == 0)
        {
            slot = -1;
            probe = default;
            return false;
        }

        (slot, probe) = _waiting[0];
        _waiting.RemoveAt(0);
        return true;
    }

    /// <summary>
    /// Puts a probe <see cref="TakeNext"/> gave back at the front of the line, for a frame that never drew its capture.
    /// A probe since removed, or put in line again, is left as it is.
    /// </summary>
    public void PutBack(ReflectionProbeHandle handle, in ReflectionProbe probe)
    {
        if (Holds(handle) && WaitingAt(handle.Index) < 0)
        {
            _waiting.Insert(0, (handle.Index, probe));
        }
    }

    /// <summary>Forgets one probe, whose cube goes to the next probe added. The rest keep theirs.</summary>
    public void Remove(ReflectionProbeHandle handle)
    {
        CheckHeld(handle);
        int slot = handle.Index;
        _held[slot] = false;
        _slots[slot] = null;
        int at = WaitingAt(slot);
        if (at >= 0)
        {
            _waiting.RemoveAt(at);
        }

        Count--;
    }

    /// <summary>Forgets every probe. Surfaces reflect the sky again from the next frame.</summary>
    public void Clear()
    {
        Array.Clear(_slots);
        Array.Clear(_held);
        _waiting.Clear();
        Count = 0;
    }

    /// <summary>Whether a handle names a probe that was added and not removed since.</summary>
    public bool Holds(ReflectionProbeHandle handle) => (uint)handle.Index < (uint)Cubes.Capacity && _held[handle.Index];

    private void CheckHeld(ReflectionProbeHandle handle)
    {
        if (!Holds(handle))
        {
            throw new ArgumentException($"Reflection probe {handle.Index} does not exist.", nameof(handle));
        }
    }

    private int WaitingAt(int slot) => _waiting.FindIndex(waiting => waiting.Slot == slot);

    /// <summary>
    /// Records the blur of a capture, <paramref name="capture"/> with its mips ready to sample, into each level of
    /// <paramref name="slot"/>'s cube, and shows <paramref name="probe"/> there from this frame on. Surfaces read the cube
    /// in their fragment shaders, so the blur waits for the frames before to finish reading it, and the frame's own
    /// lighting waits for the blur.
    /// </summary>
    public void RecordFilter(CommandBuffer cmd, int frameIndex, int slot, in ReflectionProbe probe, ImageView capture)
    {
        var api = _context.Api;
        var image = Cubes.Image;
        uint baseLayer = (uint)slot * 6;
        api.Transition(cmd, image, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.General,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            mipCount: Mips, baseLayer: baseLayer, layerCount: 6);

        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
        for (int mip = 0; mip < Mips; mip++)
        {
            var set = _sets[frameIndex * Mips + mip];
            WriteFilterSet(set, capture, Cubes.Level(slot, mip));
            var constants = new FilterConstants { Roughness = mip / (float)(Mips - 1) };
            api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
            api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FilterConstants), &constants);
            uint groups = ((ProbeCubes.FaceSize >> mip) + 7) / 8;
            api.CmdDispatch(cmd, groups, groups, 6);
        }

        api.Transition(cmd, image, ImageAspectFlags.ColorBit,
            ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
            mipCount: Mips, baseLayer: baseLayer, layerCount: 6);
        _slots[slot] = probe;
    }

    private void WriteFilterSet(DescriptorSet set, ImageView capture, ImageView target)
    {
        var sourceInfo = new DescriptorImageInfo(_sampler, capture, ImageLayout.ShaderReadOnlyOptimal);
        var targetInfo = new DescriptorImageInfo(default, target, ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.ImageWrite(set, 0, DescriptorType.CombinedImageSampler, &sourceInfo),
            Descriptors.ImageWrite(set, 1, DescriptorType.StorageImage, &targetInfo),
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
    }

    public void Dispose()
    {
        _pipeline.Destroy();
        _context.DestroyLayouts(_pipelineLayout, _pool, _setLayout);
        Cubes.Dispose();
    }
}
