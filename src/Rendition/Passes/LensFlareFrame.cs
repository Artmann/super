using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition.Passes;

/// <summary>
/// A light that flares this frame, on screen: where it is, as a fraction of the screen from the top left, the depth
/// something must lie beyond to leave it seen, 0 for the sun, which only the sky leaves seen, how far round it the
/// visibility pass looks, in screen heights, and its colour, dimmed by the fog and faded towards the screen's edges.
/// </summary>
internal readonly record struct FlareSource(Vector2 Uv, float Depth, float Radius, Vector3 Color);

/// <summary>One element of one light's flare, as lens_flare.vert reads it. Must match its inputs.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FlareQuad
{
    /// <summary>The middle, as a fraction of the screen from the top left.</summary>
    public Vector2 Center;

    /// <summary>Half the width and height, as fractions of the screen.</summary>
    public Vector2 HalfSize;

    /// <summary>The colour, before the light's visibility, and in w which light's flare it is.</summary>
    public Vector4 Color;

    /// <summary>The <see cref="LensFlareShape"/>, the polygon's sides and how far it is turned.</summary>
    public Vector4 Shape;
}

/// <summary>
/// Works out which lights flare this frame and the quads of their elements, on the CPU, with nothing allocated. How much
/// of each light is seen is left to lens_flare_visibility.comp, on the GPU. See "Lens flare" in docs/design.md.
/// </summary>
internal static class LensFlareFrame
{
    /// <summary>At most this many quads are drawn: every element of every light.</summary>
    public const int MaxQuads = LensFlareSettings.MaxSources * LensFlareSettings.MaxElements;

    /// <summary>How far in from the screen's edges, as a fraction of it, a light's flare fades in.</summary>
    public const float EdgeFade = 0.08f;

    /// <summary>The fewest pixels across the visibility pass looks, so a small sun is still seen through a few of them.</summary>
    private const float LeastTapPixels = 3f;

    /// <summary>The most it looks round a light, in screen heights, so a light right by the camera doesn't read the whole screen.</summary>
    private const float MostTapRadius = 0.05f;

    /// <summary>How far a polygon is turned, so its corners don't line up with the screen.</summary>
    private const float PolygonTurn = 0.26f;

    /// <summary>How thick a streak's quad is, as a share of its width. The shader fades it to nothing well inside.</summary>
    private const float StreakThickness = 0.04f;

    /// <summary>How far the sun is taken to be, in metres, to work out the fog it shines through.</summary>
    private const float SunDistance = 1e4f;

    /// <summary>
    /// Fills <paramref name="sources"/> with the lights that flare this frame and returns how many: the sun, when it is on
    /// screen and in front, then the view's named lights, while they fit. <paramref name="fog"/> is the frame's height fog,
    /// as <see cref="Fog.Pack(in FogSettings, RenderEffects, bool)"/> packs it. The view's sun is already coloured by the air.
    /// </summary>
    public static int Sources(in FrameView view, Vector4 fog, float sceneHeight, Span<FlareSource> sources)
    {
        var viewProjection = view.View * view.Projection;
        float height = MathF.Abs(view.Projection.M22) * 0.5f;
        float leastRadius = LeastTapPixels / MathF.Max(sceneHeight, 1f);
        int count = AddSun(view, fog, viewProjection, (height, leastRadius), sources);

        var lights = view.Picture.LensFlare.Lights.Span;
        for (int i = 0; i < lights.Length && count < sources.Length; i++)
        {
            ref readonly var light = ref lights[i];
            var clip = Vector4.Transform(new Vector4(light.Position, 1f), viewProjection);
            if (!(clip.W > 0f))
            {
                continue;
            }

            // Whatever lies nearer the camera than the light's near side covers it.
            var toCamera = view.CameraPosition - light.Position;
            float distance = toCamera.Length();
            if (!(distance > light.Radius))
            {
                continue;
            }

            var near = Vector4.Transform(new Vector4(light.Position + toCamera * (light.Radius / distance), 1f), viewProjection);
            float depth = near.W > 0f ? near.Z / near.W : 1f;
            float radius = light.Radius / clip.W * height;
            float through = Fog.Transmittance(fog.X, fog.Y, fog.Z, view.CameraPosition, light.Position);
            count = Add(sources, count, clip, (MathF.Max(depth, float.Epsilon), radius, light.Color * through), leastRadius);
        }

        return count;
    }

    /// <summary>
    /// Fills <paramref name="quads"/> with every element of every light's flare and returns how many. Each is coloured by
    /// its light and scaled by the settings' intensity; how much of the light is seen is the GPU's to add.
    /// </summary>
    public static int Quads(ReadOnlySpan<FlareSource> sources, in LensFlareSettings settings, float aspect, Span<FlareQuad> quads)
    {
        float intensity = settings.Intensity ?? 1f;
        var elements = settings.Elements.IsEmpty ? LensFlareSettings.DefaultElements.Span : settings.Elements.Span;
        elements = elements[..Math.Min(elements.Length, LensFlareSettings.MaxElements)];
        int count = 0;
        for (int s = 0; s < sources.Length; s++)
        {
            ref readonly var source = ref sources[s];
            foreach (ref readonly var element in elements)
            {
                var color = element.Color * source.Color * intensity;
                if (!(element.Size > 0f) || !(MathF.Max(color.X, MathF.Max(color.Y, color.Z)) > 0f) || count >= quads.Length)
                {
                    continue;
                }

                float half = element.Size * 0.5f;
                quads[count++] = new FlareQuad
                {
                    Center = source.Uv + (new Vector2(0.5f) - source.Uv) * element.Position,
                    HalfSize = new Vector2(half / aspect, element.Shape == LensFlareShape.Streak ? half * StreakThickness : half),
                    Color = new Vector4(color, s),
                    Shape = new Vector4((float)element.Shape, element.Sides >= 3 ? element.Sides : 6, PolygonTurn, 0f),
                };
            }
        }

        return count;
    }

    /// <summary>
    /// How much a light at <paramref name="uv"/> flares by where it is on screen: all of it from <see cref="EdgeFade"/> in
    /// from the edges, fading to none at them, and none off screen.
    /// </summary>
    public static float Fade(Vector2 uv)
    {
        float inside = MathF.Min(MathF.Min(uv.X, 1f - uv.X), MathF.Min(uv.Y, 1f - uv.Y));
        float t = Math.Clamp(inside / EdgeFade, 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    // Adds the sun the sky draws, when it isn't the moon. It is as far as the sky, so only the sky leaves it seen, and it
    // shines through the fog of SunDistance.
    private static int AddSun(in FrameView view, Vector4 fog, Matrix4x4 viewProjection, (float Height, float Least) screen, Span<FlareSource> sources)
    {
        if (view.Lights.IsEmpty)
        {
            return 0;
        }

        ref readonly var sun = ref view.Lights[SkyModel.SunIndex(view.Lights, view.Environment.Sky.Moon)];
        if (SkyModel.IsMoon(sun.Direction, view.Environment.Sky.Moon) || sun.Direction == Vector3.Zero)
        {
            return 0;
        }

        var direction = Vector3.Normalize(sun.Direction);
        var clip = Vector4.Transform(new Vector4(direction, 0f), viewProjection);
        float radius = MathF.Tan(view.Shadows.LightAngle * 0.5f) * screen.Height;
        float through = Fog.Transmittance(fog.X, fog.Y, fog.Z, view.CameraPosition, view.CameraPosition + direction * SunDistance);
        return Add(sources, 0, clip, (0f, radius, sun.Radiance * through), screen.Least);
    }

    // Adds a light at clip, when it's in front, on screen and has any colour left there. The visibility pass looks at
    // least leastRadius round it.
    private static int Add(Span<FlareSource> sources, int count, Vector4 clip, (float Depth, float Radius, Vector3 Color) light, float leastRadius)
    {
        if (!(clip.W > 0f) || count >= sources.Length)
        {
            return count;
        }

        var uv = new Vector2(clip.X, clip.Y) / clip.W * 0.5f + new Vector2(0.5f);
        var faded = light.Color * Fade(uv);
        if (!(MathF.Max(faded.X, MathF.Max(faded.Y, faded.Z)) > 0f))
        {
            return count;
        }

        float radius = MathF.Min(MathF.Max(light.Radius, leastRadius), MostTapRadius);
        sources[count] = new FlareSource(uv, light.Depth, radius, faded);
        return count + 1;
    }
}
