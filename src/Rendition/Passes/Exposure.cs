using System.Globalization;
using System.Numerics;

namespace Rendition.Passes;

/// <summary>
/// Auto exposure's sums, done the way exposure_histogram.comp and exposure_adapt.comp do them so they
/// can be tested here: which bin a pixel falls in, the brightness the camera aims for from the bins,
/// and how far the exposure moves towards it in a frame.
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
    /// <paramref name="target"/> by at most <paramref name="speedStopsPerSecond"/> times dt, without
    /// passing it. A current that is not a number snaps straight to the target, which is how the first
    /// frame starts metered instead of from black; a target that is not a number holds still.
    /// </summary>
    public static float Adapt(float current, float target, float speedStopsPerSecond, float dt)
    {
        if (!(float.IsFinite(speedStopsPerSecond) && speedStopsPerSecond > 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(speedStopsPerSecond),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The adaptation speed must be above 0 stops a second, but it is {speedStopsPerSecond}."));
        }

        if (float.IsNaN(target))
        {
            return current;
        }

        if (float.IsNaN(current))
        {
            return target;
        }

        float step = speedStopsPerSecond * dt;
        return current + Math.Clamp(target - current, -step, step);
    }

    /// <summary>The adaptation speed for a <see cref="PostSettings.AdaptationSpeed"/>, where 0 means the default.</summary>
    public static float Speed(float requested) => requested == 0f ? DefaultSpeed : requested;

    /// <summary>
    /// The switches the shaders see: <paramref name="off"/>, plus <see cref="RenderEffects.AutoExposure"/>
    /// when the view didn't ask for it, so the frame is metered only when the scene asks and the
    /// switch allows. Otherwise the fixed exposure is used exactly as before.
    /// </summary>
    public static RenderEffects ShaderOff(in PostSettings post, RenderEffects off) =>
        post.AutoExposure ? off : off | RenderEffects.AutoExposure;
}

/// <summary>What exposure_adapt.comp is told for a frame: how far it may move, or to snap.</summary>
internal readonly record struct ExposureStep(float MaxStep, bool Snap);

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

    /// <summary>The step for this frame, or null when the frame isn't metered.</summary>
    public ExposureStep? Next(bool metered, float time, float speed)
    {
        if (!metered)
        {
            _running = false;
            return null;
        }

        bool snap = !_running || time < _lastTime;
        float dt = snap ? 0f : time - _lastTime;
        _running = true;
        _lastTime = time;
        return new ExposureStep(speed * dt, snap);
    }
}
