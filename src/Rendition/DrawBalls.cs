using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>
/// What every view of a frame needs to know of each draw before it looks at the draw itself, measured once and kept
/// side by side: the ball round it, how much its transform stretches it, and how much of it the frame's fades keep.
/// None of it depends on the view, and a frame has many: the camera, the sun's cascades and every light's shadow
/// tiles. So a view reads 16 bytes for a draw it can't see, and not the draw.
/// </summary>
internal sealed class DrawBalls
{
    private Vector4[] _balls = [];
    private Vector4[] _casters = [];
    private float[] _radii = [];
    private float[] _scales = [];
    private float[] _keeps = [];
    private int _count;
    private int _casterCount;
    private List<PartDetail> _details = [];

    /// <summary>
    /// Each draw's middle in the world, with its radius in w, grown by as far as the wind can sway it. A draw with no
    /// bounds has its origin and an endless radius, so every view sees it.
    /// </summary>
    public ReadOnlySpan<Vector4> Balls => _balls.AsSpan(0, _count);

    /// <summary>The balls of the draws that have bounds, which are the ones the sun's shadows are fitted to.</summary>
    public ReadOnlySpan<Vector4> Casters => _casters.AsSpan(0, _casterCount);

    /// <summary>Each draw's radius without the wind's sway, which decides how simple a version a view picks.</summary>
    public ReadOnlySpan<float> Radii => _radii.AsSpan(0, _count);

    /// <summary>How much each draw's transform stretches its longest axis.</summary>
    public ReadOnlySpan<float> Scales => _scales.AsSpan(0, _count);

    /// <summary>How much of each draw the frame's fades keep, from 0 to 1.</summary>
    public ReadOnlySpan<float> Keeps => _keeps.AsSpan(0, _count);

    /// <summary>Whether a draw near swapping to a finer version is drawn as both, cross-fading.</summary>
    public bool Smooth { get; private set; }

    /// <summary>The simpler versions of the draw's part.</summary>
    public DetailLevel[] LevelsOf(in DrawItem draw) => _details[draw.Detail.Index].Levels;

    /// <summary>
    /// Measures a frame's draws. A draw's ball holds all its sway in <paramref name="wind"/>, and with
    /// <paramref name="fades"/> a draw fades out with its distance from the camera.
    /// </summary>
    public void Measure(ReadOnlySpan<DrawItem> draws, List<PartDetail> details, WindReach wind = default, FadeRules? fades = null)
    {
        MakeRoom(draws.Length);
        _details = details;
        _count = draws.Length;
        Smooth = fades is { Smooth: true };
        var parts = CollectionsMarshal.AsSpan(details);
        int casters = 0;
        for (int i = 0; i < draws.Length; i++)
        {
            ref readonly var draw = ref draws[i];
            if (draw.Detail.Index == 0)
            {
                var origin = draw.Transform.Translation;
                _balls[i] = new Vector4(origin, float.PositiveInfinity);
                _radii[i] = float.PositiveInfinity;
                _scales[i] = 0f;
                _keeps[i] = fades?.Keep(draw, origin) ?? 1f;
                continue;
            }

            ref readonly var part = ref parts[draw.Detail.Index];
            float scale = Scale(draw.Transform);
            var center = Vector3.Transform(part.Center, draw.Transform);
            float radius = (part.Radius + MathF.Abs(draw.Swim.Amplitude)) * scale;

            // The sway is in world metres, so it's added after the scale. Only the test for being seen counts it, so
            // the wind doesn't change which simpler version is picked.
            var ball = new Vector4(center, radius + wind.Pad(draw.Material));
            _balls[i] = ball;
            _casters[casters++] = ball;
            _radii[i] = radius;
            _scales[i] = scale;
            _keeps[i] = fades?.Keep(draw, center) ?? 1f;
        }

        _casterCount = casters;
    }

    private void MakeRoom(int count)
    {
        if (_balls.Length >= count)
        {
            return;
        }

        int room = Math.Max(count, _balls.Length * 2);
        _balls = new Vector4[room];
        _casters = new Vector4[room];
        _radii = new float[room];
        _scales = new float[room];
        _keeps = new float[room];
    }

    /// <summary>How much a transform stretches its longest axis.</summary>
    public static float Scale(in Matrix4x4 transform) => MathF.Sqrt(MathF.Max(
        new Vector3(transform.M11, transform.M12, transform.M13).LengthSquared(),
        MathF.Max(
            new Vector3(transform.M21, transform.M22, transform.M23).LengthSquared(),
            new Vector3(transform.M31, transform.M32, transform.M33).LengthSquared())));
}
