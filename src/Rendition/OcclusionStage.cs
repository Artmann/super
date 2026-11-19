using System.Runtime.InteropServices;
using Rendition.Passes;
using Rendition.Vulkan;
using Silk.NET.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>
/// The ambient occlusion and the light bounced from the screen: the trace, the average over the last frames and the
/// blur, with their passes and sets. See "Ambient occlusion" in docs/design.md.
/// </summary>
internal sealed unsafe class OcclusionStage : IDisposable
{
    private readonly VulkanContext _context;
    private readonly Vk _api;
    private readonly DescriptorKit _kit;
    private readonly HistoryCopier _copier;
    private readonly FullscreenPass _aoPass;
    private readonly FullscreenPass _aoSteadyPass;
    private readonly FullscreenPass _aoBlurPass;
    private readonly DescriptorSet _aoSet;
    private readonly DescriptorSet _aoAcrossSet;
    private readonly DescriptorSet _aoSteadySet;
    private readonly DescriptorSet _aoSteadyAcrossSet;
    private readonly DescriptorSet _aoDownSet;

    public OcclusionStage(VulkanContext context, DescriptorKit kit, HistoryCopier copier)
    {
        _context = context;
        _api = context.Api;
        _kit = kit;
        _copier = copier;
        _aoSet = kit.NewSet(kit.FourTextureLayout);
        _aoAcrossSet = kit.NewSet(kit.FourTextureLayout);
        _aoSteadySet = kit.NewSet(kit.SixTextureLayout);
        _aoSteadyAcrossSet = kit.NewSet(kit.FourTextureLayout);
        _aoDownSet = kit.NewSet(kit.FourTextureLayout);
        _aoPass = new FullscreenPass(
            context, "ao.frag", [GBuffer.AoFormat, GBuffer.BounceFormat], [kit.FrameSetLayout, kit.FourTextureLayout], sizeof(int));
        _aoBlurPass = new FullscreenPass(
            context, "ao_blur.frag", [GBuffer.AoFormat, GBuffer.BounceFormat], [kit.FrameSetLayout, kit.FourTextureLayout], 3 * sizeof(int));
        _aoSteadyPass = new FullscreenPass(
            context, "ao_temporal.frag", [GBuffer.AoSteadyFormat, GBuffer.AoSteadyFormat], [kit.FrameSetLayout, kit.SixTextureLayout], 2 * sizeof(int));
    }

    /// <summary>
    /// Works out the ambient occlusion and the light bounced from the screen, from the G-buffer just drawn and last
    /// frame's picture, and takes their grain away, as two timed passes: the trace, and then the average over the last
    /// frames and the blur. Left out, the lighting pass reads neither, but it still binds the targets.
    /// </summary>
    public void Record(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, RenderEffects off, GpuTimer timer)
    {
        if (off.HasFlag(RenderEffects.AmbientOcclusion))
        {
            targets.Occlusion.HistoryReady = false;
            _api.Transition(cmd, new ImageTransition(targets.Occlusion.AoBlurred.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal), (SampledStage, SampledAccess), (SampledStage, SampledAccess));
            _api.Transition(cmd, new ImageTransition(targets.Occlusion.BounceBlurred.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal), (SampledStage, SampledAccess), (SampledStage, SampledAccess));
            timer.Mark(cmd);
            timer.Mark(cmd);
            return;
        }

        var extent = targets.Occlusion.Extent;
        int scale = targets.Occlusion.Resolution == OcclusionResolution.Full ? 1 : 2;
        ReadOnlySpan<DescriptorSet> aoSets = [frameSet, _aoSet];
        _api.BeginColorTarget(cmd, targets.Occlusion.Ao.Image);
        _api.BeginColorTarget(cmd, targets.Occlusion.Bounce.Image);
        _aoPass.Record(cmd, [targets.Occlusion.Ao.View, targets.Occlusion.Bounce.View], extent, aoSets, new ReadOnlySpan<byte>(&scale, sizeof(int)));
        _api.EndColorTarget(cmd, targets.Occlusion.Ao.Image);
        _api.EndColorTarget(cmd, targets.Occlusion.Bounce.Image);
        timer.Mark(cmd);

        // Averaged with the frames before, where there are any to keep, and that average kept for the next frame.
        bool averaged = !off.HasFlag(RenderEffects.OcclusionHistory);
        if (averaged)
        {
            RecordAverage(cmd, targets, frameSet, scale);
        }

        targets.Occlusion.HistoryReady = averaged;

        // Blurred across into a second pair of images, then down into the pair the lighting reads.
        var across = averaged ? _aoSteadyAcrossSet : _aoAcrossSet;
        RecordBlur(cmd, [targets.Occlusion.AoAcross, targets.Occlusion.BounceAcross], [frameSet, across], new AoBlurStep(1, 0, scale), extent);
        RecordBlur(cmd, [targets.Occlusion.AoBlurred, targets.Occlusion.BounceBlurred], [frameSet, _aoDownSet], new AoBlurStep(0, 1, scale), extent);
        timer.Mark(cmd);
    }

    /// <summary>
    /// Averages this frame's occlusion and bounce with the frames before, where the history still shows the same
    /// surface, and copies the average to the history for the next frame. <paramref name="scale"/> is screen pixels
    /// per pixel of occlusion.
    /// </summary>
    private void RecordAverage(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, int scale)
    {
        if (!targets.Occlusion.HistoryReady)
        {
            // The pass binds the history even before there is any.
            _api.Transition(cmd, new ImageTransition(targets.Occlusion.AoHistory.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal), (SampledStage, SampledAccess), (SampledStage, SampledAccess));
            _api.Transition(cmd, new ImageTransition(targets.Occlusion.BounceHistory.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal), (SampledStage, SampledAccess), (SampledStage, SampledAccess));
        }

        var extent = targets.Occlusion.Extent;
        var steady = new AoSteadyConstants(scale, targets.Occlusion.HistoryReady ? 1 : 0);
        _api.BeginColorTarget(cmd, targets.Occlusion.AoSteady.Image);
        _api.BeginColorTarget(cmd, targets.Occlusion.BounceSteady.Image);
        _aoSteadyPass.Record(cmd, [targets.Occlusion.AoSteady.View, targets.Occlusion.BounceSteady.View], extent, [frameSet, _aoSteadySet],
            new ReadOnlySpan<byte>(&steady, sizeof(AoSteadyConstants)));
        _copier.RecordCopy(cmd, targets.Occlusion.AoSteady.Image, targets.Occlusion.AoHistory.Image, extent, drawnJustNow: true);
        _copier.RecordCopy(cmd, targets.Occlusion.BounceSteady.Image, targets.Occlusion.BounceHistory.Image, extent, drawnJustNow: true);
    }

    /// <summary>
    /// One direction of the occlusion and bounce blur: <paramref name="step"/> is one pixel along it, and
    /// <paramref name="sets"/> the frame's set and then the one with the occlusion and bounce to blur.
    /// </summary>
    private void RecordBlur(
        CommandBuffer cmd, ReadOnlySpan<RenderTarget> occlusionAndBounce, ReadOnlySpan<DescriptorSet> sets, AoBlurStep step,
        Extent2D extent)
    {
        var (occlusion, bounce) = (occlusionAndBounce[0], occlusionAndBounce[1]);
        _api.BeginColorTarget(cmd, occlusion.Image);
        _api.BeginColorTarget(cmd, bounce.Image);
        _aoBlurPass.Record(cmd, [occlusion.View, bounce.View], extent, sets, new ReadOnlySpan<byte>(&step, sizeof(AoBlurStep)));
        _api.EndColorTarget(cmd, occlusion.Image);
        _api.EndColorTarget(cmd, bounce.Image);
    }

    /// <summary>Points the trace, the average and the blur's two steps at the targets.</summary>
    public void WriteSets(GBuffer targets, DescriptorImageInfo depth, DescriptorImageInfo normal, DescriptorImageInfo shadowDepth)
    {
        var sampler = _kit.NearestSampler;
        ImageSets.Write(_context,_aoSet, [depth, normal, shadowDepth, new(_kit.LinearSampler, targets.Picture.History.View, ImageLayout.ShaderReadOnlyOptimal)]);
        ImageSets.Write(_context,_aoAcrossSet,
        [
            new(sampler, targets.Occlusion.Ao.View, ImageLayout.ShaderReadOnlyOptimal),
            new(sampler, targets.Occlusion.Bounce.View, ImageLayout.ShaderReadOnlyOptimal),
            depth,
            normal,
        ]);
        ImageSets.Write(_context,_aoSteadySet,
        [
            new(sampler, targets.Occlusion.Ao.View, ImageLayout.ShaderReadOnlyOptimal),
            new(sampler, targets.Occlusion.Bounce.View, ImageLayout.ShaderReadOnlyOptimal),
            new(sampler, targets.Occlusion.AoHistory.View, ImageLayout.ShaderReadOnlyOptimal),
            new(sampler, targets.Occlusion.BounceHistory.View, ImageLayout.ShaderReadOnlyOptimal),
            depth,
            new(sampler, targets.Motion.View, ImageLayout.ShaderReadOnlyOptimal),
        ]);
        ImageSets.Write(_context,_aoSteadyAcrossSet,
        [
            new(sampler, targets.Occlusion.AoSteady.View, ImageLayout.ShaderReadOnlyOptimal),
            new(sampler, targets.Occlusion.BounceSteady.View, ImageLayout.ShaderReadOnlyOptimal),
            depth,
            normal,
        ]);
        ImageSets.Write(_context,_aoDownSet,
        [
            new(sampler, targets.Occlusion.AoAcross.View, ImageLayout.ShaderReadOnlyOptimal),
            new(sampler, targets.Occlusion.BounceAcross.View, ImageLayout.ShaderReadOnlyOptimal),
            depth,
            normal,
        ]);
    }

    public void Dispose()
    {
        _aoBlurPass.Dispose();
        _aoSteadyPass.Dispose();
        _aoPass.Dispose();
    }

    /// <summary>ao_blur.frag's constants: one pixel along the way a pass blurs, and screen pixels per pixel of occlusion.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AoBlurStep(int X, int Y, int Scale);

    /// <summary>ao_temporal.frag's constants: screen pixels per pixel of occlusion, and 1 when there is a history to read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AoSteadyConstants(int Scale, int HistoryValid);
}
