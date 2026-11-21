using System.Numerics;
using Rendition.Passes;

namespace Rendition;

/// <summary>
/// Writes a frame's uniforms: the camera, this frame's and last frame's, the picture's grade and the view's effects,
/// the probes, the sky, the shadows and the lights.
/// </summary>
internal sealed unsafe class FrameUniformWriter(FrameParts parts, FrameBakes bakes, FrameCameras cameras)
{
    // How much sharper a mip level material textures are read at while anti-aliasing shifts the
    // view. Each frame alone would shimmer, but the frames blended together settle, and the
    // picture keeps detail the blend would otherwise soften.
    private const float TemporalMipBias = -0.75f;

    /// <summary>OFF_DEBUG_VIEW in common.glsl: tonemapping and the finish pass pass colours through untouched.</summary>
    private const int DebugViewBit = 1 << 30;

    /// <summary>
    /// OFF_FADES in common.glsl, for <see cref="RenderEffects.Fades"/>, which is past the bits the shaders read. It
    /// takes the bit of <see cref="RenderEffects.DepthFirst"/>, which only the CPU reads.
    /// </summary>
    private const int FadesOffBit = 1 << 29;

    /// <summary>The effects the shaders read through <c>frame.off</c>, an <c>int</c>. The renderer leaves out the rest itself.</summary>
    private const RenderEffects ShadedEffects = (RenderEffects)0xFFFF_FFFFL;

    /// <summary>What the last frame's uniforms gave, for tests.</summary>
    public LastFrame Last { get; } = new();

    /// <summary>
    /// Whether a baked space's probes blend into the live light over one probe spacing in from its box's faces, as they
    /// always do; off, the box is a hard edge, as before the band. For tests, to show the band is what keeps a moving
    /// thing from jumping as it crosses the box.
    /// </summary>
    public bool BakedProbeBand { get; set; } = true;

    /// <summary>The frame slot whose uniforms were written last, so the probe test hooks read what that frame did.</summary>
    public int LastSlot { get; private set; }

    /// <summary>
    /// Writes the frame's uniforms, with the camera's view shifted by the camera's shift, and last frame's. Returns
    /// whether a light casts a shadow this frame.
    /// </summary>
    public bool Write(in FrameSlot frame, in FrameView view, in FrameCamera camera, ReadOnlySpan<Vector4> casters)
    {
        var (stores, lights, gBuffer) = (parts.SceneStores, parts.SceneLighting, parts.FrameTargets.GBuffer);
        var switches = stores.Switches;
        bool historyValid = gBuffer.Picture.HistoryReady;
        var uniforms = (FrameUniforms*)frame.Uniforms.Mapped;
        WriteCamera(uniforms, view, camera);
        WriteEffects(uniforms, view, camera.Shift, historyValid);
        uniforms->CountBlock.ProbeCount = lights.ReflectionProbes.Prepare(
            view.View, view.Projection, new Span<GpuReflectionProbe>(frame.ReflectionProbes.Mapped, ReflectionProbeOrder.MaxProbes));
        ProbeUniforms.WriteLive(uniforms, view, lights.LightProbes);
        ProbeUniforms.WriteBaked(uniforms, bakes, BakedProbeBand);
        WriteSky(uniforms, view);
        lights.FogStage.WriteWater(uniforms, view);
        var air = uniforms->AirBlock.AirExtinction;
        lights.CloudStage.Write(uniforms, view, camera.Previous, historyValid, new Vector3(air.X, air.Y, air.Z));
        lights.Planar.Write(uniforms, view);

        uniforms->CountBlock.Time = view.Time;
        uniforms->CountBlock.PreviousTime = historyValid ? cameras.PreviousTime : view.Time;
        bool castsShadow = lights.SunShadows.Write(uniforms, view, casters, stores.Settings.MaxShadowCascades, bakes.MaskSlots.Sun);
        uniforms->CountBlock.Off = unchecked((int)(switches.Off & ShadedEffects & ~RenderEffects.DepthFirst))
            | (switches.Off.HasFlag(RenderEffects.Fades) ? FadesOffBit : 0)
            | (switches.TraceView ? DebugViewBit : 0);
        uniforms->CountBlock.MipBias = switches.Off.HasFlag(RenderEffects.AntiAliasing) ? 0f
            : parts.FrameTargets.Upscaling ? FsrMipBias(gBuffer.Extent.Width, gBuffer.Picture.Display.Width)
            : TemporalMipBias;
        lights.LocalLightShadows.Write(frame, uniforms, view, gBuffer.Extent, (bakes.MaskSlots.Channels, lights.ReflectionProbes.Order));

        // Probe updates and reflection probe captures light their faces with every point and spot light of the frame,
        // and see the sun's shadow through maps of their own, each zero when there is none this frame.
        uniforms->CountBlock.ProbeLightCount = lights.LocalLightShadows.ProbeLightCount;
        uniforms->SunBlock.ProbeSunMatrix = lights.LightProbes.SunMatrix;
        uniforms->SunBlock.ReflectionSunMatrix = lights.FrameReflectionCapture.SunMatrix;
        LastSlot = stores.Ring.Index;
        return castsShadow;
    }

    /// <summary>The camera's matrices, this frame's and last frame's, and which frame this is.</summary>
    private void WriteCamera(FrameUniforms* uniforms, in FrameView view, in FrameCamera camera)
    {
        var viewProjection = view.View * view.Projection * Matrix4x4.CreateTranslation(camera.Shift.X, camera.Shift.Y, 0f);
        Matrix4x4.Invert(viewProjection, out var inverse);
        uniforms->CameraBlock.ViewProjection = viewProjection;
        uniforms->CameraBlock.View = view.View;
        uniforms->CameraBlock.InverseViewProjection = inverse;
        uniforms->CameraBlock.PreviousViewProjection = camera.Previous;
        uniforms->CountBlock.FrameNumber = cameras.FrameNumber;
        uniforms->CountBlock.HistoryValid = parts.FrameTargets.GBuffer.Picture.HistoryReady ? 1 : 0;
        uniforms->CountBlock.ShowEnvironment = view.Environment.ShowAsBackground ? 1 : 0;
    }

    /// <summary>The picture's grade and the view's haze, occlusion, outlines, fog, puddles and wind.</summary>
    private void WriteEffects(FrameUniforms* uniforms, in FrameView view, Vector2 shift, bool historyValid)
    {
        var (off, settings) = (parts.SceneStores.Switches.Off, parts.SceneStores.Settings);
        var post = PostGrade.Without(view.Picture.Post, off);
        uniforms->GradeBlock.Grade= new Vector4(MathF.Pow(2f, post.Exposure), 1f + post.Contrast, 1f + post.Saturation, post.Vignette);
        uniforms->GradeBlock.Finish = new Vector4(post.Sharpen, post.Grain, shift.X, shift.Y);
        (uniforms->GradeBlock.Lift, uniforms->GradeBlock.Gain) = PostGrade.Tint(post);
        uniforms->ToneBlock = PostGrade.Tone(post);
        // A paper whose texture is gone, or never was, reads as none rather than a slot the table no longer fills.
        int canvas = post.Screen.Canvas is { } paper && parts.SceneStores.Resources.Textures.IsLive(paper.Index) ? paper.Index : -1;
        uniforms->ScreenBlock = PostGrade.Screen(post.Screen, canvas);
        (uniforms->GradeBlock.HazeOrigin, uniforms->GradeBlock.HazeColor) = Haze.Pack(view.CameraPosition, view.Background.FadeOrigin, view.Background.FadeColor);
        uniforms->Occlusion = Occlusion.Pack(view.ScreenSpace.Occlusion, view.ScreenSpace.Bounce, off, historyValid);
        (uniforms->OutlineBlock.Outline, uniforms->OutlineBlock.OutlineFade) = Outlines.Pack(view.ScreenSpace.Outlines, parts.FrameTargets.GBuffer.Extent.Width / (float)Math.Max(view.Width, 1));
        uniforms->SkyBlock.Fog= Fog.Pack(view.Weather.Fog, off, underwater: parts.SceneStores.Switches.Underwater(view));
        parts.SceneLighting.FogStage.Write(uniforms, view, historyValid, (settings.VolumetricFogCellSize, settings.VolumetricFogSlices));
        uniforms->WeatherBlock.Puddles = Puddles.Pack(view.Weather.Puddles, off);
        uniforms->WeatherBlock.Wind = Wind.Pack(view.Weather.Wind, off);
    }

    /// <summary>The camera's place, the environment's light, the sky's sun and night, and the shadows' settings.</summary>
    private void WriteSky(FrameUniforms* uniforms, in FrameView view)
    {
        var (sky, off) = (parts.SceneLighting.EnvironmentSky, parts.SceneStores.Switches.Off);
        uniforms->CameraBlock.CameraPosition = new Vector4(view.CameraPosition, 1f);
        uniforms->BackgroundBlock.BackgroundRadiance = new Vector4(view.Background.Radiance, view.Environment.Intensity);
        var flat = sky.Map.Flat;
        uniforms->BakedBlock.FlatIrradiance = new Vector4(flat.Irradiance, 0f);
        uniforms->BakedBlock.FlatReflection = new Vector4(flat.Reflection, 0f);
        Last.Flat = flat;

        // Probes leave the sun's disc out: their rough levels would scatter it into specks, and
        // the sun's own light already gives surfaces its highlight. The sky is the one BakeSky worked out from the
        // original view, so a frame drawn without its baked sun still shows that sun's disc.
        var baked = sky.Baked;
        uniforms->SkyBlock.SkySun = new Vector4(baked.Direction, baked.Turbidity);
        var disc = new Vector4(baked.DiscRadiance, baked.DiscCosine);
        uniforms->SkyBlock.SunDisc = disc;
        Last.FromCamera(disc, view.Lights);

        // The physical sky's air between the eye and far surfaces; see SkyModel.AirShows for where it shows.
        (uniforms->AirBlock.AirExtinction, uniforms->AirBlock.AirSun) = Atmosphere.Air(
            baked.Turbidity, baked.Direction, view.Environment.Intensity, SkyModel.AirShows(view, off));
        (uniforms->AirBlock.Moon, uniforms->AirBlock.MoonRadiance, uniforms->AirBlock.Stars) = SkyModel.Night(view, baked.Direction, off);

        // Moving points two texels of the first cascade off their surface stops them
        // shadowing themselves. The lighting pass works this out per cascade itself.
        float normalOffset = view.Shadows.Radius * 4f / parts.SceneLighting.ShadowPass.Resolution;
        // A fade from 0 to 0 is none: the shaders fade only where the end lies past the start.
        bool haze = !off.HasFlag(RenderEffects.Haze);
        uniforms->BackgroundBlock.Parameters = new Vector4(
            haze ? view.Background.FadeStart : 0f, haze ? view.Background.FadeEnd : 0f, view.Shadows.LightAngle, normalOffset);
    }

    /// <summary>
    /// The mip bias FSR asks for: textures read as sharp as the window's pixels would read them, and one level sharper
    /// still, since FSR gathers the detail over its frames. -1 at a render scale of 1, -2 at 0.5.
    /// </summary>
    private static float FsrMipBias(uint sceneWidth, uint displayWidth) =>
        MathF.Log2((float)sceneWidth / displayWidth) - 1f;
}
