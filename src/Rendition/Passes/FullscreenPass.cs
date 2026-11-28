using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Runs a fragment shader over the whole target, drawn through the GPU layer. The lighting, the tonemap and the
/// post-processing effects are each one of these.
/// </summary>
internal sealed class FullscreenPass : IDisposable
{
    private readonly GpuDevice _device;
    private readonly int _targetCount;

    /// <summary>
    /// A pass writing one output per format in <paramref name="targetFormats"/>. With <paramref name="blend"/> it adds
    /// premultiplied colour over what the targets hold. Until every pass has moved, <paramref name="borrowed"/> lends
    /// the set layouts of groups an old pass fills, which needs a <see cref="VulkanGpu"/>; see
    /// <see cref="VulkanGpu.CreateRenderPipeline(in RenderPipelineDescription, ReadOnlySpan{DescriptorSetLayout})"/>.
    /// </summary>
    public FullscreenPass(
        GpuDevice device,
        string fragmentShader,
        ReadOnlySpan<GpuFormat> targetFormats,
        bool blend = false,
        ReadOnlySpan<DescriptorSetLayout> borrowed = default)
    {
        _device = device;
        _targetCount = targetFormats.Length;
        Span<ColorTargetState> targets = stackalloc ColorTargetState[targetFormats.Length];
        for (int i = 0; i < targets.Length; i++)
        {
            targets[i] = new ColorTargetState(targetFormats[i], blend ? BlendState.Premultiplied : null);
        }

        var description = new RenderPipelineDescription
        {
            VertexShader = device.CreateShader("fullscreen.vert"),
            FragmentShader = device.CreateShader(fragmentShader),
            Targets = targets,
        };
        if (borrowed.IsEmpty)
        {
            Pipeline = device.CreateRenderPipeline(description);
        }
        else
        {
            var vulkan = device as VulkanGpu ?? throw new ArgumentException("Only the Vulkan backend can borrow set layouts.", nameof(borrowed));
            Pipeline = vulkan.CreateRenderPipeline(description, borrowed);
        }
    }

    /// <summary>The pass's pipeline, which its bind groups are made for.</summary>
    public PipelineId Pipeline { get; }

    /// <summary>
    /// Records the pass into <paramref name="commands"/>: one target per format given at creation, in the same order,
    /// with each of <paramref name="groups"/> set at its index, where an id of 0 leaves that group unset.
    /// </summary>
    public void Record(CommandList commands, ReadOnlySpan<ColorTarget> targets, ReadOnlySpan<BindGroupId> groups, ReadOnlySpan<byte> constants)
    {
        if (targets.Length != _targetCount)
        {
            throw new ArgumentException($"The pass writes {_targetCount} targets, not {targets.Length}.", nameof(targets));
        }

        commands.BeginRenderPass(targets, null);
        commands.SetPipeline(Pipeline);
        commands.SetBindGroups(groups);
        if (!constants.IsEmpty)
        {
            commands.SetConstants(constants);
        }

        commands.Draw(3, 1, 0, 0);
        commands.EndPass();
    }

    public void Dispose() => _device.Destroy(Pipeline);
}
