using System.Numerics;
using Rendition.Passes;

namespace Rendition;

/// <summary>Where a list of draws will be seen from, which decides what can be left out or simplified.</summary>
internal readonly struct DetailView
{
    private const int PlaneCount = 6;

    private readonly Plane _left;
    private readonly Plane _right;
    private readonly Plane _top;
    private readonly Plane _bottom;
    private readonly Plane _near;
    private readonly Plane _far;
    private readonly bool _cull;

    /// <summary>
    /// Takes the planes from clip space, where what's visible has |x| and |y| and z no larger than w.
    /// With <paramref name="far"/>, z must also be at least 0, for a view with a far plane, such as a
    /// light's into a shadow tile; the camera's reaches forever and has none. Without <paramref name="cull"/>, the view
    /// sees everything, for <see cref="RenderEffects.Culling"/> off.
    /// </summary>
    public DetailView(
        in Matrix4x4 clip, Vector3 eye, float pixelsAtOneUnit, float pixelsPerUnit, float maxError, bool far = false, bool cull = true)
    {
        _cull = cull;
        var x = new Vector4(clip.M11, clip.M21, clip.M31, clip.M41);
        var y = new Vector4(clip.M12, clip.M22, clip.M32, clip.M42);
        var z = new Vector4(clip.M13, clip.M23, clip.M33, clip.M43);
        var w = new Vector4(clip.M14, clip.M24, clip.M34, clip.M44);
        _left = Normalized(w + x);
        _right = Normalized(w - x);
        _top = Normalized(w + y);
        _bottom = Normalized(w - y);
        _near = Normalized(w - z);
        _far = far ? Normalized(z) : new Plane(0f, 0f, 0f, 1f);
        Eye = eye;
        PixelsAtOneUnit = pixelsAtOneUnit;
        PixelsPerUnit = pixelsPerUnit;
        MaxError = maxError;
    }

    public Vector3 Eye { get; }

    /// <summary>Pixels per unit one unit from <see cref="Eye"/>, for a perspective view.</summary>
    public float PixelsAtOneUnit { get; }

    /// <summary>Pixels per unit at any distance, for a parallel view.</summary>
    public float PixelsPerUnit { get; }

    /// <summary>How many pixels a simpler model may stray by. 0 keeps every model at full detail.</summary>
    public float MaxError { get; }

    public bool CanSee(Vector3 center, float radius)
    {
        if (!_cull)
        {
            return true;
        }

        ReadOnlySpan<Plane> planes = [_left, _right, _top, _bottom, _near, _far];
        for (int i = 0; i < PlaneCount; i++)
        {
            if (Plane.DotCoordinate(planes[i], center) < -radius)
            {
                return false;
            }
        }

        return true;
    }

    private static Plane Normalized(Vector4 plane) => Plane.Normalize(new Plane(plane));
}

/// <summary>
/// How far the frame's wind can move a draw's vertices, in world metres, so a draw's bounds can grow to hold its sway.
/// <paramref name="strength"/> is the wind's, 0 in a calm or with the switch off, and <paramref name="swayOf"/> gives
/// each material's share of the lean.
/// </summary>
internal readonly struct WindReach(float strength, Func<MaterialHandle, float>? swayOf)
{
    public float Strength { get; } = strength;

    /// <summary>
    /// The farthest a vertex of the material can move: the full lean of a vertex 1 m or more up, at a gust's peak,
    /// plus the flutter. Nothing for a material that stands still.
    /// </summary>
    public float Pad(MaterialHandle material)
    {
        if (!(Strength > 0f) || swayOf is null)
        {
            return 0f;
        }

        float sway = swayOf(material);
        return sway > 0f ? Strength * sway + Wind.Flutter : 0f;
    }
}

/// <summary>Picks what a view needs from a list of draws, keeping their order.</summary>
internal static class DrawSelection
{
    private const float NearestDistance = 0.1f;

    /// <summary>
    /// Writes the draws to keep into <paramref name="selected"/> and returns how many there are. A draw that sways in
    /// <paramref name="wind"/> is kept while its sway could still bring it into view. When <paramref name="sources"/> is
    /// given, each kept draw's index in <paramref name="draws"/> is written into it at the same place.
    /// </summary>
    public static int Select(
        in DetailView view, ReadOnlySpan<DrawItem> draws, List<PartDetail> details, Span<DrawItem> selected, WindReach wind = default,
        Span<int> sources = default)
    {
        bool keepSources = !sources.IsEmpty;
        int count = 0;
        for (int index = 0; index < draws.Length; index++)
        {
            ref readonly var draw = ref draws[index];
            if (draw.Detail.Index == 0)
            {
                if (keepSources)
                {
                    sources[count] = index;
                }

                selected[count++] = draw;
                continue;
            }

            var detail = details[draw.Detail.Index];
            float scale = Scale(draw.Transform);
            var (center, radius) = Bounds(draw, detail, scale);

            // The sway is in world metres, so it's added after the scale. Only the test for being seen counts it, so
            // the wind doesn't change which simpler version is picked.
            if (!view.CanSee(center, radius + wind.Pad(draw.Material)))
            {
                continue;
            }

            if (keepSources)
            {
                sources[count] = index;
            }

            ref var item = ref selected[count++];
            item = draw;
            float distance = MathF.Max(Vector3.Distance(view.Eye, center) - radius, NearestDistance);
            float pixels = scale * (view.PixelsPerUnit + view.PixelsAtOneUnit / distance);
            var levels = detail.Levels;
            for (int i = levels.Length - 1; i >= 0; i--)
            {
                if (levels[i].Error * pixels <= view.MaxError)
                {
                    item.FirstIndex = levels[i].FirstIndex;
                    item.IndexCount = levels[i].IndexCount;
                    break;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Writes the world-space ball, x, y and z with the radius in w, round each draw that has
    /// bounds into <paramref name="bounds"/>, and returns how many there are. A ball holds all the draw's sway in
    /// <paramref name="wind"/>.
    /// </summary>
    public static int Bounds(ReadOnlySpan<DrawItem> draws, List<PartDetail> details, Span<Vector4> bounds, WindReach wind = default)
    {
        int count = 0;
        foreach (ref readonly var draw in draws)
        {
            if (draw.Detail.Index == 0)
            {
                continue;
            }

            var (center, radius) = Bounds(draw, details[draw.Detail.Index], Scale(draw.Transform));
            bounds[count++] = new Vector4(center, radius + wind.Pad(draw.Material));
        }

        return count;
    }

    /// <summary>
    /// The world-space ball round one draw, or false when the draw has no bounds, as a draw made
    /// without a part's details doesn't.
    /// </summary>
    public static bool TryBounds(in DrawItem draw, List<PartDetail> details, out Vector3 center, out float radius)
    {
        if (draw.Detail.Index == 0 || draw.Detail.Index >= details.Count)
        {
            center = default;
            radius = 0f;
            return false;
        }

        (center, radius) = Bounds(draw, details[draw.Detail.Index], Scale(draw.Transform));
        return true;
    }

    private static (Vector3 Center, float Radius) Bounds(in DrawItem draw, PartDetail detail, float scale) =>
        (Vector3.Transform(detail.Center, draw.Transform), (detail.Radius + MathF.Abs(draw.Swim.Amplitude)) * scale);

    /// <summary>How much a transform stretches its longest axis.</summary>
    private static float Scale(in Matrix4x4 transform) => MathF.Sqrt(MathF.Max(
        new Vector3(transform.M11, transform.M12, transform.M13).LengthSquared(),
        MathF.Max(
            new Vector3(transform.M21, transform.M22, transform.M23).LengthSquared(),
            new Vector3(transform.M31, transform.M32, transform.M33).LengthSquared())));
}
