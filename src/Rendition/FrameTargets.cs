using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The window's swapchain and the scene's targets at the render scale, made again when the window, the render scale,
/// the upscaler or a resolution changes, and what tonemapping and bloom read.
/// </summary>
internal sealed class FrameTargets
{
    private readonly SceneStores _stores;
    private readonly SceneLighting _lights;
    private readonly FrameStages _stages;

    public FrameTargets(SceneStores stores, (SceneLighting Lights, FrameStages Stages) passes, (Swapchain Window, GBuffer Scene) first)
    {
        _stores = stores;
        (_lights, _stages) = passes;
        (Swapchain, GBuffer) = first;
    }

    /// <summary>The window's swapchain.</summary>
    public Swapchain Swapchain { get; private set; }

    /// <summary>The scene's targets.</summary>
    public GBuffer GBuffer { get; private set; }

    /// <summary>Whether FSR draws the picture now: only with anti-aliasing on and the scene no larger than the window.</summary>
    public bool Upscaling { get; private set; }

    /// <summary>
    /// Whether tonemapping and bloom are bound to the anti-aliased image or, with anti-aliasing left out, straight to
    /// the lit one.
    /// </summary>
    public bool ReadsAntiAliased { get; private set; } = true;

    /// <summary>What tonemapping and bloom read: the anti-aliased image, or the lit one when anti-aliasing is left out.</summary>
    public ImageView DisplaySource => ReadsAntiAliased ? GBuffer.Picture.Resolved.View : GBuffer.Hdr.View;

    /// <summary>The FSR version in use, such as "FSR 3.1.4", or null while FSR isn't drawing the picture.</summary>
    public string? UpscalerVersion => Upscaling ? _stages.FsrPass.Version : null;

    /// <summary>The first swapchain, for the window's size and vsync, and the first scene targets.</summary>
    public static (Swapchain Window, GBuffer Scene) Start(SceneStores stores, RendererOptions options)
    {
        var swapchain = new Swapchain(stores.Context, (uint)options.Width, (uint)options.Height, !stores.Settings.Vsync);
        return (swapchain, new GBuffer(stores.Context, SceneExtent(stores, swapchain.Extent)));
    }

    /// <summary>
    /// Makes the swapchain, the targets and the shadow maps again when the window, the render scale, the upscaler or a
    /// resolution changed since the last frame.
    /// </summary>
    public void Keep(in FrameView view, RenderEffects viewOff)
    {
        var settings = _stores.Settings;
        // FSR draws the picture at the window's size, so starting or stopping it changes the targets.
        bool upscaling = settings.UpscalerUsed == Upscaler.Fsr && settings.RenderScale <= 1f && !viewOff.HasFlag(RenderEffects.AntiAliasing);
        bool upscalingChanged = upscaling != Upscaling;
        Upscaling = upscaling;
        if (settings.VsyncChanged || (uint)view.Width != Swapchain.Extent.Width || (uint)view.Height != Swapchain.Extent.Height)
        {
            Resize((uint)view.Width, (uint)view.Height);
        }
        else if (upscalingChanged || TargetsChanged())
        {
            // Frames in flight still read the targets being replaced. Changing the scale, the upscaler or the
            // resolution of the reflections or the occlusion is rare.
            _stores.Context.WaitIdle();
            Rebuild();
        }

        if (settings.ShadowResolution != _lights.ShadowPass.Resolution)
        {
            // Frames in flight still read the maps being replaced. Changing this is rare.
            _stores.Context.WaitIdle();
            _lights.ShadowPass.Resize(settings.ShadowResolution);
            WriteDescriptors();
        }
    }

    /// <summary>Points bloom, tonemapping, the meter and depth of field at the anti-aliased image or the lit one.</summary>
    public void KeepDisplaySource(bool antiAliased)
    {
        if (antiAliased == ReadsAntiAliased)
        {
            return;
        }

        // Frames in flight still read the sets being rewritten. Changing this is rare.
        _stores.Context.WaitIdle();
        ReadsAntiAliased = antiAliased;
        _stages.PostStage.SetSource(DisplaySource, GBuffer);
    }

    /// <summary>
    /// Makes a new swapchain for the window's size and vsync. The scene's targets are only made again when
    /// the size changed, so switching vsync keeps the anti-aliasing history and the next picture is the
    /// same as it would have been.
    /// </summary>
    public void Resize(uint width, uint height)
    {
        _stores.Context.WaitIdle();
        var extent = Swapchain.Extent;
        Swapchain.Dispose();
        Swapchain = new Swapchain(_stores.Context, width, height, !_stores.Settings.Vsync);
        _stores.Settings.VsyncChanged = false;
        if (Swapchain.Extent.Width != extent.Width || Swapchain.Extent.Height != extent.Height)
        {
            Rebuild();
        }
    }

    /// <summary>Points the fullscreen passes at the current render targets.</summary>
    public void WriteDescriptors() => TargetDescriptors.Write(_stores, (_lights, _stages), this);

    /// <summary>Frees the scene's targets and the swapchain.</summary>
    public void Dispose()
    {
        GBuffer.Dispose();
        Swapchain.Dispose();
    }

    // Whether the scene's size, or the resolution of the reflections or the occlusion, is no longer the targets'.
    private bool TargetsChanged()
    {
        var settings = _stores.Settings;
        var scene = SceneExtent(_stores, Swapchain.Extent);
        return settings.ReflectionResolution != GBuffer.Reflections || settings.OcclusionResolution != GBuffer.Occlusion.Resolution
            || scene.Width != GBuffer.Extent.Width || scene.Height != GBuffer.Extent.Height;
    }

    /// <summary>
    /// Replaces the scene's targets with ones at the window's size times the render scale, keeping the window's
    /// swapchain, with everything that follows their size: bloom's chain, the exposure meter's dispatch and the
    /// anti-aliasing history, which starts again.
    /// </summary>
    private void Rebuild()
    {
        GBuffer.Dispose();
        var settings = _stores.Settings;
        var scene = SceneExtent(_stores, Swapchain.Extent);
        GBuffer = new GBuffer(_stores.Context, scene, Upscaling ? Swapchain.Extent : scene, settings.ReflectionResolution, settings.OcclusionResolution);
        if (Upscaling)
        {
            try
            {
                _stages.FsrPass.SetTargets(scene, Swapchain.Extent, GBuffer.Depth.View, GBuffer.Motion.View);
            }
            catch (InvalidOperationException e)
            {
                // TAA draws instead, at the scene's size, until the upscaler is asked for again.
                Console.Error.WriteLine($"{e.Message} Anti-aliasing uses TAA instead.");
                settings.UpscalerUsed = Upscaler.Taa;
                Upscaling = false;
                GBuffer.Dispose();
                GBuffer = new GBuffer(_stores.Context, scene, reflections: settings.ReflectionResolution, occlusion: settings.OcclusionResolution);
            }
        }
        else
        {
            _stages.FsrPass.Release();
        }

        _stages.PostStage.Resize(DisplaySource, GBuffer);
        _lights.FogStage.Resize(GBuffer.Extent, (settings.VolumetricFogCellSize, settings.VolumetricFogSlices));
        WriteDescriptors();
    }

    /// <summary>The scene's size for a window this size, at the render scale.</summary>
    private static Extent2D SceneExtent(SceneStores stores, Extent2D window)
    {
        var (width, height) = RenderSettings.SceneSize((int)window.Width, (int)window.Height, stores.Settings.RenderScale, stores.MaxImageSize);
        return new Extent2D((uint)width, (uint)height);
    }
}
