namespace Super;

/// <summary>The air: fog, haze, the physical sky's stars and air, water, fog volumes and the volumetric fog.</summary>
public sealed class GraphicsAtmosphere
{
    /// <summary>
    /// The height fog from <see cref="SkySettings.Fog"/>, which sets far things back in the air. Off, the air is
    /// clear whatever the fog's density. Haze is not fog and stays.
    /// </summary>
    public bool Fog { get; set; } = true;

    /// <summary>
    /// Distant things fading into the sky behind them, from <see cref="SkySettings.HazeStart"/> to
    /// <see cref="SkySettings.HazeEnd"/>. Off, they keep their colours however far they are. The sky's own colour stays.
    /// </summary>
    public bool Haze { get; set; } = true;

    /// <summary>
    /// The physical sky's air between the eye and far surfaces, from <see cref="SkySettings.Turbidity"/>: far hills
    /// turn blue and pale, and glow warm towards a low sun. Off, they keep their colours however far they are. A scene
    /// that sets its own haze gets that instead. It costs almost nothing.
    /// </summary>
    public bool AerialPerspective { get; set; } = true;

    /// <summary>
    /// The stars in a physical sky at night (<see cref="SkySettings.Turbidity"/>), fading in as the sky darkens after
    /// sunset. Off, the night sky is its plain glow and the moon. They are drawn per pixel of the sky only, which
    /// costs almost nothing.
    /// </summary>
    public bool Stars { get; set; } = true;

    /// <summary>
    /// The water set with <c>view.Surroundings.Water</c>. Off, the view is drawn as if the camera weren't under water:
    /// no absorption, no caustics and no light shafts, and the sky shows.
    /// </summary>
    public bool Underwater { get; set; } = true;

    /// <summary>
    /// The fog volumes placed with <c>view.Effects.FogVolume</c>, such as mist in a room or a lamp's beam. Off, only the
    /// scene's own fog fills the volumetric fog. They need <see cref="VolumetricFog"/>, and cost a little more for each
    /// volume on screen.
    /// </summary>
    public bool FogVolumes { get; set; } = true;

    /// <summary>
    /// Working out the fog near the camera in a volume that the sun's shadows and the point and spot lights
    /// light up, so sunlight falls through it in shafts and lamps glow with halos. Off, the fog is the plain
    /// height fog everywhere, exactly as without it. It costs two compute passes over a small grid, so
    /// <see cref="Graphics.Fastest"/> turns it off.
    /// </summary>
    public bool VolumetricFog { get; set; } = true;

    /// <summary>
    /// Scene pixels across one cell of the volumetric fog's grid, each way, from 4 to 32. Smaller cells show finer
    /// shafts and sharper edges on fog volumes, and cost about four times as much each time the size halves. The
    /// size is after <see cref="GraphicsScreen.RenderScale"/>. At 8, the default, the grid takes about 35 MB at 1600
    /// by 900, and 16 cuts that to a quarter.
    /// </summary>
    public int VolumetricFogCellSize
    {
        get;
        set => field = value is >= 4 and <= 32
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "A volumetric fog cell can be from 4 to 32 pixels across.");
    } = 8;

    /// <summary>
    /// How many slices the volumetric fog's grid has in depth, from 16 to 128, out to
    /// <see cref="Rendition.FogSettings.VolumetricDistance"/>. More slices keep shafts and fog volumes sharp further
    /// out, and the cost grows with the count. <see cref="GraphicsQuality.Ultra"/> uses 128; the rest 64.
    /// </summary>
    public int VolumetricFogSlices
    {
        get;
        set => field = value is >= 16 and <= 128
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The volumetric fog can have from 16 to 128 slices.");
    } = 64;

    /// <summary>Takes every setting from <paramref name="other"/>.</summary>
    internal void CopyFrom(GraphicsAtmosphere other)
    {
        Fog = other.Fog;
        Haze = other.Haze;
        AerialPerspective = other.AerialPerspective;
        Stars = other.Stars;
        Underwater = other.Underwater;
        FogVolumes = other.FogVolumes;
        VolumetricFog = other.VolumetricFog;
        VolumetricFogCellSize = other.VolumetricFogCellSize;
        VolumetricFogSlices = other.VolumetricFogSlices;
    }
}
