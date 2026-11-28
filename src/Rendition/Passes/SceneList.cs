using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// What a scene pass needs to draw through the GPU layer into rendering it began by hand: the pipeline layout its
/// pipelines share, the list its draws record into with the materials bound once, and the pipelines it handed to the
/// layer. Disposing it destroys those pipelines and the layout.
/// </summary>
internal sealed class SceneList : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly DescriptorSetLayout[] _sets;
    private readonly SceneTarget _target;
    private readonly List<PipelineVariants> _adopted = [];

    /// <summary>
    /// Makes the layout from <paramref name="sets"/> and <paramref name="constants"/>. Set 1 is the materials'
    /// <paramref name="resources"/>, which every draw recorded through <see cref="Begin"/> binds.
    /// </summary>
    public SceneList(
        (VulkanContext Context, VulkanGpu Gpu) device, ResourceTable resources, DescriptorSetLayout[] sets,
        PushConstantRange constants)
    {
        _context = device.Context;
        _gpu = device.Gpu;
        _sets = sets;
        Layout = device.Context.CreatePipelineLayout(sets, constants);
        _target = new SceneTarget(new CommandList(), new GlobalMaterialBinding(device.Gpu, resources));
    }

    /// <summary>The layout every pipeline of the pass is made with, and its hand-bound sets are bound through.</summary>
    public PipelineLayout Layout { get; }

    /// <summary>Empties the list and returns it, with the materials, to record draws into.</summary>
    public SceneTarget Begin()
    {
        _target.Commands.Reset();
        return _target;
    }

    /// <summary>Plays what was recorded since <see cref="Begin"/> into the rendering <paramref name="cmd"/> is in.</summary>
    public void Play(CommandBuffer cmd) => _gpu.Play(_target.Commands, cmd);

    /// <summary>
    /// Hands <paramref name="culled"/> and <paramref name="twoSided"/>, which may be the same pipeline, to the layer
    /// with the layout lent, and destroys them when the list is disposed. Constants go to every stage that
    /// <see cref="SceneDraws.PushStages"/> names.
    /// </summary>
    public PipelineVariants Adopt(LivePipeline culled, LivePipeline twoSided)
    {
        var culledId = _gpu.Adopt(culled, Layout, _sets, SceneDraws.PushStages);
        var twoSidedId = ReferenceEquals(culled, twoSided) ? culledId : _gpu.Adopt(twoSided, Layout, _sets, SceneDraws.PushStages);
        var variants = new PipelineVariants(culledId, twoSidedId);
        _adopted.Add(variants);
        return variants;
    }

    public unsafe void Dispose()
    {
        foreach (var (culled, twoSided) in _adopted)
        {
            _gpu.Destroy(culled);
            if (twoSided != culled)
            {
                _gpu.Destroy(twoSided);
            }
        }

        _context.Api.DestroyPipelineLayout(_context.Device, Layout, null);
    }
}
