using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// The cube a reflection probe's capture is drawn into, <see cref="ProbeCubes.FaceSize"/> across with its depth, and
/// what finishes it: reflection_sky.comp fills what nothing covered with the sky, and blits halve it into a full chain
/// of mips for the blur to read. See <see cref="ReflectionCapturePass"/>.
/// </summary>
internal sealed unsafe class ReflectionCaptureCube : IDisposable
{
    /// <summary>The mips of the capture, from <see cref="ProbeCubes.FaceSize"/> across down to one texel.</summary>
    public static readonly uint Mips = (uint)BitOperations.Log2(ProbeCubes.FaceSize) + 1;

    private const PipelineStageFlags2 ComputeStage = PipelineStageFlags2.ComputeShaderBit;
    private const PipelineStageFlags2 CopyStage = PipelineStageFlags2.BlitBit;
    private const PipelineStageFlags2 ColorStage = PipelineStageFlags2.ColorAttachmentOutputBit;
    private const PipelineStageFlags2 DepthStages = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;

    // Must match the Fill block in reflection_sky.comp.
    [StructLayout(LayoutKind.Sequential)]
    private struct SkyConstants
    {
        public Vector4 SkySun;
        public float EnvironmentIntensity;
    }

    private readonly VulkanContext _context;
    private readonly RenderTarget _color;
    private readonly RenderTarget _depth;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _layout;
    private readonly LivePipeline _pipeline;

    /// <summary>
    /// Makes the cube and the sky fill, which reads <paramref name="sky"/>, the environment's sharp cube. That stays the
    /// same image for as long as the renderer lives.
    /// </summary>
    public ReflectionCaptureCube(VulkanContext context, DescriptorImageInfo sky)
    {
        _context = context;
        var size = new Extent2D(ProbeCubes.FaceSize, ProbeCubes.FaceSize);
        _color = new RenderTarget(context, GBuffer.HdrFormat, size,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            ImageAspectFlags.ColorBit, layers: 6, mips: Mips, flags: ImageCreateFlags.CreateCubeCompatibleBit);
        _depth = new RenderTarget(context, GBuffer.DepthFormat, size, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit, layers: 6);
        Faces = CreateView(ImageViewType.Type2DArray, 1);
        CubeView = CreateView(ImageViewType.TypeCube, Mips);

        _setLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
        ]);
        _pool = context.CreateDescriptorPool(
            1, new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 1), new DescriptorPoolSize(DescriptorType.StorageImage, 1));
        _set = context.AllocateSet(_pool, _setLayout);
        var faces = new DescriptorImageInfo(default, Faces, ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.ImageWrite(_set, 0, DescriptorType.CombinedImageSampler, &sky),
            Descriptors.ImageWrite(_set, 1, DescriptorType.StorageImage, &faces),
        };
        context.Api.UpdateDescriptorSets(context.Device, 2, writes, 0, null);

        _layout = context.CreatePipelineLayout([_setLayout], new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(SkyConstants)));
        _pipeline = PipelineFactory.CreateCompute(context, "reflection_sky.comp", _layout);
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
        api.Transition(cmd, _color.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, ComputeStage, AccessFlags2.ShaderSampledReadBit,
            ColorStage, AccessFlags2.ColorAttachmentWriteBit, layerCount: 6);
        api.Transition(cmd, _depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal, DepthStages, AccessFlags2.DepthStencilAttachmentWriteBit,
            DepthStages, AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit, layerCount: 6);
    }

    /// <summary>
    /// After the faces are drawn, fills the texels nothing covered with the sky, <paramref name="skySun"/> being the
    /// frame's (xyz towards the clear sky's sun, w its turbidity), then makes the mips and leaves them all ready for
    /// compute shaders to sample.
    /// </summary>
    public void RecordFinish(CommandBuffer cmd, Vector4 skySun, float environmentIntensity)
    {
        var api = _context.Api;
        api.Transition(cmd, _color.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.General, ColorStage, AccessFlags2.ColorAttachmentWriteBit,
            ComputeStage, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit, layerCount: 6);

        var set = _set;
        var constants = new SkyConstants { SkySun = skySun, EnvironmentIntensity = environmentIntensity };
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
        api.CmdPushConstants(cmd, _layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(SkyConstants), &constants);
        uint groups = (ProbeCubes.FaceSize + 7) / 8;
        api.CmdDispatch(cmd, groups, groups, 6);

        api.Transition(cmd, _color.Image, ImageAspectFlags.ColorBit,
            ImageLayout.General, ImageLayout.TransferSrcOptimal, ComputeStage, AccessFlags2.ShaderStorageWriteBit,
            CopyStage, AccessFlags2.TransferReadBit, layerCount: 6);
        RecordMips(cmd);
    }

    // Halves each level into the next, then leaves them all ready for the blur to sample.
    private void RecordMips(CommandBuffer cmd)
    {
        var api = _context.Api;
        int size = (int)ProbeCubes.FaceSize;
        for (uint mip = 1; mip < Mips; mip++)
        {
            api.Transition(cmd, _color.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.TransferDstOptimal, ComputeStage, AccessFlags2.ShaderSampledReadBit,
                CopyStage, AccessFlags2.TransferWriteBit, baseMip: mip, layerCount: 6);
            int next = Math.Max(size / 2, 1);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip - 1, 0, 6),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip, 0, 6),
            };
            blit.SrcOffsets[1] = new Offset3D(size, size, 1);
            blit.DstOffsets[1] = new Offset3D(next, next, 1);
            api.CmdBlitImage(cmd, _color.Image, ImageLayout.TransferSrcOptimal, _color.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);
            api.Transition(cmd, _color.Image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, CopyStage, AccessFlags2.TransferWriteBit,
                CopyStage, AccessFlags2.TransferReadBit, baseMip: mip, layerCount: 6);
            size = next;
        }

        api.Transition(cmd, _color.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, CopyStage, AccessFlags2.TransferReadBit,
            ComputeStage, AccessFlags2.ShaderSampledReadBit, mipCount: Mips, layerCount: 6);
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
        _pipeline.Destroy();
        _context.DestroyLayouts(_layout, _pool, _setLayout);
        _context.Api.DestroyImageView(_context.Device, Faces, null);
        _context.Api.DestroyImageView(_context.Device, CubeView, null);
        _color.Dispose();
        _depth.Dispose();
    }
}
