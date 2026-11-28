using System.Globalization;
using Silk.NET.Vulkan;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>
/// What the camera's opaque draws read besides the scene: the frame's instances, its bakes and relit chunks, the sky
/// they are relit by, where the draws stood last frame, and the view whose depth is drawn first, if any.
/// </summary>
internal readonly record struct OpaqueInputs(
    InstanceBuffer Instances, FrameBakes Bakes, SkySh Sky, MotionHistory Motion, int[] CameraChunks, DetailView? DepthFirst);

/// <summary>Draws the camera's opaque draws and the decals into the G-buffer.</summary>
internal sealed unsafe class OpaqueStage
{
    private readonly Vk _api;
    private readonly FrameRing _ring;
    private readonly MeshStore _meshes;
    private readonly ResourceTable _resources;
    private readonly FrameSwitches _switches;
    private readonly DecalPass _decalPass;

    public OpaqueStage((VulkanContext Context, VulkanGpu Gpu) device, DescriptorKit kit, FrameRing ring, MeshStore meshes, (ResourceTable Resources, FrameSwitches Switches) parts)
    {
        var context = device.Context;
        _api = context.Api;
        _ring = ring;
        _meshes = meshes;
        _resources = parts.Resources;
        _switches = parts.Switches;
        _decalPass = new DecalPass(device, kit.FrameSetLayout, parts.Resources, FrameRing.Count);
        Pass = new GBufferPass(context, kit.FrameSetLayout, parts.Resources);
    }

    /// <summary>The G-buffer pass, which also keeps the surface shaders.</summary>
    public GBufferPass Pass { get; }

    /// <summary>
    /// Checks and packs the view's decals, or none when they're switched off. Throws for a decal
    /// that can't be drawn: too many, a box that can't be inverted, a weight outside 0 to 1 or a material that doesn't exist.
    /// </summary>
    public void PrepareDecals(in FrameView view)
    {
        ReadOnlySpan<DecalDraw> decals = _switches.Off.HasFlag(RenderEffects.Decals) ? [] : view.Extras.Decals;
        for (int i = 0; i < decals.Length; i++)
        {
            if (!_resources.Materials.IsLive(decals[i].Material))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Decal {i}'s material does not exist."), nameof(view));
            }
        }

        _decalPass.Prepare(decals);
    }

    /// <summary>Uploads the prepared decals for the frame's slot.</summary>
    public void UploadDecals() => _decalPass.Upload(_ring.Index);

    /// <summary>Points the decals at the targets' depth and their copy of the normals.</summary>
    public void WriteTargets(GBuffer targets) => _decalPass.WriteTargets(targets.Depth.View, targets.NormalCopy.View);

    /// <summary>
    /// Draws the camera's opaque draws and the decals into the G-buffer, and leaves it ready to sample. Draws that
    /// don't take decals, such as characters, are drawn after them, over what they laid.
    /// </summary>
    public void Record(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, in OpaqueInputs inputs)
    {
        ReadOnlySpan<Image> colors =
            [targets.BaseColor.Image, targets.Normal.Image, targets.Emissive.Image, targets.Motion.Image, targets.Baked.Image, targets.Mask.Image];
        foreach (var color in colors)
        {
            _api.Transition(cmd, new ImageTransition(color, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal), (SampledStage, SampledAccess), (ColorStage, ColorWrite));
        }

        _api.Transition(cmd, new ImageTransition(targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal), (DepthStages, AccessFlags2.DepthStencilAttachmentReadBit),
            (DepthStages, DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit));

        bool depthTarget = RecordOpaque(cmd, targets, frameSet, draws, inputs);
        foreach (var color in colors)
        {
            _api.Transition(cmd, new ImageTransition(color, ImageAspectFlags.ColorBit,
                ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal), (ColorStage, ColorWrite), (SampledStage, SampledAccess));
        }

        if (depthTarget)
        {
            _api.Transition(cmd, new ImageTransition(targets.Depth.Image, ImageAspectFlags.DepthBit,
                ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal), (DepthStages, DepthWrite), (SampledStage, SampledAccess));
        }
    }

    public void Dispose()
    {
        _decalPass.Dispose();
        Pass.Dispose();
    }

    /// <summary>
    /// Draws the opaque draws and the decals, with what the draws read: where they stood last frame, the space's
    /// lightmaps and, for the camera's draws, which know their chunk, a relit chunk's bake. Returns whether depth is
    /// still a target: the decals leave it ready to read, unless draws came after them.
    /// </summary>
    private bool RecordOpaque(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, in OpaqueInputs inputs)
    {
        var instances = inputs.Instances;

        // A chunk's bake is relit by the frame's sky and suns: its sky is projected first, for writeBaked.
        if (inputs.Bakes.Relight.Active)
        {
            inputs.Sky.Record(cmd);
            instances.Chunks = inputs.CameraChunks;
            instances.ChunkLightmaps = inputs.Bakes.Relight.Lightmaps;
        }

        instances.History = inputs.Motion;
        instances.Lightmaps = inputs.Bakes.SpaceLightmaps;
        bool late = _decalPass.Count > 0 && SceneDraws.AnyIn(draws, _resources, ScenePass.OpaqueAfterDecals);
        Pass.Record(cmd, targets, frameSet, new SceneGeometry(draws, _meshes.MeshSlots, _meshes.DetailSlots, instances),
            late ? ScenePass.OpaqueTakingDecals : ScenePass.Opaque, inputs.DepthFirst);
        bool decalsDrawn = RecordDecals(cmd, targets, frameSet);
        if (late)
        {
            RecordAfterDecals(cmd, targets, frameSet, draws, instances);
        }

        instances.History = null;
        instances.Lightmaps = null;
        instances.Chunks = null;
        instances.ChunkLightmaps = null;
        return !decalsDrawn || late;
    }

    /// <summary>
    /// Lays the prepared decals onto the G-buffer just drawn, and returns whether there were any.
    /// They read the normal target while writing it, so it is copied first and they read the copy.
    /// Depth is left ready to sample, the base colour and normal targets ready to draw into.
    /// </summary>
    private bool RecordDecals(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet)
    {
        if (_decalPass.Count == 0)
        {
            return false;
        }

        const PipelineStageFlags2 copy = PipelineStageFlags2.CopyBit;
        _api.Transition(cmd, new ImageTransition(targets.Normal.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal), (ColorStage, ColorWrite), (copy, AccessFlags2.TransferReadBit));
        _api.Transition(cmd, new ImageTransition(targets.NormalCopy.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal), (SampledStage, SampledAccess), (copy, AccessFlags2.TransferWriteBit));
        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(targets.Extent.Width, targets.Extent.Height, 1),
        };
        _api.CmdCopyImage(cmd, targets.Normal.Image, ImageLayout.TransferSrcOptimal,
            targets.NormalCopy.Image, ImageLayout.TransferDstOptimal, 1, &region);
        _api.Transition(cmd, new ImageTransition(targets.NormalCopy.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal), (copy, AccessFlags2.TransferWriteBit), (SampledStage, SampledAccess));
        // The decals load the normals and blend over them, so the change of layout must land before
        // that read as well as before their writes.
        _api.Transition(cmd, new ImageTransition(targets.Normal.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferSrcOptimal, ImageLayout.ColorAttachmentOptimal), (copy, AccessFlags2.TransferReadBit),
            (ColorStage, ColorWrite | AccessFlags2.ColorAttachmentReadBit));

        // The colour blends over what the opaque pass wrote.
        _api.Transition(cmd, new ImageTransition(targets.BaseColor.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal), (ColorStage, ColorWrite),
            (ColorStage, ColorWrite | AccessFlags2.ColorAttachmentReadBit));
        _api.Transition(cmd, new ImageTransition(targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal), (DepthStages, DepthWrite), (SampledStage, SampledAccess));

        _decalPass.Record(cmd, targets, frameSet, _ring.Index);
        return true;
    }

    /// <summary>
    /// Draws what doesn't take decals over the G-buffer the decals were laid on. The decals read depth and wrote the
    /// colour and normals, so depth goes back to being drawn into, and the draws wait for the decals' writes.
    /// </summary>
    private void RecordAfterDecals(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, InstanceBuffer instances)
    {
        _api.Transition(cmd, new ImageTransition(targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.DepthAttachmentOptimal), (SampledStage, SampledAccess),
            (DepthStages, DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit));
        _api.Transition(cmd, new ImageTransition(targets.BaseColor.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal), (ColorStage, ColorWrite),
            (ColorStage, ColorWrite | AccessFlags2.ColorAttachmentReadBit));
        _api.Transition(cmd, new ImageTransition(targets.Normal.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal), (ColorStage, ColorWrite),
            (ColorStage, ColorWrite | AccessFlags2.ColorAttachmentReadBit));
        Pass.Record(cmd, targets, frameSet, new SceneGeometry(draws, _meshes.MeshSlots, _meshes.DetailSlots, instances), ScenePass.OpaqueAfterDecals);
    }
}
