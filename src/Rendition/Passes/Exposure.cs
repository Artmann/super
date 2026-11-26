using System.Globalization;
using System.Numerics;

namespace Rendition.Passes;

/// <summary>
/// Auto exposure's sums, done the way exposure_histogram.comp and exposure_adapt.comp do them so they
/// can be tested here: which bin a pixel falls in and how much it counts, the brightness the camera aims
/// for from the bins, and how far the exposure moves towards it in a frame.
/// </summary>
internal static class Exposure
{
    /// <summary>How many bins the histogram has. Must match EXPOSURE_BINS in the shaders.</summary>
    public const int Bins = 64;
    /// <summary>The darkest the histogram sees, in EV: 1/256 of a white of 1.</summary>
    public const float MinEv = -8f;
    /// <summary>The brightest the histogram sees, in EV: 65536 times a white of 1, past the sun's disc.</summary>
    public const float MaxEv = 16f;
    /// <summary>The share of darkest pixels left out, so deep shadow doesn't brighten the picture.</summary>
    public const float LowCut = 0.4f;
    /// <summary>Where the brightest pixels start to be left out, so the sun and its glints don't darken it.</summary>
    public const float HighCut = 0.95f;
    /// <summary>The adaptation speed used when <see cref="PostSettings.AdaptationSpeed"/> is left at 0.</summary>
    public const float DefaultSpeed = 2f;
    /// <summary>The speed into the dark used when <see cref="MeteringSettings.DarkSpeed"/> is left at 0.</summary>
    public const float DefaultDarkSpeed = 1f;
    /// <summary>How many times a corner the very middle counts at a <see cref="MeteringSettings.Centre"/> of 1, less one.</summary>
    public const float CentreBoost = 15f;

    /// <summary>
    /// The bin of a pixel: its luminance's EV across <see cref="MinEv"/> to <see cref="MaxEv"/>. Anything
    /// darker, black, negative or not a number lands in the first bin, and anything brighter, however
    /// bright, in the last, so no pixel falls outside.
    /// </summary>
    public static uint Bin(Vector3 rgb)
    {
        float luminance = Vector3.Dot(rgb, new Vector3(0.2126f, 0.7152f, 0.0722f));
        if (!(luminance > 0f))
        {
            return 0;
        }

        float position = (MathF.Log2(luminance) - MinEv) * (Bins / (MaxEv - MinEv));
        return (uint)Math.Clamp(position, 0f, Bins - 1f);
    }

    /// <summary>
    /// How many times a pixel at <paramref name="uv"/> counts, a whole number so the GPU can add it up exactly:
    /// 1 at the corners and anywhere at a <paramref name="centre"/> of 0, rising towards the middle as one less
    /// the squared distance from it, with a corner at 1, to 1 + 15 times the centre weight.
    /// </summary>
    public static uint Weight(Vector2 uv, float centre)
    {
        var fromMiddle = uv - new Vector2(0.5f);
        float near = MathF.Max(0f, 1f - (Vector2.Dot(fromMiddle, fromMiddle) * 2f));
        return 1u + (uint)MathF.Round(centre * CentreBoost * near, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The EV the camera aims for: the mean of the bins' centres between the 40th and 95th percentile
    /// of pixels, each bin counted by how many of its pixels fall in that band. Not a number when the
    /// histogram is empty.
    /// </summary>
    public static float Target(ReadOnlySpan<uint> bins, float minEv, float maxEv)
    {
        float total = 0f;
        foreach (uint count in bins)
        {
            total += count;
        }

        if (total <= 0f)
        {
            return float.NaN;
        }

        float low = total * LowCut;
        float high = total * HighCut;
        float width = (maxEv - minEv) / bins.Length;
        float below = 0f;
        float weight = 0f;
        float sum = 0f;
        for (int i = 0; i < bins.Length; i++)
        {
            float start = below;
            float end = below + bins[i];
            below = end;
            float kept = MathF.Max(0f, MathF.Min(end, high) - MathF.Max(start, low));
            weight += kept;
            sum += kept * (minEv + (i + 0.5f) * width);
        }

        return weight > 0f ? sum / weight : float.NaN;
    }

    /// <summary>
    /// The exposure after <paramref name="dt"/> seconds: <paramref name="current"/> moved towards
    /// <paramref name="target"/> without passing it, by at most <paramref name="brighter"/> stops a second when
    /// the target is brighter and <paramref name="darker"/> when it is darker. A current that is not a number
    /// snaps straight to the target, which is how the first frame starts metered instead of from black; a
    /// target that is not a number holds still.
    /// </summary>
    public static float Adapt(float current, float target, float brighter, float darker, float dt)
    {
        if (!(float.IsFinite(brighter) && brighter > 0f && float.IsFinite(darker) && darker > 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(brighter),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The adaptation speeds must be above 0 stops a second, but they are {brighter} towards the light and {darker} towards the dark."));
        }

        if (float.IsNaN(target))
        {
            return current;
        }

        if (float.IsNaN(current))
        {
            return target;
        }

        return current + Math.Clamp(target - current, -darker * dt, brighter * dt);
    }

    /// <summary>
    /// The adaptation speed for a requested one: 0 means <paramref name="otherwise"/>, and so does anything that
    /// isn't a finite speed above 0. Super's View refuses those, but Rendition can be used alone, and a negative
    /// speed would reach exposure_adapt.comp as a negative step, which flips its clamp and throws the exposure the
    /// wrong way. What comes out is always one <see cref="Adapt"/> takes.
    /// </summary>
    public static float Speed(float requested, float otherwise = DefaultSpeed) =>
        float.IsFinite(requested) && requested > 0f ? requested : otherwise;

    /// <summary>
    /// How the meter is set for a frame from its settings. Rendition can be used alone, so a centre weight
    /// outside 0 to 1 is held to it, and a limit that is not a number, or a darkest past the brightest, is left out.
    /// </summary>
    public static ExposureAim Aim(in PostSettings post)
    {
        var metering = post.Metering;
        float darkest = metering.Darkest is { } low && float.IsFinite(low) ? low : -float.MaxValue;
        float brightest = metering.Brightest is { } high && float.IsFinite(high) ? high : float.MaxValue;
        if (darkest > brightest)
        {
            (darkest, brightest) = (-float.MaxValue, float.MaxValue);
        }

        float centre = float.IsNaN(metering.Centre) ? 0f : Math.Clamp(metering.Centre, 0f, 1f);
        return new ExposureAim(
            Speed(post.AdaptationSpeed), Speed(metering.DarkSpeed, DefaultDarkSpeed), centre, darkest, brightest);
    }

    /// <summary>
    /// The switches the shaders see: <paramref name="off"/>, plus <see cref="RenderEffects.AutoExposure"/>
    /// when the view didn't ask for it, so the frame is metered only when the scene asks and the
    /// switch allows. Otherwise the fixed exposure is used exactly as before.
    /// </summary>
    public static RenderEffects ShaderOff(in PostSettings post, RenderEffects off) =>
        post.AutoExposure ? off : off | RenderEffects.AutoExposure;
}

/// <summary>
/// How the meter is set: its speeds towards the light and the dark in stops a second, how much the middle counts,
/// and the darkest and brightest targets it follows, which are the float range when there is no limit.
/// </summary>
internal readonly record struct ExposureAim(float Brighter, float Darker, float Centre, float Darkest, float Brightest)
{
    /// <summary>A target held within the limits. One that is not a number stays so, and holds the exposure still.</summary>
    public float Limit(float target) => float.IsNaN(target) ? target : Math.Clamp(target, Darkest, Brightest);
}

/// <summary>
/// What the meter is told for a frame: how it is set, and the seconds since the last metered frame, or to snap.
/// The exposure may rise by <see cref="Rise"/> stops and fall by <see cref="Fall"/>.
/// </summary>
internal readonly record struct ExposureStep(ExposureAim Aim, float Seconds, bool Snap)
{
    public float Rise => Aim.Brighter * Seconds;

    public float Fall => Aim.Darker * Seconds;
}

/// <summary>
/// Keeps time for auto exposure on the CPU, so the GPU never has to be read back. The first metered
/// frame, the first after metering was off, and the first after time went backwards, as on a restart,
/// snap to the target. After that each frame may move speed times the time since the last, so frames
/// drawn again at the same moment, as a screenshot does while it settles, hold the exposure still.
/// </summary>
internal sealed class ExposureClock
{
    private bool _running;
    private float _lastTime;

    /// <summary>The step <see cref="Next"/> last gave, which the frame being recorded meters with.</summary>
    public ExposureStep? Step { get; private set; }

    /// <summary>The step for this frame, or null when the frame isn't metered. It is kept as <see cref="Step"/> too.</summary>
    public ExposureStep? Next(bool metered, float time, in ExposureAim aim)
    {
        if (!metered)
        {
            _running = false;
            Step = null;
            return null;
        }

        bool snap = !_running || time < _lastTime;
        float dt = snap ? 0f : time - _lastTime;
        _running = true;
        _lastTime = time;
        Step = new ExposureStep(aim, dt, snap);
        return Step;
    }
}
