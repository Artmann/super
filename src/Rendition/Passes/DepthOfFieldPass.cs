using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// What the depth of field shaders are told for a frame. Must match Lens in Shaders/dof_coc.frag,
/// dof_blur.frag and dof_combine.frag.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DepthOfFieldConstants
{
    /// <summary>The largest circle drawn, as a radius in the scene's pixels. A wider one is drawn this wide.</summary>
    public const float MaxRadius = 16f;

    /// <summary>Where the lens is focused, in metres in front of the camera.</summary>
    public float FocusDistance;

    /// <summary>
    /// How many of the scene's pixels across the circle is for a point at the horizon. A point at depth d
    /// gets this times (d − focus) / d, which is negative in front of the focus.
    /// </summary>
    public float PixelsAtInfinity;

    /// <summary>The largest radius, in the scene's pixels.</summary>
    public float MaxRadiusPixels;

    public float Unused;

    public static unsafe uint Size => (uint)sizeof(DepthOfFieldConstants);

    /// <summary>
    /// The constants for a frame whose scene is <paramref name="sceneWidth"/> pixels wide, or null when no
    /// depth of field is drawn: the switch is off, the aperture is 0, or the lens settings are ones a
    /// view would refuse, which Rendition used alone takes as no depth of field rather than throwing.
    /// </summary>
    public static DepthOfFieldConstants? For(in PostSettings post, RenderEffects off, int sceneWidth)
    {
        if (off.HasFlag(RenderEffects.DepthOfField) || post.Aperture == 0f || sceneWidth <= 0)
        {
            return null;
        }

        float focalLength = ThinLens.FocalLength(post.FocalLength);
        try
        {
            ThinLens.Check(post.FocusDistance, post.Aperture, focalLength, nameof(post));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        float circle = ThinLens.CircleOfConfusion(float.PositiveInfinity, post.FocusDistance, post.Aperture, focalLength);
        return new DepthOfFieldConstants
        {
            FocusDistance = post.FocusDistance,
            PixelsAtInfinity = ThinLens.InPixels(circle, sceneWidth),
            MaxRadiusPixels = MaxRadius,
        };
    }
}

/// <summary>
/// Blurs what's in front of and behind the focus, as a camera lens does. At half size it works out each
/// pixel's circle from its depth, gathers a near layer and a far layer over a disc of 48 taps, and then
/// blends both over the full-size picture: the far layer where the pixel itself is out of focus, the
/// near layer wherever a blurred near thing spreads, even over sharp things behind it.
/// </summary>
internal sealed unsafe class DepthOfFieldPass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly Sampler _pointSampler;
    private readonly Sampler _linearSampler;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _cocSet;
    private readonly DescriptorSet _blurSet;
    private readonly DescriptorSet _combineSet;
    private readonly FullscreenPass _cocPass;
    private readonly FullscreenPass _blurPass;
    private readonly FullscreenPass _combinePass;
    private RenderTarget _coc = null!;
    private RenderTarget _near = null!;
    private RenderTarget _far = null!;
    private Extent2D _half;

    public DepthOfFieldPass(VulkanContext context, Sampler pointSampler, Sampler linearSampler, DescriptorSetLayout frameSetLayout)
    {
        _context = context;
        _pointSampler = pointSampler;
        _linearSampler = linearSampler;
        var api = context.Api;

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(2, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = bindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &layoutInfo, null, out _setLayout).Check();

        const int setCount = 3;
        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, setCount * 3);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = setCount,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool).Check();

        _cocSet = Allocate();
        _blurSet = Allocate();
        _combineSet = Allocate();

        ReadOnlySpan<DescriptorSetLayout> layouts = [frameSetLayout, _setLayout];
        _cocPass = new FullscreenPass(context, "dof_coc.frag", [GBuffer.HdrFormat], layouts, DepthOfFieldConstants.Size);
        _blurPass = new FullscreenPass(context, "dof_blur.frag", [GBuffer.HdrFormat, GBuffer.HdrFormat], layouts, DepthOfFieldConstants.Size);
        _combinePass = new FullscreenPass(context, "dof_combine.frag", [GBuffer.HdrFormat], layouts, DepthOfFieldConstants.Size, blend: true);
    }

    /// <summary>
    /// Makes the half-size layers for scene targets <paramref name="extent"/> in size, and points the
    /// passes at <paramref name="source"/>, the picture blurred, and <paramref name="depth"/>, which glass
    /// leaves read-only.
    /// </summary>
    public void SetTargets(ImageView source, ImageView depth, Extent2D extent)
    {
        DestroyTargets();
        _half = new Extent2D(Math.Max((extent.Width + 1) / 2, 1), Math.Max((extent.Height + 1) / 2, 1));
        _coc = CreateTarget();
        _near = CreateTarget();
        _far = CreateTarget();
        SetSource(source, depth);
    }

    /// <summary>Reads a different picture from the next frame on. Frames still in flight must have finished.</summary>
    public void SetSource(ImageView source, ImageView depth)
    {
        var depthInfo = new DescriptorImageInfo(_pointSampler, depth, ImageLayout.DepthReadOnlyOptimal);
        Write(_cocSet,
        [
            new(_linearSampler, source, ImageLayout.ShaderReadOnlyOptimal),
            depthInfo,
            new(_linearSampler, source, ImageLayout.ShaderReadOnlyOptimal),
        ]);
        Write(_blurSet,
        [
            new(_pointSampler, _coc.View, ImageLayout.ShaderReadOnlyOptimal),
            depthInfo,
            new(_linearSampler, _coc.View, ImageLayout.ShaderReadOnlyOptimal),
        ]);
        Write(_combineSet,
        [
            new(_linearSampler, _near.View, ImageLayout.ShaderReadOnlyOptimal),
            depthInfo,
            new(_linearSampler, _far.View, ImageLayout.ShaderReadOnlyOptimal),
        ]);
    }

    /// <summary>
    /// Blurs <paramref name="target"/>, the picture <see cref="SetSource"/> was given, in place. It must be
    /// ready to sample, and is left ready to sample again.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet, Image target, ImageView targetView, Extent2D extent, in DepthOfFieldConstants constants)
    {
        var api = _context.Api;
        var lens = constants;
        var bytes = new ReadOnlySpan<byte>(&lens, (int)DepthOfFieldConstants.Size);

        api.BeginColorTarget(cmd, _coc.Image);
        _cocPass.Record(cmd, [_coc.View], _half, [frameSet, _cocSet], bytes);
        api.EndColorTarget(cmd, _coc.Image);

        api.BeginColorTarget(cmd, _near.Image);
        api.BeginColorTarget(cmd, _far.Image);
        _blurPass.Record(cmd, [_near.View, _far.View], _half, [frameSet, _blurSet], bytes);
        api.EndColorTarget(cmd, _near.Image);
        api.EndColorTarget(cmd, _far.Image);

        // The combine blends over the picture, so it keeps what's there. The meter, a compute pass, reads it after.
        api.Transition(cmd, target, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit);
        _combinePass.Record(cmd, [targetView], extent, [frameSet, _combineSet], bytes);
        api.Transition(cmd, target, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit);
    }

    public void Dispose()
    {
        DestroyTargets();
        _combinePass.Dispose();
        _blurPass.Dispose();
        _cocPass.Dispose();
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _context.Api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }

    private DescriptorSet Allocate()
    {
        var setLayout = _setLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        _context.Api.AllocateDescriptorSets(_context.Device, &allocInfo, out var set).Check();
        return set;
    }

    private RenderTarget CreateTarget() =>
        new(_context, GBuffer.HdrFormat, _half, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);

    private void DestroyTargets()
    {
        _coc?.Dispose();
        _near?.Dispose();
        _far?.Dispose();
    }

    private void Write(DescriptorSet set, ReadOnlySpan<DescriptorImageInfo> images)
    {
        var writes = stackalloc WriteDescriptorSet[images.Length];
        fixed (DescriptorImageInfo* imagesPtr = images)
        {
            for (int i = 0; i < images.Length; i++)
            {
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = (uint)i,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = &imagesPtr[i],
                };
            }

            _context.Api.UpdateDescriptorSets(_context.Device, (uint)images.Length, writes, 0, null);
        }
    }
}
