using Rendition.Passes;
using static Rendition.SettingsMix;

namespace Rendition;

/// <summary>The camera's lens, for depth of field, in <see cref="PostSettings.Lens"/>. Left at zero, everything is sharp.</summary>
public struct LensSettings
{
    /// <summary>
    /// Where the camera is focused, in metres in front of it. Only things at this distance are sharp once
    /// <see cref="Aperture"/> opens the lens. It must be further than <see cref="FocalLength"/>. See docs/rendering.md.
    /// </summary>
    public float FocusDistance;

    /// <summary>
    /// How wide the lens is open, as an f-number: 2.8 blurs strongly, 8 gently, 16 hardly at all.
    /// 0, the default, is no depth of field: everything is sharp, as through a pinhole.
    /// </summary>
    public float Aperture;

    /// <summary>
    /// The lens's focal length in millimetres, on a 36 mm wide sensor. Longer lenses blur more at the same f-number. 0
    /// means the default of 35. It changes only the blur, not the field of view, which the camera sets.
    /// </summary>
    public float FocalLength;

    /// <summary>
    /// The lens <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>. A lens can't be
    /// part open, since an f-number halfway from none would be the widest lens of all, so the focus and the aperture
    /// move only between two open lenses and otherwise change at the halfway point.
    /// </summary>
    public static LensSettings Lerp(in LensSettings from, in LensSettings to, float t)
    {
        var near = t < 0.5f ? from : to;
        bool open = from.Aperture > 0f && to.Aperture > 0f;
        return new LensSettings
        {
            FocusDistance = open ? Mix(from.FocusDistance, to.FocusDistance, t) : near.FocusDistance,
            Aperture = open ? Mix(from.Aperture, to.Aperture, t) : near.Aperture,
            FocalLength = Mix(ThinLens.FocalLength(from.FocalLength), ThinLens.FocalLength(to.FocalLength), t),
        };
    }
}
