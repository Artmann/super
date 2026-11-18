namespace Super;

/// <summary>The shadows of the sun and of the point and spot lights, and how sharp and far the sun's reach.</summary>
public sealed class GraphicsShadowing
{
    private const int MaxShadowCascades = 4;

    /// <summary>
    /// The sun's shadows. Off, nothing is drawn into the shadow maps, and sunlight is blocked only by nearby
    /// surfaces, as ambient occlusion sees them. Often the most expensive effect.
    /// </summary>
    public bool SunShadows { get; set; } = true;

    /// <summary>
    /// How many texels across each of the sun's shadow maps is: 1024, 2048 or 4096. Each step up
    /// gives sharper shadow edges for four times the memory and more drawing. A change while the
    /// game runs rebuilds the maps before the next frame.
    /// </summary>
    public int ShadowResolution
    {
        get;
        set => field = value is 1024 or 2048 or 4096
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Shadow maps can be 1024, 2048 or 4096 texels across.");
    } = 2048;

    /// <summary>
    /// The most of the sun's shadow cascades drawn, from 1 to 4, whatever <see cref="ViewLights.Shadows"/> asks for. Each
    /// cascade covers ground further out than the last and costs drawing the scene once more, so fewer are faster,
    /// and the sun's shadows end sooner.
    /// </summary>
    public int ShadowCascades
    {
        get;
        set => field = value is >= 1 and <= MaxShadowCascades
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The sun can draw from 1 to 4 shadow cascades.");
    } = MaxShadowCascades;

    /// <summary>
    /// The sun's contact shadows: the thin dark line where things meet the ground, found on screen, which
    /// the shadow maps are too coarse to draw. Off, only the maps shade there. See <see cref="ViewLights.Shadows"/>.
    /// </summary>
    public bool ContactShadows { get; set; } = true;

    /// <summary>Point and spot light shadows. Off, those lights are blocked only by nearby surfaces, as the sun is without its shadows.</summary>
    public bool LightShadows { get; set; } = true;

    /// <summary>Takes every setting from <paramref name="other"/>.</summary>
    internal void CopyFrom(GraphicsShadowing other)
    {
        SunShadows = other.SunShadows;
        ShadowResolution = other.ShadowResolution;
        ShadowCascades = other.ShadowCascades;
        ContactShadows = other.ContactShadows;
        LightShadows = other.LightShadows;
    }
}
