using System.Numerics;
using System.Runtime.Intrinsics;
using Rendition.Passes;

namespace Rendition;

/// <summary>Where a list of draws will be seen from, which decides what can be left out or simplified.</summary>
internal readonly struct DetailView
{
    private const int Places = 8;

    // The six planes side by side, each of x, y, z and distance in a row of its own, so one ball is tried against
    // all of them at once. The last two of the eight places hold planes that see everything.
    private readonly Vector256<float> _xs;
    private readonly Vector256<float> _ys;
    private readonly Vector256<float> _zs;
    private readonly Vector256<float> _distances;
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
        var everything = new Plane(0f, 0f, 0f, 1f);
        ReadOnlySpan<Plane> planes =
        [
            Normalized(w + x), Normalized(w - x), Normalized(w + y), Normalized(w - y), Normalized(w - z),
            far ? Normalized(z) : everything, everything, everything,
        ];
        Span<float> rows = stackalloc float[4 * Places];
        for (int i = 0; i < Places; i++)
        {
            rows[i] = planes[i].Normal.X;
            rows[Places + i] = planes[i].Normal.Y;
            rows[2 * Places + i] = planes[i].Normal.Z;
            rows[3 * Places + i] = planes[i].D;
        }

        _xs = Vector256.Create<float>(rows[..Places]);
        _ys = Vector256.Create<float>(rows.Slice(Places, Places));
        _zs = Vector256.Create<float>(rows.Slice(2 * Places, Places));
        _distances = Vector256.Create<float>(rows.Slice(3 * Places, Places));
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

    /// <summary>Whether any of a ball, its middle in x, y and z and its radius in w, is in view.</summary>
    public bool CanSee(Vector4 ball)
    {
        if (!_cull)
        {
            return true;
        }

        // How far the ball's middle is in front of each plane. It is out of view when it is more than its radius
        // behind any of them.
        var ahead = _xs * ball.X + _ys * ball.Y + _zs * ball.Z + _distances;
        return !Vector256.LessThanAny(ahead, Vector256.Create(-ball.W));
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

/// <summary>
/// How draws fade in a frame: by distance from <paramref name="from"/>, the camera, in every view, so a shadow fades
/// with what casts it, with <paramref name="fadeOf"/> giving each material's fade. With <paramref name="smooth"/> off,
/// for <see cref="RenderEffects.Fades"/> off, a fade is a hard cut halfway through it and simpler versions swap at once.
/// </summary>
internal readonly struct FadeRules(Vector3 from, bool smooth, Func<MaterialHandle, DistanceFade?>? fadeOf = null)
{
    public Vector3 From { get; } = from;

    public bool Smooth { get; } = smooth;

    /// <summary>How much of a draw the fade keeps, from 0 to 1, given where its middle is.</summary>
    public float Keep(in DrawItem draw, Vector3 center)
    {
        var fade = draw.Fade ?? fadeOf?.Invoke(draw.Material);
        if (fade is not { } distances)
        {
            return 1f;
        }

        float distance = Vector3.Distance(From, center);
        if (!Smooth)
        {
            return distance < (distances.Start + distances.End) * 0.5f ? 1f : 0f;
        }

        return distances.Keep(distance);
    }
}

/// <summary>Picks what a view needs from a list of draws, keeping their order.</summary>
internal static class DrawSelection
{
    private const float NearestDistance = 0.1f;

    /// <summary>
    /// How close to swapping to a finer version, as a share of the error a view allows, a draw starts to dither into
    /// it, drawing both versions with complementary noise so the swap never pops.
    /// </summary>
    public const float CrossFade = 0.25f;

    /// <summary>
    /// Writes the draws to keep into <paramref name="selected"/> and returns how many there are, going by what
    /// <paramref name="measured"/> holds of each, which must be of <paramref name="draws"/>. A draw that sways in the
    /// wind is kept while its sway could still bring it into view. When <paramref name="sources"/> is given, each kept
    /// draw's index in <paramref name="draws"/> is written into it at the same place. Draws fade out with distance,
    /// and a draw near swapping to a finer version is written twice, once for each version, so
    /// <paramref name="selected"/> needs room for twice as many draws.
    /// </summary>
    public static int Select(
        in DetailView view, ReadOnlySpan<DrawItem> draws, DrawBalls measured, Span<DrawItem> selected, Span<int> sources = default)
    {
        var writer = new SelectionWriter(selected, sources);
        var balls = measured.Balls;
        var keeps = measured.Keeps;
        var radii = measured.Radii;
        var scales = measured.Scales;

        // A draw out of view is left after reading its ball alone, and most draws are out of most views.
        for (int index = 0; index < balls.Length; index++)
        {
            var ball = balls[index];
            float keep = keeps[index];
            if (!view.CanSee(ball) || keep <= 0f)
            {
                continue;
            }

            ref readonly var draw = ref draws[index];
            if (draw.Detail.Index == 0)
            {
                writer.Add(index, draw, Dither(keep), null);
                continue;
            }

            var center = new Vector3(ball.X, ball.Y, ball.Z);
            float distance = MathF.Max(Vector3.Distance(view.Eye, center) - radii[index], NearestDistance);
            float pixels = scales[index] * (view.PixelsPerUnit + view.PixelsAtOneUnit / distance);
            WriteLevels(ref writer, new LevelChoice(measured.LevelsOf(draw), pixels, view.MaxError, measured.Smooth), index, draw, keep);
        }

        return writer.Count;
    }

    /// <summary>
    /// Writes the version of a draw its size on screen calls for. Near the error a view allows, the chosen version is
    /// about to give way to the next finer one, or the full mesh, so both are written: the coarse one keeps a share of
    /// the pixels that shrinks to nothing as the swap comes, and the fine one keeps the rest.
    /// </summary>
    private static void WriteLevels(ref SelectionWriter writer, in LevelChoice choice, int index, in DrawItem draw, float keep)
    {
        int chosen = choice.Chosen();
        DetailLevel? coarseLevel = chosen >= 0 ? choice.Levels[chosen] : null;
        float coarse = coarseLevel is { } level ? choice.CoarseShare(level) : 1f;
        if (coarse < 1f)
        {
            DetailLevel? fineLevel = chosen > 0 ? choice.Levels[chosen - 1] : null;
            writer.Add(index, draw, coarse <= 0f ? Dither(keep) : -(1f - coarse) * keep, fineLevel);
        }

        if (coarse > 0f)
        {
            writer.Add(index, draw, coarse < 1f ? coarse * keep : Dither(keep), coarseLevel);
        }
    }

    /// <summary>A draw's simpler versions, with how many pixels a unit of their error covers and how many a view allows.</summary>
    private readonly record struct LevelChoice(DetailLevel[] Levels, float Pixels, float MaxError, bool Smooth)
    {
        /// <summary>The coarsest version within the error allowed, or -1 for the full mesh.</summary>
        public int Chosen()
        {
            for (int i = Levels.Length - 1; i >= 0; i--)
            {
                if (Levels[i].Error * Pixels <= MaxError)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The share of pixels the chosen version keeps in a cross-fade, 1 or more when it's drawn alone.</summary>
        public float CoarseShare(DetailLevel level)
        {
            if (!Smooth)
            {
                return 1f;
            }

            float closeness = level.Error * Pixels / MaxError;
            return MathF.Min((1f - closeness) / CrossFade, 1f);
        }
    }

    /// <summary>Writes kept draws, and where each came from when the caller asked.</summary>
    private ref struct SelectionWriter(Span<DrawItem> selected, Span<int> sources)
    {
        private readonly Span<DrawItem> _selected = selected;
        private readonly Span<int> _sources = sources;

        public int Count { get; private set; }

        /// <summary>Writes one copy of the draw from <paramref name="index"/>, with <paramref name="level"/>'s indices when it has one.</summary>
        public void Add(int index, in DrawItem draw, float dither, DetailLevel? level)
        {
            if (!_sources.IsEmpty)
            {
                _sources[Count] = index;
            }

            ref var item = ref _selected[Count++];
            item = draw;
            item.Dither = dither;
            if (level is { } at)
            {
                item.FirstIndex = at.FirstIndex;
                item.IndexCount = at.IndexCount;
            }
        }
    }

    // A draw kept whole is drawn as usual, with no noise.
    private static float Dither(float keep) => keep >= 1f ? 0f : keep;

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

        var detail = details[draw.Detail.Index];
        center = Vector3.Transform(detail.Center, draw.Transform);
        radius = (detail.Radius + MathF.Abs(draw.Swim.Amplitude)) * DrawBalls.Scale(draw.Transform);
        return true;
    }

    /// <summary>How many triangles the draws hold, for the triangle counts in profiles.</summary>
    public static long Triangles(ReadOnlySpan<DrawItem> draws)
    {
        long indices = 0;
        foreach (ref readonly var draw in draws)
        {
            indices += draw.IndexCount;
        }

        return indices / 3;
    }

    /// <summary>
    /// About how many pixels a draw's ball covers in a view: the disc it shows as. A draw with no bounds, or with
    /// the eye inside its ball, may cover any number, so it gives infinity.
    /// </summary>
    public static float PixelsCovered(in DetailView view, in DrawItem draw, List<PartDetail> details)
    {
        if (!TryBounds(draw, details, out var center, out float radius))
        {
            return float.PositiveInfinity;
        }

        float distance = Vector3.Distance(view.Eye, center);
        if (distance <= radius)
        {
            return float.PositiveInfinity;
        }

        float pixels = radius * (view.PixelsPerUnit + view.PixelsAtOneUnit / distance);
        return MathF.PI * pixels * pixels;
    }
}
