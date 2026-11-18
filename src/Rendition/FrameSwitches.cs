using System.Numerics;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// What the frame being drawn leaves out, taken from its view at the start of the frame, and what follows from that:
/// the debug view, the wind's reach and the fade rules.
/// </summary>
internal sealed class FrameSwitches
{
    /// <summary>What the trace debug view leaves out, so its colours reach the screen as they were traced.</summary>
    private const RenderEffects TraceViewOff =
        RenderEffects.AntiAliasing | RenderEffects.Bloom | RenderEffects.DepthOfField | RenderEffects.AutoExposure;

    /// <summary>The effects this frame leaves out.</summary>
    public RenderEffects Off { get; private set; }

    /// <summary>Whether this frame shows the trace debug view, which leaves out what would change its colours.</summary>
    public bool TraceView { get; private set; }

    /// <summary>Whether this frame shows the probe debug view: the lit scene with a sphere at every light probe.</summary>
    public bool ProbeView { get; private set; }

    /// <summary>How far the frame's wind can move each material's vertices, which culling adds to the bounds.</summary>
    public WindReach WindReach { get; private set; }

    /// <summary>How far away each draw fades out this frame.</summary>
    public FadeRules FadeRules { get; private set; }

    /// <summary>Whether views leave out what they can't see, which <see cref="RenderEffects.Culling"/> off stops.</summary>
    public bool Culls => !Off.HasFlag(RenderEffects.Culling);

    /// <summary>
    /// Takes the view's debug view, and throws when it is the trace view on a GPU that can't trace, whose reason is
    /// <paramref name="noTraceReason"/>. Returns the effects the view leaves out: the trace debug view shows colours as
    /// they are, so nothing that blends, blurs or brightens them runs.
    /// </summary>
    public RenderEffects TakeDebugView(in FrameView view, bool canTrace, string? noTraceReason)
    {
        bool traceView = view.Picture.DebugView == DebugView.Trace;
        if (traceView && !canTrace)
        {
            throw new InvalidOperationException(
                $"The trace debug view needs a GPU that can trace rays, and this one can't ({noTraceReason}).");
        }

        TraceView = traceView;
        ProbeView = view.Picture.DebugView == DebugView.Probes;
        return TraceView ? view.Off | TraceViewOff : view.Off;
    }

    /// <summary>Takes the effects the frame leaves out, and the wind's reach and the fade rules that follow from them.</summary>
    public void Take(in FrameView view, RenderEffects viewOff, MaterialTable materials)
    {
        // Metering is left out of the shaders unless the view asked for it, so the fixed exposure stays as it was.
        Off = Exposure.ShaderOff(view.Picture.Post, viewOff & RenderEffects.All);
        WindReach = new WindReach(Wind.Strength(view.Weather.Wind, Off), materials.SwayOf);
        FadeRules = new FadeRules(view.CameraPosition, !Off.HasFlag(RenderEffects.Fades), materials.FadeOf);
    }

    /// <summary>
    /// How many pixels a simpler model may stray by in a view that allows <paramref name="error"/> at a mesh detail of 1,
    /// or 0, full detail, with <see cref="RenderEffects.SimplerModels"/> off.
    /// </summary>
    public float DetailError(float error, float meshDetailScale) =>
        Off.HasFlag(RenderEffects.SimplerModels) ? 0f : error / meshDetailScale;

    /// <summary>
    /// The point and spot lights the frame draws: none while <see cref="RenderEffects.LocalLights"/> is off. Bakes
    /// keep reading the view's own, so the switch never makes a bake stale.
    /// </summary>
    public ReadOnlySpan<LocalLight> DrawnLocalLights(in FrameView view) =>
        Off.HasFlag(RenderEffects.LocalLights) ? [] : view.LocalLights;

    /// <summary>Whether the camera is under the view's water, which it never is while <see cref="RenderEffects.Underwater"/> is off.</summary>
    public bool Underwater(in FrameView view) =>
        view.Weather.Water.Absorption != Vector3.Zero && !Off.HasFlag(RenderEffects.Underwater);

    /// <summary>How far the volumetric fog reaches under water, the shafts' distance, or null above water.</summary>
    public float? UnderwaterReach(in FrameView view) => Underwater(view) ? view.Weather.Water.ShaftDistance : null;

    /// <summary>
    /// The direction towards the sun that casts this frame's shadow, or null when none does or sun shadows are off.
    /// </summary>
    public Vector3? ShadowSun(in FrameView view)
    {
        if (view.Shadows.Radius <= 0f || Off.HasFlag(RenderEffects.SunShadows))
        {
            return null;
        }

        int lights = Math.Min(view.Lights.Length, FrameUniforms.MaxLights);
        for (int i = 0; i < lights; i++)
        {
            if (view.Lights[i].CastsShadow)
            {
                return view.Lights[i].Direction;
            }
        }

        return null;
    }
}
