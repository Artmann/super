using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Anti-aliases and upscales with AMD FSR 3.1 in place of TAA and the finish pass's stretch. Two small passes get
/// FSR's inputs ready, motion with the sky's filled in and the exposure as one pixel, and then FSR rebuilds the
/// picture at the display's size from the jittered scene and its own history. See "Upscaling" in docs/design.md.
/// </summary>
internal sealed unsafe class FsrPass : IDisposable
{
    private const Format MotionFormat = Format.R16G16Sfloat;
    private const Format ExposureFormat = Format.R32Sfloat;

    private readonly VulkanContext _context;
    private readonly Sampler _pointSampler;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _motionSet;
    private readonly FullscreenPass _motionPass;
    private readonly FullscreenPass _exposurePass;

    private Fsr? _fsr;
    private RenderTarget? _motion;
    private RenderTarget? _exposure;

    public FsrPass(VulkanContext context, Sampler pointSampler, DescriptorSetLayout frameSetLayout, DescriptorSetLayout exposureSetLayout)
    {
        _context = context;
        _pointSampler = pointSampler;
        var api = context.Api;

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &layoutInfo, null, out _setLayout).Check();

        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 2);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool).Check();

        var setLayout = _setLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        api.AllocateDescriptorSets(context.Device, &allocInfo, out _motionSet).Check();

        _motionPass = new FullscreenPass(context, "fsr_motion.frag", MotionFormat, [frameSetLayout, _setLayout]);
        _exposurePass = new FullscreenPass(context, "fsr_exposure.frag", ExposureFormat, [frameSetLayout, exposureSetLayout]);
    }

    /// <summary>Whether FSR has a context, from <see cref="SetTargets"/> until <see cref="Release"/>.</summary>
    public bool HasTargets => _fsr is not null;

    /// <summary>The FSR version the library picked, once it has a context.</summary>
    public string? Version => _fsr?.Version;

    /// <summary>
    /// Makes a new FSR context, with no history, for a scene drawn at <paramref name="render"/> and shown at
    /// <paramref name="display"/>, reading the scene's <paramref name="depth"/> and <paramref name="motion"/>.
    /// Frames still in flight must have finished.
    /// </summary>
    public void SetTargets(Extent2D render, Extent2D display, ImageView depth, ImageView motion)
    {
        Release();
        _motion = new RenderTarget(_context, MotionFormat, render, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        _exposure = new RenderTarget(_context, ExposureFormat, new Extent2D(1, 1), ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        _fsr = new Fsr(_context, render, display, debugChecking: _context.Validating);

        // Glass has just used depth for testing, and it stays in that layout until FSR needs it.
        var images = stackalloc DescriptorImageInfo[]
        {
            new(_pointSampler, depth, ImageLayout.DepthReadOnlyOptimal),
            new(_pointSampler, motion, ImageLayout.ShaderReadOnlyOptimal),
        };
        var writes = stackalloc WriteDescriptorSet[2];
        for (int i = 0; i < 2; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _motionSet,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &images[i],
            };
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
    }

    /// <summary>Frees the context and its targets, while FSR isn't used. Frames still in flight must have finished.</summary>
    public void Release()
    {
        _fsr?.Dispose();
        _motion?.Dispose();
        _exposure?.Dispose();
        _fsr = null;
        _motion = null;
        _exposure = null;
    }

    /// <summary>
    /// Records FSR's frame: it reads <paramref name="color"/>, which must be ready to sample, and
    /// <paramref name="depth"/>, read-only after the scene, and writes <paramref name="output"/> at the display's
    /// size, which is left ready to sample. Depth goes back to read-only for the passes after.
    /// </summary>
    public void Record(
        CommandBuffer cmd, DescriptorSet frameSet, DescriptorSet exposureSet, Image color, Image depth, Image output,
        in FsrFrame frame)
    {
        if (_fsr is null || _motion is null || _exposure is null)
        {
            throw new InvalidOperationException("FSR has no targets yet. Call SetTargets first.");
        }

        var api = _context.Api;
        api.BeginColorTarget(cmd, _motion.Image);
        _motionPass.Record(cmd, _motion.View, _fsr.RenderSize, [frameSet, _motionSet]);
        api.EndColorTarget(cmd, _motion.Image);

        api.BeginColorTarget(cmd, _exposure.Image);
        _exposurePass.Record(cmd, _exposure.View, new Extent2D(1, 1), [frameSet, exposureSet]);
        api.EndColorTarget(cmd, _exposure.Image);

        // FSR samples depth from compute shaders, in the layout it was told.
        api.Transition(cmd, depth, ImageAspectFlags.DepthBit,
            ImageLayout.DepthReadOnlyOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit | PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit);

        // Last frame's passes after FSR sampled the output. FSR writes all of it, so what it held goes.
        api.Transition(cmd, output, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.General,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit);

        // FSR's own passes read what the fragment passes above wrote, and keep their history between frames.
        MemoryBarrier(cmd);
        _fsr.Dispatch(cmd, new FsrInputs(
            color, depth, _motion.Image, _exposure.Image, output,
            frame.Jitter, frame.Milliseconds, frame.Reset, frame.CameraNear, frame.VerticalFov));
        MemoryBarrier(cmd);

        api.Transition(cmd, output, ImageAspectFlags.ColorBit,
            ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit);
        api.Transition(cmd, depth, ImageAspectFlags.DepthBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.DepthReadOnlyOptimal,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);
    }

    public void Dispose()
    {
        Release();
        _exposurePass.Dispose();
        _motionPass.Dispose();
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _context.Api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }

    private void MemoryBarrier(CommandBuffer cmd)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }
}

/// <summary>What changes from one FSR frame to the next.</summary>
/// <param name="Jitter">The camera's shift this frame, in scene pixels with y down.</param>
/// <param name="Milliseconds">How long since the last frame.</param>
/// <param name="Reset">True when there's no last frame to build on, such as after a cut or a resize.</param>
/// <param name="CameraNear">The near plane's distance.</param>
/// <param name="VerticalFov">The view's height as an angle, in radians.</param>
internal readonly record struct FsrFrame(
    System.Numerics.Vector2 Jitter, float Milliseconds, bool Reset, float CameraNear, float VerticalFov);
