using System.Numerics;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The reflection probes, which let surfaces in a box reflect what is really there, and the hashes that say when to
/// capture them again. Reached through <see cref="Renderer.Reflections"/>. See "Reflection probes" in docs/rendering.md.
/// </summary>
public sealed class RendererReflections
{
    /// <summary>The most reflection probes the renderer holds at once.</summary>
    public const int MaxProbes = ReflectionProbeOrder.MaxProbes;

    private readonly ReflectionProbes _probes;
    private readonly FrameReflectionCapture _capture;
    private readonly EnvironmentSky _sky;
    private readonly LightProbes _lightProbes;
    private readonly FrameBakes _frameBakes;

    // Points the lighting and the glass at the probes' cubes again after the array grew.
    private readonly Action _cubesGrew;

    internal RendererReflections(
        (ReflectionProbes Probes, FrameReflectionCapture Capture) probes,
        (EnvironmentSky Sky, LightProbes LightProbes, FrameBakes Bakes) lighting,
        Action cubesGrew)
    {
        (_probes, _capture) = probes;
        (_sky, _lightProbes, _frameBakes) = lighting;
        _cubesGrew = cubesGrew;
    }

    /// <summary>
    /// Adds a reflection probe, so surfaces in its box reflect what is really there, and puts it in line for its first
    /// capture. Each frame photographs the probe at the front of the line from the frame's own draws and lights, inside
    /// the frame and without waiting for the GPU, so a probe shows from a frame or a few after it's added, and surfaces
    /// reflect the sky behind it until then. Its sun shadow is framed on its box. At most <see cref="MaxProbes"/> fit.
    /// Room for <see cref="ProbeCubes.FirstCapacity"/> is made at the start, and it doubles when it runs out, which
    /// waits for the GPU once.
    /// </summary>
    public ReflectionProbeHandle Add(in ReflectionProbe probe)
    {
        if (_probes.Count == MaxProbes)
        {
            throw new InvalidOperationException($"Only {MaxProbes} reflection probes are supported.");
        }

        int capacity = _probes.Cubes.Capacity;
        var handle = _probes.Add(probe);
        if (_probes.Cubes.Capacity != capacity)
        {
            // The array grew, so the lighting and glass read the new one.
            _cubesGrew();
        }

        return handle;
    }

    /// <summary>
    /// Puts a probe <see cref="Add"/> made in line to be photographed again, placed as <paramref name="probe"/> says,
    /// which can move it or change its box. Surfaces keep reflecting its last picture until the new one is drawn. A
    /// probe already in line keeps its place.
    /// </summary>
    public void Update(ReflectionProbeHandle handle, in ReflectionProbe probe) => _probes.Update(handle, probe);

    /// <summary>Whether a probe is in line for a capture that hasn't been drawn yet.</summary>
    public bool Waiting(ReflectionProbeHandle handle) => _probes.Waiting(handle);

    /// <summary>
    /// Forgets one reflection probe from the next frame. The others keep their pictures, and the next
    /// probe added takes its place.
    /// </summary>
    public void Remove(ReflectionProbeHandle handle) => _probes.Remove(handle);

    /// <summary>Forgets every reflection probe, so surfaces reflect only the sky from the next frame.</summary>
    public void Clear() => _probes.Clear();

    /// <summary>How many reflection probes there are, captured or waiting for their first capture.</summary>
    public int Count => _probes.Count;

    /// <summary>How many reflection probe captures the frames have drawn since the renderer was made.</summary>
    public int CapturesDrawn => _capture.Drawn;

    /// <summary>
    /// A number that changes whenever something that lights every reflection probe's picture changes, so update the
    /// probes when it does. It covers the suns, the sky, whether sun shadows are drawn, the light probes and whether
    /// they have settled at least once since they were made, the bakes loaded and whether one is still fading in
    /// (see <see cref="Renderer.BakeFadeSeconds"/>), and the switches that change what a capture draws. Moving
    /// things or the camera doesn't change it, and nor do point and spot lights, which reach only the probes near
    /// them: see <see cref="LightsHash"/>.
    /// </summary>
    public int LightingHash(in FrameView view)
    {
        // Light probes fill in over many frames, and reflections captured before they first settle look wrong. Only the
        // first settle counts: the live ProbesSettled turns false on every cell the camera crosses, and updating every
        // reflection probe then would keep them all busy.
        // A capture lights surfaces with the bake's probes, so a bake loaded, dropped or swapped for another changes
        // what it sees, and so does a chunk bake arriving or going. A bake that arrives on screen is still fading in
        // then, so a capture would keep its half-faded picture; the fade ending changes the hash once more, and the
        // capture after it sees the bake whole, as one made when the level loads with the bake saved does.
        return HashCode.Combine(
            LightingHash(view, _sky.Version), _lightProbes.InUse, _lightProbes.SettledOnce, _frameBakes.DrawnKey,
            _frameBakes.Fading);
    }

    /// <summary>
    /// The part of <see cref="LightingHash(in FrameView)"/> that the view alone decides: what
    /// <see cref="ProbeLightingKey"/> holds for the light probes, which captures light surfaces as updates do, and the
    /// light probe settings and the switches for the point and spot lights. The scene's geometry and the camera aren't
    /// in it: a capture frames the sun's shadow on its own box.
    /// </summary>
    internal static int LightingHash(in FrameView view, int environmentVersion) =>
        HashCode.Combine(
            ProbeLightingKey(view, environmentVersion, view.Off),
            view.Off & (RenderEffects.LocalLights | RenderEffects.EnvironmentLight),
            view.LightProbes);

    /// <summary>
    /// A number that changes when a point or spot light that reaches a reflection probe's box appears, goes, or changes
    /// its range, cone or shadow, so update the probe when it does. Its colour, where it is and where it points aren't in
    /// it, so a flickering or swinging light doesn't keep a probe busy: update a probe near a light that moves around
    /// all the time on every frame, or by hand when a light near it dims or moves for good.
    /// </summary>
    public static int LightsHash(ReadOnlySpan<LocalLight> lights, Vector3 boxMin, Vector3 boxMax)
    {
        var hash = new HashCode();
        foreach (ref readonly var light in lights)
        {
            var nearest = Vector3.Clamp(light.Position, boxMin, boxMax);
            if (Vector3.Distance(nearest, light.Position) > light.Range)
            {
                continue;
            }

            hash.Add(light.Key);
            hash.Add(light.Range);
            hash.Add(light.InnerAngle);
            hash.Add(light.OuterAngle);
            hash.Add(light.CastsShadow);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Sums up the light that reaches every light probe at once: the suns, the environment and its
    /// intensity, and the switches that change what colour a surface sends on. When it changes, every
    /// probe is updated again. Point and spot lights are followed one by one instead, as each reaches
    /// only the probes near it. <see cref="RenderEffects.WearAndDirt"/> isn't in it, as neither the capture nor the
    /// trace draws wear or dirt. The reflection probes' <see cref="LightingHash(in FrameView)"/> holds it too.
    /// </summary>
    internal static int ProbeLightingKey(in FrameView view, int environmentVersion, RenderEffects off)
    {
        var hash = new HashCode();
        int count = Math.Min(view.Lights.Length, FrameUniforms.MaxLights);
        for (int i = 0; i < count; i++)
        {
            ref readonly var light = ref view.Lights[i];
            hash.Add(light.Direction);
            hash.Add(light.Radiance);
            hash.Add(light.CastsShadow);
        }

        hash.Add(count);
        hash.Add(environmentVersion);
        hash.Add(view.Environment.Intensity);
        hash.Add(view.Environment.Sky.Turbidity);
        hash.Add(view.Environment.Sky.Image);
        hash.Add(view.Environment.Sky.Rotation);
        hash.Add(view.Environment.Sky.Exposure);
        hash.Add(view.Shadows.Radius > 0f);
        // Under water the probes see the sun through it, so switching the water off lights them again.
        hash.Add(off & (RenderEffects.SunShadows | RenderEffects.Materials | RenderEffects.LightShadows | RenderEffects.Emissive
            | RenderEffects.Underwater));
        return hash.ToHashCode();
    }
}
