using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;

namespace Super;

/// <summary>
/// The lights of a frame, reached through <see cref="View.Lights"/>: suns, point and spot lights, and how far and how
/// softly the shadows reach. Like the rest of a view, it holds only what this frame says.
/// </summary>
public sealed class ViewLights
{
    private readonly List<Sun> _suns = [];
    private readonly List<LocalLight> _lights = [];

    internal ViewLights()
    {
    }

    /// <summary>How wide the sun looks, in radians, unless <see cref="Shadows"/> says otherwise.</summary>
    public const float DefaultSunSize = 0.12f;

    /// <summary>The widest a sun can look, in radians. Wider would blur shadows away.</summary>
    public const float MaxSunSize = 0.5f;

    internal ReadOnlySpan<Sun> Suns => CollectionsMarshal.AsSpan(_suns);

    /// <summary>Every point and spot light so far this frame.</summary>
    public ReadOnlySpan<LocalLight> DrawnLights => CollectionsMarshal.AsSpan(_lights);

    internal float ShadowRadius { get; private set; }
    internal int ShadowCascades { get; private set; }
    internal float SunSize { get; private set; }
    internal float ShadowContact { get; private set; } = ShadowSettings.DefaultContactLength;

    /// <summary>
    /// Adds a sun. The direction points from the ground towards it, and needn't be one
    /// unit long. Colors above 1 are brighter. One sun casts the shadows: the one added
    /// with <paramref name="shadows"/> set, or else the first. Marking two throws. The mode says how
    /// a bake treats it; see "Light modes" in docs/rendering.md.
    /// </summary>
    public void Sun(Vector3 direction, Vector3 color, bool shadows = false, LightMode mode = LightMode.Mixed)
    {
        LightChecks.Mode(mode, nameof(mode));

        if (shadows)
        {
            foreach (var sun in _suns)
            {
                if (sun.CastsShadows)
                {
                    throw new InvalidOperationException(
                        "Only one sun can cast shadows, and another already does this frame. Pass shadows: true to one sun only.");
                }
            }
        }

        _suns.Add(new Sun(Vector3.Normalize(direction), color, shadows, mode));
    }

    /// <summary>
    /// Adds a light that shines every way from a point, such as a candle or a torch. The key names it across frames,
    /// so its shadow stays as sharp as it moves; use a stable ID. A frame can have up to
    /// <see cref="LocalLight.MaxPerFrame"/> point and spot lights. See "Point and spot lights" in docs/rendering.md.
    /// </summary>
    public void Point(int key, in PointLight light)
    {
        var local = light.ToLocal(key);
        LightChecks.Local(local, _lights.Count, nameof(light));
        _lights.Add(local);
    }

    /// <summary>
    /// Adds a light that shines one way in a cone, such as a lantern's beam. Otherwise it's like
    /// <see cref="Point"/>, though it costs a sixth as much shadow.
    /// </summary>
    public void Spot(int key, in SpotLight light)
    {
        var local = light.ToLocal(key);
        LightChecks.Local(local, _lights.Count, nameof(light));
        LightChecks.Cone(light.Direction, light.InnerAngle, light.OuterAngle, nameof(light));
        _lights.Add(local);
    }

    /// <summary>
    /// Sets how far around the camera's target the sharpest shadows reach, in metres,
    /// and how many maps to draw, 1 to 4. Each map covers three times the radius of the
    /// one before, so a wide world keeps sharp shadows near the camera.
    /// <paramref name="sunSize"/> is how wide the sun looks, in radians, which sets how fast
    /// its shadows blur with distance from what casts them: 0 is hard, the real sun is about
    /// 0.01, and the default of 0.12 is a soft, hazy sun.
    /// <paramref name="contact"/> is how far, in metres, each pixel looks towards the sun on screen
    /// for something touching it, which darkens the last centimetres where things meet the ground
    /// that the maps are too coarse to see. It works only inside the first map's radius; 0 turns it off.
    /// </summary>
    public void Shadows(float radius, int cascades = 1, float sunSize = DefaultSunSize, float contact = ShadowSettings.DefaultContactLength)
    {
        if (!(radius > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The shadow radius must be more than zero.");
        }

        if (cascades is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(cascades), cascades, "There can be 1 to 4 shadow cascades.");
        }

        if (!(sunSize is >= 0f and <= MaxSunSize))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sunSize), sunSize, $"The sun's size must be from 0 to {MaxSunSize.ToString(CultureInfo.InvariantCulture)} radians.");
        }

        if (!(float.IsFinite(contact) && contact >= 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(contact), contact, "The contact shadows' length must be 0 or more metres, and 0 turns them off.");
        }

        ShadowRadius = radius;
        ShadowCascades = cascades;
        SunSize = sunSize;
        ShadowContact = contact;
    }

    /// <summary>Forgets the last frame's lights and puts the shadows back as a frame starts.</summary>
    internal void Clear()
    {
        _suns.Clear();
        _lights.Clear();
        ShadowRadius = 8f;
        ShadowCascades = 1;
        SunSize = DefaultSunSize;
        ShadowContact = ShadowSettings.DefaultContactLength;
    }
}
