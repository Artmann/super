using System.Numerics;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// The lens flare: which lights flare and their elements' quads, worked out on the CPU by <see cref="LensFlareFrame"/>,
/// how much of each light is seen, on the GPU by <see cref="LensFlareVisibility"/>, and the quads drawn into a half-size
/// HDR target by <see cref="LensFlareDraw"/>, which tonemapping adds after bloom. See "Lens flare" in docs/design.md.
/// </summary>
internal sealed class LensFlarePass : IDisposable
{
    private readonly LensFlareVisibility _visibility;
    private readonly LensFlareDraw _draw;
    private readonly FlareSource[] _sources = new FlareSource[LensFlareSettings.MaxSources];
    private readonly FlareQuad[] _quads = new FlareQuad[LensFlareFrame.MaxQuads];
    private int _quadCount;
    private int _slot;
    private float _aspect = 1f;

    public LensFlarePass((VulkanContext Context, VulkanGpu Gpu) device, Sampler depthSampler, Extent2D display)
    {
        _visibility = new LensFlareVisibility(device, depthSampler);
        _draw = new LensFlareDraw(device.Context, _visibility.SetLayout, display);
    }

    /// <summary>The flare, half the picture's size, ready to sample once <see cref="Record"/> has run.</summary>
    public ImageView Result => _draw.Result;

    /// <summary>How many lights flare this frame, for tests.</summary>
    public int SourceCount { get; private set; }

    /// <summary>
    /// Works out which lights flare this frame and their elements' quads, before the frame is recorded. Nothing runs
    /// with the switch off, the intensity 0 or no light on screen. The quads go to the GPU in <see cref="Record"/>, once
    /// the slot's last frame is done with its buffer.
    /// </summary>
    public void Take(in FrameView view, RenderEffects off, Vector4 fog, (int Slot, bool HistoryReady, Extent2D Scene) frame)
    {
        _slot = frame.Slot;
        SourceCount = 0;
        _quadCount = 0;
        var settings = view.Picture.LensFlare;
        if (off.HasFlag(RenderEffects.LensFlare) || settings.Intensity is <= 0f)
        {
            return;
        }

        int count = LensFlareFrame.Sources(view, fog, frame.Scene.Height, _sources);
        _aspect = frame.Scene.Width / (float)Math.Max(frame.Scene.Height, 1u);
        _quadCount = LensFlareFrame.Quads(_sources.AsSpan(0, count), settings, _aspect, _quads);
        if (_quadCount > 0)
        {
            SourceCount = count;
            _visibility.Take(
                _sources.AsSpan(0, count), hold: !off.HasFlag(RenderEffects.AntiAliasing), frame.HistoryReady, clouds: CloudModel.Tier(view, off) > 0);
        }
    }

    /// <summary>Points the visibility pass at the scene's depth. Only call it while no frame in flight uses the set.</summary>
    public void SetDepth(ImageView depth) => _visibility.SetDepth(depth);

    /// <summary>Points the sun's visibility at the clouds' picture. Only call it while no frame in flight uses the set.</summary>
    public void SetClouds(ImageView picture) => _visibility.SetClouds(picture);

    /// <summary>Makes the target for a picture of a new size. Only call it while no frame in flight uses it.</summary>
    public void Resize(Extent2D display) => _draw.Resize(display);

    /// <summary>
    /// Works out how much of each light is seen and draws the flare, with the scene's depth read-only. Skipped, the
    /// target is cleared once, so the tonemap adds nothing.
    /// </summary>
    public void Record(CommandBuffer cmd)
    {
        if (_quadCount == 0)
        {
            _visibility.Skip();
            _draw.RecordSkipped(cmd);
            return;
        }

        _visibility.Record(cmd, _sources.AsSpan(0, SourceCount), _aspect);
        _draw.Record(cmd, _quads.AsSpan(0, _quadCount), _slot, _visibility.Set);
    }

    public void Dispose()
    {
        _draw.Dispose();
        _visibility.Dispose();
    }
}
