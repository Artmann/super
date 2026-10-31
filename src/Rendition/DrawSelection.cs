using System.Numerics;

namespace Rendition;

/// <summary>Where a list of draws will be seen from, which decides what can be left out or simplified.</summary>
internal readonly struct DetailView
{
    private const int PlaneCount = 5;

    private readonly Plane _left;
    private readonly Plane _right;
    private readonly Plane _top;
    private readonly Plane _bottom;
    private readonly Plane _near;

    /// <summary>Takes the planes from clip space, where what's visible has |x| and |y| and z no larger than w.</summary>
    public DetailView(in Matrix4x4 clip, Vector3 eye, float pixelsAtOneUnit, float pixelsPerUnit, float maxError)
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
        ReadOnlySpan<Plane> planes = [_left, _right, _top, _bottom, _near];
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
            ref readonly var transform = ref draw.Transform;
            var center = Vector3.Transform(detail.Center, transform);
            float scale = MathF.Sqrt(MathF.Max(
                new Vector3(transform.M11, transform.M12, transform.M13).LengthSquared(),
                MathF.Max(
                    new Vector3(transform.M21, transform.M22, transform.M23).LengthSquared(),
                    new Vector3(transform.M31, transform.M32, transform.M33).LengthSquared())));
            float radius = (detail.Radius + MathF.Abs(draw.Swim.Amplitude)) * scale;
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
}
