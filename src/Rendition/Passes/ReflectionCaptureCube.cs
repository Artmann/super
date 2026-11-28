using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// The cube a reflection probe's capture is drawn into, <see cref="ProbeCubes.FaceSize"/> across with its depth, and
/// what finishes it: reflection_sky.comp fills what nothing covered with the sky, through the GPU layer, and draws halve
/// it into a full chain of mips for the blur to read, through the layer too. See <see cref="ReflectionCapturePass"/>.
/// </summary>
internal sealed unsafe class ReflectionCaptureCube : IDisposable
{
    /// <summary>The mips of the capture, from <see cref="ProbeCubes.FaceSize"/> across down to one texel.</summary>
    public static readonly uint Mips = (uint)BitOperations.Log2(ProbeCubes.FaceSize) + 1;

    private const PipelineStageFlags2 ComputeStage = PipelineStageFlags2.ComputeShaderBit;
    private const PipelineStageFlags2 ColorStage = PipelineStageFlags2.ColorAttachmentOutputBit;
    private const PipelineStageFlags2 DepthStages = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;

    // Must match the Fill block in reflection_sky.comp.
    [StructLayout(LayoutKind.Sequential)]
    private struct SkyConstants
    {
        public float EnvironmentIntensity;
    }

    private readonly VulkanContext _context;
    private readonly RenderTarget _color;
    private readonly RenderTarget _depth;
    private readonly VulkanGpu _gpu;
    private readonly ComputePass _fill;
    private readonly ImageGroup _group;
    private readonly CommandList _commands = new();
    private readonly MipDraws _mips;

    /// <summary>
    /// Makes the cube and the sky fill, which reads <paramref name="sky"/>, the environment's sharp cube. That stays the
    /// same image for as long as the renderer lives.
    /// </summary>
    public ReflectionCaptureCube((VulkanContext Context, VulkanGpu Gpu) device, ImageView sky)
    {
        var (context, gpu) = device;
        _context = context;
        _gpu = gpu;
        var size = new Extent2D(ProbeCubes.FaceSize, ProbeCubes.FaceSize);
        _color = new RenderTarget(context, GBuffer.HdrFormat, size,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.StorageBit,
            ImageAspectFlags.ColorBit, layers: 6, mips: Mips, flags: ImageCreateFlags.CreateCubeCompatibleBit);
        _depth = new RenderTarget(context, GBuffer.DepthFormat, size, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit, layers: 6);
        Faces = CreateView(ImageViewType.Type2DArray, 1);
        CubeView = CreateView(ImageViewType.TypeCube, Mips);

        // The fill reads only the sky's sharpest level.
        _fill = new ComputePass(gpu, "reflection_sky.comp");
        _group = new ImageGroup(gpu, gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear)));
        _group.Make(_fill.Pipeline, 0, [("sky", sky), new GroupImage("capture", Faces, Use: GroupImageUse.Written)]);
        _mips = new MipDraws(gpu, context, new MipImage(_color.Image, GpuFormat.Rgba16Float, size, Mips, 6));
    }

    /// <summary>The sharpest level's six faces as layers, which the capture draws into.</summary>
    public ImageView Faces { get; }

    /// <summary>The depth the capture draws against, six layers.</summary>
    public ImageView Depth => _depth.View;

    /// <summary>The capture with all its mips, for the blur to read.</summary>
    public ImageView CubeView { get; }

    /// <summary>
    /// Readies the faces and depth to be drawn. The last blur may still be reading the capture, and nothing in it is kept.
    /// </summary>
    public void RecordStart(CommandBuffer cmd)
    {
        var api = _context.Api;
        api.Transition(cmd, new ImageTransition(_color.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal) { LayerCount = 6 }, (ComputeStage, AccessFlags2.ShaderSampledReadBit),
            (ColorStage, AccessFlags2.ColorAttachmentWriteBit));
        api.Transition(cmd, new ImageTransition(_depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal) { LayerCount = 6 }, (DepthStages, AccessFlags2.DepthStencilAttachmentWriteBit),
            (DepthStages, AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit));
    }

    /// <summary>
    /// After the faces are drawn, fills the texels nothing covered with the sky, at the view's environment intensity,
    /// then makes the mips and leaves them all ready for compute shaders to sample.
    /// </summary>
    public void RecordFinish(CommandBuffer cmd, float environmentIntensity)
    {
        var api = _context.Api;
        api.Transition(cmd, new ImageTransition(_color.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.General) { LayerCount = 6 }, (ColorStage, AccessFlags2.ColorAttachmentWriteBit),
            (ComputeStage, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit));

        var constants = new SkyConstants { EnvironmentIntensity = environmentIntensity };
        int groups = (int)(ProbeCubes.FaceSize + 7) / 8;
        _commands.Reset();
        _fill.Record(_commands, [_group.Id], MemoryMarshal.AsBytes(new ReadOnlySpan<SkyConstants>(in constants)), (groups, groups, 6));
        _gpu.Play(_commands, cmd);

        // The sharpest level goes back to resting, as the layer expects, so the draws below halve it.
        api.Transition(cmd, new ImageTransition(_color.Image, ImageAspectFlags.ColorBit,
            ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal) { LayerCount = 6 }, (ComputeStage, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));
        _mips.Play(cmd);
    }

    private ImageView CreateView(ImageViewType type, uint mips)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _color.Image,
            ViewType = type,
            Format = GBuffer.HdrFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, mips, 0, 6),
        };
        _context.Api.CreateImageView(_context.Device, &info, null, out var view).Check();
        return view;
    }

    public void Dispose()
    {
        _mips.Dispose();
        _group.Dispose();
        _fill.Dispose();
        _context.Api.DestroyImageView(_context.Device, Faces, null);
        _context.Api.DestroyImageView(_context.Device, CubeView, null);
        _color.Dispose();
        _depth.Dispose();
    }
}
