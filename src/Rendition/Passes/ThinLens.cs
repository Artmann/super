using System.Globalization;

namespace Rendition.Passes;

/// <summary>
/// How blurred a point is through a simple camera lens. A point at the focus distance lands on the
/// sensor as a point; anything nearer or further lands as a small disc, the circle of confusion,
/// which grows with the lens's focal length and with the aperture opened wider (a smaller f-number).
/// </summary>
public static class ThinLens
{
    /// <summary>A full-frame camera's sensor, 36 mm across.</summary>
    public const float FullFrameMm = 36f;

    /// <summary>The focal length used when <see cref="PostSettings.FocalLength"/> is left at 0: a 35 mm lens.</summary>
    public const float DefaultFocalLengthMm = 35f;

    /// <summary>
    /// How wide the disc is on the sensor, in metres, for a point <paramref name="depth"/> metres in
    /// front of the lens: (f/N) · |S2 − S1| / S2 · f / (S1 − f). An infinite depth gives the disc for a
    /// point at the horizon. An <paramref name="aperture"/> of 0 means no blur at all, at every depth.
    /// </summary>
    /// <param name="depth">How far the point is in front of the lens, S2, in metres.</param>
    /// <param name="focusDistance">Where the lens is focused, S1, in metres. It must be beyond the focal length.</param>
    /// <param name="aperture">The f-number, N, or 0 for none.</param>
    /// <param name="focalLengthMm">The lens's focal length, f, in millimetres.</param>
    /// <param name="sensorMm">The sensor's width in millimetres. The disc's size on the sensor doesn't depend on it.</param>
    public static float CircleOfConfusion(float depth, float focusDistance, float aperture, float focalLengthMm, float sensorMm = FullFrameMm)
    {
        Check(focusDistance, aperture, focalLengthMm, nameof(focusDistance));
        if (!(float.IsFinite(sensorMm) && sensorMm > 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sensorMm),
                string.Create(CultureInfo.InvariantCulture, $"The sensor must be wider than 0 mm, but it is {sensorMm} mm."));
        }

        if (aperture == 0f)
        {
            return 0f;
        }

        float focal = focalLengthMm / 1000f;
        float away = float.IsPositiveInfinity(depth) ? 1f : MathF.Abs(depth - focusDistance) / depth;
        return focal / aperture * away * focal / (focusDistance - focal);
    }

    /// <summary>A circle of confusion in metres on the sensor as pixels across an image <paramref name="imageWidth"/> pixels wide.</summary>
    public static float InPixels(float circle, int imageWidth, float sensorMm = FullFrameMm) =>
        circle / (sensorMm / 1000f) * imageWidth;

    /// <summary>
    /// Throws unless the lens settings make sense: an aperture of 0 (none) or above, a focal length above
    /// 0, and, while the aperture is open, a focus distance beyond the focal length. A negative or broken
    /// focus distance is refused even with no aperture, since it's a mistake whatever else is set.
    /// </summary>
    public static void Check(float focusDistance, float aperture, float focalLengthMm, string paramName)
    {
        if (IsValid(focusDistance, aperture, focalLengthMm))
        {
            return;
        }

        if (!(float.IsFinite(focusDistance) && focusDistance >= 0f))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                string.Create(CultureInfo.InvariantCulture, $"The focus distance must be 0 or more metres, but it is {focusDistance}."));
        }

        if (!(float.IsFinite(aperture) && aperture >= 0f))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                string.Create(CultureInfo.InvariantCulture, $"The aperture must be an f-number such as 2.8, or 0 for no depth of field, but it is {aperture}."));
        }

        if (!(float.IsFinite(focalLengthMm) && focalLengthMm > 0f))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                string.Create(CultureInfo.InvariantCulture, $"The focal length must be above 0 mm, but it is {focalLengthMm}."));
        }

        if (aperture > 0f && focusDistance <= focalLengthMm / 1000f)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The focus distance must be further than the focal length of {focalLengthMm} mm, but it is {focusDistance} m."));
        }
    }

    /// <summary>
    /// Whether <see cref="Check"/> would accept these lens settings, without throwing, so a frame can
    /// ask every time at no cost.
    /// </summary>
    public static bool IsValid(float focusDistance, float aperture, float focalLengthMm) =>
        float.IsFinite(focusDistance) && focusDistance >= 0f
        && float.IsFinite(aperture) && aperture >= 0f
        && float.IsFinite(focalLengthMm) && focalLengthMm > 0f
        && !(aperture > 0f && focusDistance <= focalLengthMm / 1000f);

    /// <summary>The focal length in millimetres for a <see cref="PostSettings.FocalLength"/>, where 0 means the default.</summary>
    public static float FocalLength(float requested) => requested == 0f ? DefaultFocalLengthMm : requested;
}
