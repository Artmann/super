using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>
/// One light as the path tracer samples it. Must match BakeLight in Shaders/bake_lights.glsl. Make one with
/// <see cref="Sun"/> or <see cref="Local"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuBakeLight
{
    // A spot's fade divisor never goes below this, as in the runtime's light data.
    private const float MinFade = 1e-4f;

    public Vector4 PositionRange;
    public Vector4 RadianceRadius;
    public Vector4 DirectionCone;
    public Vector4 FadeKind;

    /// <summary>The shadow mask channel the light is measured in, or -1 for none, which both makers start with.</summary>
    public int MaskChannel
    {
        readonly get => (int)FadeKind.W;
        set => FadeKind.W = value;
    }

    /// <summary>A sun, its disc <paramref name="lightAngle"/> radians across (<see cref="ShadowSettings.LightAngle"/>).</summary>
    public static GpuBakeLight Sun(in DirectionalLight sun, float lightAngle) => new()
    {
        PositionRange = new Vector4(Vector3.Normalize(sun.Direction), 0f),
        RadianceRadius = new Vector4(sun.Radiance, MathF.Cos(lightAngle * 0.5f)),
        DirectionCone = new Vector4(0f, 0f, 0f, -2f),
        FadeKind = new Vector4(0f, 1f, sun.Mode == LightMode.Mixed ? 1f : 0f, -1f),
    };

    /// <summary>A point or spot light, dimming as the runtime dims it.</summary>
    public static GpuBakeLight Local(in LocalLight light)
    {
        float cosOuter = -2f;
        float fade = 0f;
        var direction = Vector3.Zero;
        if (light.IsSpot)
        {
            float outer = light.OuterAngle;
            float cosInner = MathF.Cos(MathF.Min(light.InnerAngle, outer));
            cosOuter = MathF.Cos(outer);
            fade = 1f / MathF.Max(cosInner - cosOuter, MinFade);
            direction = Vector3.Normalize(light.Direction);
        }

        return new GpuBakeLight
        {
            PositionRange = new Vector4(light.Position, light.Range),
            RadianceRadius = new Vector4(light.Radiance, Passes.LocalLights.RadiusOf(light)),
            DirectionCone = new Vector4(direction, cosOuter),
            FadeKind = new Vector4(fade, 0f, light.Mode == LightMode.Mixed ? 1f : 0f, -1f),
        };
    }
}
