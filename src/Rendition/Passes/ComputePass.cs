using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Runs one compute shader through the GPU layer. Each <see cref="Record"/> is a compute pass of its own with one
/// dispatch, and every dispatch sees what the ones before it wrote.
/// </summary>
internal sealed class ComputePass : IDisposable
{
    private readonly GpuDevice _device;

    /// <summary>
    /// A pass running <paramref name="shader"/>, such as <c>exposure.comp</c>. Until every pass has moved,
    /// <paramref name="borrowed"/> lends the set layouts of groups an old pass fills, as it does for
    /// <see cref="FullscreenPass"/>.
    /// </summary>
    public ComputePass(GpuDevice device, string shader, ReadOnlySpan<DescriptorSetLayout> borrowed = default)
    {
        _device = device;
        var description = new ComputePipelineDescription { Shader = device.CreateShader(shader) };
        if (borrowed.IsEmpty)
        {
            Pipeline = device.CreateComputePipeline(description);
        }
        else
        {
            var vulkan = device as VulkanGpu ?? throw new ArgumentException("Only the Vulkan backend can borrow set layouts.", nameof(borrowed));
            Pipeline = vulkan.CreateComputePipeline(description, borrowed);
        }
    }

    /// <summary>The pass's pipeline, which its bind groups are made for.</summary>
    public PipelineId Pipeline { get; }

    /// <summary>
    /// Records one dispatch of <paramref name="workgroups"/>, with
    /// each of <paramref name="groups"/> set at its index, where an id of 0 leaves that group unset.
    /// </summary>
    public void Record(CommandList commands, ReadOnlySpan<BindGroupId> groups, ReadOnlySpan<byte> constants, (int X, int Y, int Z) workgroups)
    {
        commands.BeginComputePass();
        commands.SetPipeline(Pipeline);
        commands.SetBindGroups(groups);
        if (!constants.IsEmpty)
        {
            commands.SetConstants(constants);
        }

        commands.Dispatch(workgroups.X, workgroups.Y, workgroups.Z);
        commands.EndPass();
    }

    public void Dispose() => _device.Destroy(Pipeline);
}
