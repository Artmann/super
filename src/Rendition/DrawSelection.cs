using System.Numerics;

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

    /// <summary>
    /// Takes the planes from clip space, where what's visible has |x| and |y| and z no larger than w.
    /// With <paramref name="far"/>, z must also be at least 0, for a view with a far plane, such as a
    /// light's into a shadow tile; the camera's reaches forever and has none.
    /// </summary>
    public DetailView(in Matrix4x4 clip, Vector3 eye, float pixelsAtOneUnit, float pixelsPerUnit, float maxError, bool far = false)
    {
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

    /// <summary>How many pixels a simpler model may stray by.</summary>
    public float MaxError { get; }

    public bool CanSee(Vector3 center, float radius)
    {
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

/// <summary>Picks what a view needs from a list of draws, keeping their order.</summary>
internal static class DrawSelection
{
    private const float NearestDistance = 0.1f;

    /// <summary>Writes the draws to keep into <paramref name="selected"/> and returns how many there are.</summary>
    public static int Select(in DetailView view, ReadOnlySpan<DrawItem> draws, List<PartDetail> details, Span<DrawItem> selected)
    {
        int count = 0;
        foreach (ref readonly var draw in draws)
        {
            if (draw.Detail.Index == 0)
            {
                selected[count++] = draw;
                continue;
            }

            var detail = details[draw.Detail.Index];
            float scale = Scale(draw.Transform);
            var (center, radius) = Bounds(draw, detail, scale);
            if (!view.CanSee(center, radius))
            {
                continue;
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
    /// bounds into <paramref name="bounds"/>, and returns how many there are.
    /// </summary>
    public static int Bounds(ReadOnlySpan<DrawItem> draws, List<PartDetail> details, Span<Vector4> bounds)
    {
        int count = 0;
        foreach (ref readonly var draw in draws)
        {
            if (draw.Detail.Index == 0)
            {
                continue;
            }

            var (center, radius) = Bounds(draw, details[draw.Detail.Index], Scale(draw.Transform));
            bounds[count++] = new Vector4(center, radius);
        }

        return count;
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
