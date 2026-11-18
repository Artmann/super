using System.Numerics;

namespace Rendition;

/// <summary>A glowing rectangle in the environment, such as a studio softbox.</summary>
public struct EnvironmentPanel
{
    /// <summary>Points from the centre of the scene towards the panel.</summary>
    public Vector3 Direction;

    /// <summary>Half the panel's size at a distance of one from the scene's centre.</summary>
    public float HalfWidth;
    public float HalfHeight;
    public Vector3 Radiance;
}

/// <summary>The surroundings that surfaces reflect: three bands of colour plus up to eight panels.</summary>
public ref struct EnvironmentDescription
{
    public Vector3 Ceiling;
    public Vector3 Horizon;
    public Vector3 Floor;
    public ReadOnlySpan<EnvironmentPanel> Panels;
}

/// <summary>
/// A physical clear sky, the light of the sun scattered by the air (after Hillaire 2020), in place
/// of the environment's ceiling and horizon. Its sun is the light that casts the shadows, or else
/// the first, and it shows that light as a disc <see cref="ShadowSettings.LightAngle"/> across. The
/// sky follows the sun all day: blue at noon, orange and red at sunset, deep blue after dusk. The
/// sun's light itself turns warm and dim as it sinks, and far surfaces fade into the air (see
/// <see cref="RenderEffects.AerialPerspective"/>). The environment's floor still shows below the
/// horizon, lit by the sun and sky. Changing it, or that sun, while it is on redraws the
/// environment's lighting in the next frame, without waiting, so it can change every frame. See
/// "The sky" in docs/rendering.md.
/// </summary>
public struct AnalyticSky
{
    /// <summary>
    /// How hazy the air is, from 1.7 (very clear) to 10 (milky). 0 keeps the environment's bands. Rendition
    /// doesn't refuse other values, but draws anything above 0 held to 1.7 to 10. Super's SkySettings
    /// refuses them.
    /// </summary>
    public float Turbidity;

    /// <summary>
    /// Towards the moon, which the sky shows as a disc, lit on the side facing the sun; zero for no moon. Its light is
    /// a light of the game's own, aimed this way: the sky never follows that light as its sun, even when it casts the
    /// shadows. See "Night" in docs/rendering.md.
    /// </summary>
    public Vector3 Moon;

    /// <summary>Leaves the stars out of the night sky, as for an overcast night.</summary>
    public bool HideStars;

    /// <summary>How far the stars have turned about the zenith, in radians, for a night sky that turns with time.</summary>
    public float StarRotation;

    /// <summary>
    /// A photographed sky, from <see cref="Renderer.CreateSkyImage"/>, in place of the bands and the physical sky. It
    /// shows behind the scene, lights it and shows in reflections, with the sun the cooker took out of it drawn back
    /// as a disc. It wins over <see cref="Turbidity"/>, <see cref="Moon"/> and the stars, which it leaves out. Its sun
    /// is not a light: aim the game's own at it. See "A photographed sky" in docs/rendering.md.
    /// </summary>
    public SkyImageHandle? Image;

    /// <summary>How far <see cref="Image"/> is turned about the up axis, in radians, counterclockwise seen from above.</summary>
    public float Rotation;

    /// <summary>How much brighter <see cref="Image"/> is drawn, in stops: 1 doubles it, -1 halves it.</summary>
    public float Exposure;
}

/// <summary>
/// A spot where the real scene is photographed in all directions, so nearby surfaces
/// reflect it. The box is the room around it: reflections land on its walls and floor
/// instead of infinitely far away, which keeps them lined up with the scene.
/// </summary>
public struct ReflectionProbe
{
    public Vector3 Position;
    public Vector3 BoxMin;
    public Vector3 BoxMax;

    /// <summary>The probe fades out over this distance inside its box.</summary>
    public float BlendDistance;

    /// <summary>
    /// Where boxes overlap, the probe with the higher priority is blended first and the next fills in
    /// only what it leaves, towards its sides. Between equal priorities the smaller box goes first.
    /// </summary>
    public float Priority;
}
