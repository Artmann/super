using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
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
    private readonly VulkanGpu _gpu;
    private readonly CommandList _commands = new();
    private readonly ImageGroup _motionInputs;
    private readonly AdoptedTarget _motionTarget;
    private readonly AdoptedTarget _exposureTarget;
    private readonly FullscreenPass _motionPass;
    private readonly FullscreenPass _exposurePass;

    private Fsr? _fsr;
    private RenderTarget? _motion;
    private RenderTarget? _exposure;

    /// <summary>
    /// The two passes that ready FSR's inputs draw through the GPU layer, lent the frame's set layout and the exposure
    /// meter's.
    /// </summary>
    public FsrPass((VulkanContext Context, VulkanGpu Gpu) device, DescriptorSetLayout frameSetLayout, DescriptorSetLayout exposureSetLayout)
    {
        var (context, gpu) = device;
        _context = context;
        _gpu = gpu;
        _motionTarget = new AdoptedTarget(gpu);
        _exposureTarget = new AdoptedTarget(gpu);
        _motionInputs = new ImageGroup(gpu, gpu.CreateSampler(new SamplerDescription(SamplerFilter.Nearest)));
        _motionPass = new FullscreenPass(gpu, "fsr_motion.frag", [VulkanFormats.ToGpu(MotionFormat)], borrowed: [frameSetLayout, default]);
        _exposurePass = new FullscreenPass(gpu, "fsr_exposure.frag", [VulkanFormats.ToGpu(ExposureFormat)],
            borrowed: [frameSetLayout, exposureSetLayout]);
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
        _fsr = new Fsr(_context, render, display, debugChecking: _context.Validation is not null);

        // Glass has just used depth for testing, and it stays in that layout until FSR needs it.
        _motionInputs.Make(_motionPass.Pipeline, 1,
            [new GroupImage("depthTexture", depth, ReadOnlyDepth: true), ("motionTexture", motion)]);
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
    /// Records FSR's frame, with the frame's and the exposure meter's <paramref name="sets"/>: it reads the colour image,
    /// which must be ready to sample, and depth, read-only after the scene, and writes the output at the display's
    /// size, which is left ready to sample. Depth goes back to read-only for the passes after.
    /// </summary>
    public void Record(
        CommandBuffer cmd, (DescriptorSet Frame, DescriptorSet Exposure) sets, (Image Color, Image Depth, Image Output) images,
        in FsrFrame frame)
    {
        if (_fsr is null || _motion is null || _exposure is null)
        {
            throw new InvalidOperationException("FSR has no targets yet. Call SetTargets first.");
        }

        var api = _context.Api;
        var (color, depth, output) = images;
        RecordInputs(cmd, sets, _fsr, (_motion, _exposure));

        // FSR samples depth from compute shaders, in the layout it was told.
        api.Transition(cmd, new ImageTransition(depth, ImageAspectFlags.DepthBit,
            ImageLayout.DepthReadOnlyOptimal, ImageLayout.ShaderReadOnlyOptimal),
            (PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit | PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.ShaderSampledReadBit),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit));

        // Last frame's passes after FSR sampled the output. FSR writes all of it, so what it held goes.
        api.Transition(cmd, new ImageTransition(output, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.General),
            (PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit));

        // FSR's own passes read what the fragment passes above wrote, and keep their history between frames.
        MemoryBarrier(cmd);
        _fsr.Dispatch(cmd, new FsrInputs(
            color, depth, _motion.Image, _exposure.Image, output,
            frame.Jitter, frame.Milliseconds, frame.Reset, frame.CameraNear, frame.VerticalFov));
        MemoryBarrier(cmd);

        api.Transition(cmd, new ImageTransition(output, ImageAspectFlags.ColorBit,
            ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit));
        api.Transition(cmd, new ImageTransition(depth, ImageAspectFlags.DepthBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.DepthReadOnlyOptimal),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));
    }

    // Draws the motion vectors FSR reads, and the exposure it reads, into their own targets.
    private void RecordInputs(
        CommandBuffer cmd, (DescriptorSet Frame, DescriptorSet Exposure) sets, Fsr fsr, (RenderTarget Motion, RenderTarget Exposure) inputs)
    {
        var frameSet = _gpu.Adopt(sets.Frame);
        _commands.Reset();
        _motionPass.Record(_commands, [new ColorTarget(_motionTarget.For(inputs.Motion, fsr.RenderSize), LoadOp.DontCare, default)],
            [frameSet, _motionInputs.Id], []);
        _exposurePass.Record(_commands, [new ColorTarget(_exposureTarget.For(inputs.Exposure, new Extent2D(1, 1)), LoadOp.DontCare, default)],
            [frameSet, _gpu.Adopt(sets.Exposure)], []);
        _gpu.Play(_commands, cmd);
    }

    public void Dispose()
    {
        Release();
        _motionInputs.Dispose();
        _motionTarget.Dispose();
        _exposureTarget.Dispose();
        _exposurePass.Dispose();
        _motionPass.Dispose();
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
