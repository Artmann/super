using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Paint's filter: paint_filter.comp runs an anisotropic Kuwahara filter over the picture into the half-size painted
/// picture, through the GPU layer.
/// </summary>
internal sealed unsafe class PaintFilter : IDisposable
{
    private const int GroupSize = 16;

    private readonly ComputePass _pass;
    private readonly ImageGroup _group;
    private readonly SamplerId _nearest;

    /// <summary>
    /// A filter reading the picture through <paramref name="samplers"/>' linear one and depth through its nearest one.
    /// The frame's and the meter's sets are made outside the layer, so their layouts are lent.
    /// </summary>
    public PaintFilter(VulkanGpu gpu, (SamplerId Nearest, SamplerId Linear) samplers, DescriptorSetLayout frameSetLayout, DescriptorSetLayout exposureLayout)
    {
        _pass = new ComputePass(gpu, "paint_filter.comp", [frameSetLayout, default, exposureLayout]);
        _group = new ImageGroup(gpu, samplers.Linear);
        _nearest = samplers.Nearest;
    }

    /// <summary>Points the filter at <paramref name="source"/> and <paramref name="depth"/>, writing into <paramref name="painted"/>.</summary>
    public void Write(ImageView source, ImageView depth, ImageView painted) =>
        _group.Make(_pass.Pipeline, 1,
        [
            ("sourceTexture", source),
            new GroupImage("depthTexture", depth, _nearest, GroupImageUse.Depth),
            new GroupImage("painted", painted, Use: GroupImageUse.Written),
        ]);

    /// <summary>Filters the picture into the painted picture, which is <paramref name="half"/> in size and rests in General.</summary>
    public void Record(CommandList commands, (BindGroupId Frame, BindGroupId Exposure) tables, Extent2D half, in PaintConstants constants)
    {
        var lens = constants;
        var workgroups = (((int)half.Width + GroupSize - 1) / GroupSize, ((int)half.Height + GroupSize - 1) / GroupSize, 1);
        _pass.Record(commands, [tables.Frame, _group.Id, tables.Exposure], new ReadOnlySpan<byte>(&lens, (int)PaintConstants.Size), workgroups);
    }

    public void Dispose()
    {
        _group.Dispose();
        _pass.Dispose();
    }
}
