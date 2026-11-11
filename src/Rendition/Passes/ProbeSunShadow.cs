using System.Numerics;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// The sun's shadow as light probe updates see it: one parallel map centred on the camera, wide
/// enough for the largest cascade. The frame's own cascades are fitted to what the camera sees, so
/// probes behind it or far off would fall outside them. This map is drawn again only when the sun
/// turns, when the camera has moved an eighth of its width, or every <see cref="RedrawFrames"/>
/// frames so moved things show up, which costs one shadow draw in that frame.
/// See "Light probes" in docs/design.md.
/// </summary>
internal sealed class ProbeSunShadow : IDisposable
{
    /// <summary>Texels across the map. Probes keep only soft light, so a coarse map is plenty.</summary>
    public const int Resolution = 2048;

    /// <summary>The most frames between two drawings of the map.</summary>
    public const int RedrawFrames = 240;

    private readonly RenderTarget _target;
    private Vector3 _direction;
    private Vector3 _center;
    private float _radius;
    private long _drawnFrame;
    private bool _drawn;
    private bool _ready;

    public ProbeSunShadow(VulkanContext context)
    {
        _target = new RenderTarget(
            context, GBuffer.DepthFormat, new Extent2D(Resolution, Resolution), ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);
    }

    /// <summary>The map, which the capture samples with a compare sampler.</summary>
    public ImageView View => _target.View;

    /// <summary>World to the map, or zero when no sun casts a shadow, which the capture reads as lit everywhere.</summary>
    public Matrix4x4 Matrix { get; private set; }

    /// <summary>Whether <see cref="Plan"/> asked for the map to be drawn this frame.</summary>
    public bool Redraw { get; private set; }

    /// <summary>
    /// Decides this frame's map. <paramref name="sunDirection"/> is towards the shadow-casting sun, or
    /// null for none. <paramref name="radius"/> is how far round the camera the map must reach.
    /// </summary>
    public void Plan(Vector3? sunDirection, Vector3 camera, float radius, long frame)
    {
        Redraw = false;
        if (sunDirection is not { } direction)
        {
            Matrix = default;
            _drawn = false;
            return;
        }

        bool due = !_drawn
            || direction != _direction
            || radius != _radius
            || Vector3.Distance(camera, _center) > radius * 2f / 8f
            || frame - _drawnFrame >= RedrawFrames;
        if (due)
        {
            _direction = direction;
            _center = camera;
            _radius = radius;
            _drawnFrame = frame;
            _drawn = true;
            Redraw = true;
            Matrix = ShadowPass.CreateMatrix(direction, camera, radius, radius, Resolution);
        }
    }

    /// <summary>
    /// Draws the map if <see cref="Plan"/> asked for it, through <paramref name="shadows"/>' pipelines,
    /// and leaves it readable. The first frame readies it even without a sun, as the capture binds it.
    /// </summary>
    public void Record(
        Vk api, CommandBuffer cmd, ShadowPass shadows, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, List<Mesh?> meshes,
        List<PartDetail> details, InstanceBuffer instances)
    {
        const PipelineStageFlags2 readStages = PipelineStageFlags2.FragmentShaderBit;
        const AccessFlags2 read = AccessFlags2.ShaderSampledReadBit;
        const PipelineStageFlags2 depthStages = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
        const AccessFlags2 depthAccess = AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentReadBit;

        if (Redraw)
        {
            api.Transition(cmd, _target.Image, ImageAspectFlags.DepthBit,
                ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal, readStages, read, depthStages, depthAccess);
            shadows.RecordInto(cmd, frameSet, draws, meshes, details, instances, _target.View, new Extent2D(Resolution, Resolution), cascade: -1);
            api.Transition(cmd, _target.Image, ImageAspectFlags.DepthBit,
                ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, depthStages, AccessFlags2.DepthStencilAttachmentWriteBit, readStages, read);
            _ready = true;
        }
        else if (!_ready)
        {
            api.Transition(cmd, _target.Image, ImageAspectFlags.DepthBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, readStages, read, readStages, read);
            _ready = true;
        }
    }

    public void Dispose() => _target.Dispose();
}
