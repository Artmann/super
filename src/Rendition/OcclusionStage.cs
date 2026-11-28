using System.Runtime.InteropServices;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using Silk.NET.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>
/// The ambient occlusion and the light bounced from the screen: the trace, the average over the last frames and the
/// blur, drawn through the GPU layer. See "Ambient occlusion" in docs/design.md.
/// </summary>
internal sealed unsafe class OcclusionStage : IDisposable
{
    private readonly Vk _api;
    private readonly VulkanGpu _gpu;
    private readonly HistoryCopier _copier;
    private readonly FullscreenPass _aoPass;
    private readonly FullscreenPass _aoSteadyPass;
    private readonly FullscreenPass _aoBlurPass;
    private readonly CommandList _commands = new();
    private readonly AdoptedTargets _targets;
    private readonly SamplerId _linear;

    // The trace's images, the average's, and the blur's: across from the trace or the average, then down.
    private readonly ImageGroup[] _groups;

    public OcclusionStage((VulkanContext Context, VulkanGpu Gpu) device, DescriptorKit kit, HistoryCopier copier)
    {
        var (context, gpu) = device;
        _api = context.Api;
        _gpu = gpu;
        _copier = copier;
        _targets = new AdoptedTargets(gpu);
        _linear = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear));
        var nearest = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Nearest));
        _groups = [.. Enumerable.Range(0, (int)Group.Count).Select(_ => new ImageGroup(gpu, nearest))];

        // Group 1 is each pass's own, laid out from its shader; the frame's set is lent.
        ReadOnlySpan<DescriptorSetLayout> borrowed = [kit.FrameSetLayout, default];
        _aoPass = new FullscreenPass(gpu, "ao.frag", [Format(GBuffer.AoFormat), Format(GBuffer.BounceFormat)], borrowed: borrowed);
        _aoBlurPass = new FullscreenPass(gpu, "ao_blur.frag", [Format(GBuffer.AoFormat), Format(GBuffer.BounceFormat)], borrowed: borrowed);
        _aoSteadyPass = new FullscreenPass(gpu, "ao_temporal.frag", [Format(GBuffer.AoSteadyFormat), Format(GBuffer.AoSteadyFormat)], borrowed: borrowed);
    }

    private enum Group
    {
        Trace,
        Steady,
        Across,
        SteadyAcross,
        Down,
        Count,
    }

    /// <summary>
    /// Works out the ambient occlusion and the light bounced from the screen, from the G-buffer just drawn and last
    /// frame's picture, and takes their grain away, as two timed passes: the trace, and then the average over the last
    /// frames and the blur. Left out, the lighting pass reads neither, but it still binds the targets.
    /// </summary>
    public void Record(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, RenderEffects off, GpuTimer timer)
    {
        var occlusion = targets.Occlusion;
        if (off.HasFlag(RenderEffects.AmbientOcclusion))
        {
            occlusion.HistoryReady = false;
            MakeReadable(cmd, occlusion.AoBlurred.Image, occlusion.BounceBlurred.Image);
            timer.Mark(cmd);
            timer.Mark(cmd);
            return;
        }

        var extent = occlusion.Extent;
        int scale = occlusion.Resolution == OcclusionResolution.Full ? 1 : 2;
        var frame = _gpu.Adopt(frameSet);

        // Averaged with the frames before, where there are any to keep, and that average kept for the next frame.
        bool averaged = !off.HasFlag(RenderEffects.OcclusionHistory);
        var constants = new AoConstants(scale, AoSlices(scale, averaged));
        _commands.Reset();
        _aoPass.Record(_commands, [_targets.For(occlusion.Ao, extent), _targets.For(occlusion.Bounce, extent)],
            [frame, _groups[(int)Group.Trace].Id], new ReadOnlySpan<byte>(&constants, sizeof(AoConstants)));
        _gpu.Play(_commands, cmd);
        timer.Mark(cmd);

        if (averaged)
        {
            RecordAverage(cmd, occlusion, frame, scale);
        }

        occlusion.HistoryReady = averaged;

        // Blurred across into a second pair of images, then down into the pair the lighting reads.
        var across = _groups[(int)(averaged ? Group.SteadyAcross : Group.Across)];
        _commands.Reset();
        RecordBlur([occlusion.AoAcross, occlusion.BounceAcross], [frame, across.Id], new AoBlurStep(1, 0, scale), extent);
        RecordBlur([occlusion.AoBlurred, occlusion.BounceBlurred], [frame, _groups[(int)Group.Down].Id], new AoBlurStep(0, 1, scale), extent);
        _gpu.Play(_commands, cmd);
        timer.Mark(cmd);
    }

    /// <summary>
    /// Averages this frame's occlusion and bounce with the frames before, where the history still shows the same
    /// surface, and copies the average to the history for the next frame. <paramref name="scale"/> is screen pixels
    /// per pixel of occlusion.
    /// </summary>
    private void RecordAverage(CommandBuffer cmd, OcclusionTargets occlusion, BindGroupId frame, int scale)
    {
        if (!occlusion.HistoryReady)
        {
            // The pass binds the history even before there is any.
            MakeReadable(cmd, occlusion.AoHistory.Image, occlusion.BounceHistory.Image);
        }

        var extent = occlusion.Extent;
        var steady = new AoSteadyConstants(scale, occlusion.HistoryReady ? 1 : 0);
        _commands.Reset();
        _aoSteadyPass.Record(_commands, [_targets.For(occlusion.AoSteady, extent), _targets.For(occlusion.BounceSteady, extent)],
            [frame, _groups[(int)Group.Steady].Id], new ReadOnlySpan<byte>(&steady, sizeof(AoSteadyConstants)));
        _gpu.Play(_commands, cmd);

        // The layer leaves the average ready to sample.
        _copier.RecordCopy(cmd, occlusion.AoSteady.Image, occlusion.AoHistory.Image, extent);
        _copier.RecordCopy(cmd, occlusion.BounceSteady.Image, occlusion.BounceHistory.Image, extent);
    }

    /// <summary>Lets a pass sample an occlusion and a bounce image nothing has drawn, whatever they held.</summary>
    private void MakeReadable(CommandBuffer cmd, Image occlusion, Image bounce)
    {
        foreach (var image in (ReadOnlySpan<Image>)[occlusion, bounce])
        {
            _api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal), (SampledStage, SampledAccess), (SampledStage, SampledAccess));
        }
    }

    /// <summary>
    /// One direction of the occlusion and bounce blur: <paramref name="step"/> is one pixel along it, and
    /// <paramref name="groups"/> the frame's set and then the group with the occlusion and bounce to blur.
    /// </summary>
    private void RecordBlur(ReadOnlySpan<RenderTarget> occlusionAndBounce, ReadOnlySpan<BindGroupId> groups, AoBlurStep step, Extent2D extent) =>
        _aoBlurPass.Record(_commands, [_targets.For(occlusionAndBounce[0], extent), _targets.For(occlusionAndBounce[1], extent)],
            groups, new ReadOnlySpan<byte>(&step, sizeof(AoBlurStep)));

    /// <summary>
    /// Points the trace, the average and the blur's two steps at the targets. Makes the bind groups again, so it must
    /// only be called while the GPU is idle.
    /// </summary>
    public void WriteSets(GBuffer targets, ImageView depth, ImageView normal, ImageView shadowDepth)
    {
        var occlusion = targets.Occlusion;
        _targets.Clear();
        _groups[(int)Group.Trace].Make(_aoPass.Pipeline, 1,
        [
            ("depthTexture", depth),
            ("normalRoughnessAoTexture", normal),
            ("shadowDepthTexture", shadowDepth),
            new GroupImage("historyTexture", targets.Picture.History.View, _linear),
        ]);
        _groups[(int)Group.Steady].Make(_aoSteadyPass.Pipeline, 1,
        [
            ("occlusionTexture", occlusion.Ao.View),
            ("bounceTexture", occlusion.Bounce.View),
            ("occlusionHistory", occlusion.AoHistory.View),
            ("bounceHistory", occlusion.BounceHistory.View),
            ("depthTexture", depth),
            ("motionTexture", targets.Motion.View),
        ]);
        MakeBlur(Group.Across, (occlusion.Ao, occlusion.Bounce), depth, normal);
        MakeBlur(Group.SteadyAcross, (occlusion.AoSteady, occlusion.BounceSteady), depth, normal);
        MakeBlur(Group.Down, (occlusion.AoAcross, occlusion.BounceAcross), depth, normal);
    }

    private void MakeBlur(Group group, (RenderTarget Occlusion, RenderTarget Bounce) blurred, ImageView depth, ImageView normal) =>
        _groups[(int)group].Make(_aoBlurPass.Pipeline, 1,
        [
            ("occlusionTexture", blurred.Occlusion.View),
            ("bounceTexture", blurred.Bounce.View),
            ("depthTexture", depth),
            ("normalRoughnessAoTexture", normal),
        ]);

    public void Dispose()
    {
        foreach (var group in _groups)
        {
            group.Dispose();
        }

        _targets.Dispose();
        _aoBlurPass.Dispose();
        _aoSteadyPass.Dispose();
        _aoPass.Dispose();
    }

    private static GpuFormat Format(Format format) => VulkanFormats.ToGpu(format);

    /// <summary>ao_blur.frag's constants: one pixel along the way a pass blurs, and screen pixels per pixel of occlusion.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AoBlurStep(int X, int Y, int Scale);

    /// <summary>ao.frag's constants: screen pixels per pixel of occlusion, and how many slices each pixel traces.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AoConstants(int Scale, int Slices);

    /// <summary>
    /// How many slices round a pixel the occlusion traces a frame. At full size with the frames averaged one does, a
    /// different way each frame, and the average gathers the turns. That is still twice the slices a screen pixel gets
    /// at half size, where each pixel traces two every fourth frame. Without the average, or at half size, two do.
    /// </summary>
    private static int AoSlices(int scale, bool averaged) => scale == 1 && averaged ? 1 : 2;

    /// <summary>ao_temporal.frag's constants: screen pixels per pixel of occlusion, and 1 when there is a history to read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AoSteadyConstants(int Scale, int HistoryValid);
}
