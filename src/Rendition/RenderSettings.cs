using System.Globalization;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// How the renderer draws: the quality and cost settings a game or its options screen changes while it runs, each
/// taking effect on the next frame. See <see cref="Renderer.Settings"/>.
/// </summary>
public sealed class RenderSettings
{
    /// <summary>The smallest <see cref="MeshDetailScale"/>: models switch to their simpler versions at twice the distance.</summary>
    public const float MinMeshDetail = 0.5f;

    /// <summary>The largest <see cref="MeshDetailScale"/>: models keep their finer versions to twice the distance.</summary>
    public const float MaxMeshDetail = 2f;

    /// <summary>The smallest <see cref="RenderScale"/>: a quarter of the window's pixels.</summary>
    public const float MinRenderScale = 0.5f;

    /// <summary>The largest <see cref="RenderScale"/>: four times the window's pixels.</summary>
    public const float MaxRenderScale = 2f;

    internal RenderSettings(RendererOptions options)
    {
        Vsync = !options.DisableVsync;
        VsyncChanged = false;
        RenderScale = CheckRenderScale(options.RenderScale);
        ShadowResolution = ShadowPass.CheckResolution(options.ShadowResolution);

        // The targets start at the scene's size; the first frame makes FSR's, when it's used, as it learns whether
        // anti-aliasing is on. Setting TAA, which it starts at, chooses nothing, and TAA is what it comes to.
        Upscaler = options.Upscaler;
    }

    /// <summary>
    /// Waits for the display before showing each frame, so the picture never tears, and the
    /// frame rate stops at the display's. Turned off, frames are shown as soon as they're done.
    /// Displays that can't do that keep it on. A change takes effect on the next frame.
    /// </summary>
    public bool Vsync
    {
        get;
        set
        {
            if (value != field)
            {
                field = value;
                VsyncChanged = true;
            }
        }
    }

    /// <summary>Whether <see cref="Vsync"/> changed since the swapchain was last made.</summary>
    internal bool VsyncChanged { get; set; }

    /// <summary>
    /// How many texels across each of the sun's cascade maps is: 1024, 2048 or 4096. Finer maps
    /// give sharper shadows for four times the memory and drawing each step up. A change
    /// rebuilds the maps before the next frame.
    /// </summary>
    public int ShadowResolution
    {
        get;
        set => field = ShadowPass.CheckResolution(value);
    }

    /// <summary>
    /// The most of the sun's cascades drawn, from 1 to <see cref="ShadowPass.MaxCascades"/>, whatever
    /// <see cref="ShadowSettings.Cascades"/> asks for. Each cascade left out saves drawing the scene once more,
    /// and the sun's shadows end where the last cascade drawn ends. Takes effect on the next frame.
    /// </summary>
    public int MaxShadowCascades
    {
        get;
        set => field = value is >= 1 and <= ShadowPass.MaxCascades
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"The sun can draw from 1 to {ShadowPass.MaxCascades} cascades.");
    } = ShadowPass.MaxCascades;

    /// <summary>
    /// Scene pixels across one cell of the volumetric fog's grid, each way, from 4 to 32; 8 unless set. Smaller
    /// cells are sharper and cost more. A change remakes the grid, with no history, before the next frame.
    /// </summary>
    public int VolumetricFogCellSize
    {
        get;
        set => field = VolumetricFog.CheckCellPixels(value);
    } = VolumetricFog.CellPixels;

    /// <summary>
    /// Slices in the volumetric fog's grid, from the camera out to <see cref="FogSettings.VolumetricDistance"/>,
    /// from 16 to 128; 64 unless set. A change remakes the grid, with no history, before the next frame.
    /// </summary>
    public int VolumetricFogSlices
    {
        get;
        set => field = VolumetricFog.CheckSlices(value);
    } = VolumetricFog.Slices;

    /// <summary>
    /// How long models keep their finer versions as they move away, from 0.5 to 2. 1 switches when a simpler version
    /// would stray by a pixel on screen. At 0.5 they may stray by two, so they switch sooner and fewer triangles are
    /// drawn; at 2 by half a pixel. The sun's shadows follow in step. Takes effect on the next frame.
    /// </summary>
    public float MeshDetailScale
    {
        get;
        set => field = value is >= MinMeshDetail and <= MaxMeshDetail
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                string.Create(CultureInfo.InvariantCulture, $"Mesh detail can be from {MinMeshDetail} to {MaxMeshDetail}."));
    } = 1f;

    /// <summary>
    /// How large the scene is drawn against the window, from 0.5 to 2 each way. Above 1 the scene is
    /// drawn larger and shrunk to fit, which smooths edges and fine detail. Below 1 it's drawn smaller
    /// and stretched, which is faster and softer. 1 draws at the window's size, with nothing resampled.
    /// The overlay is always drawn at the window's size. A change rebuilds the targets before the next frame.
    /// </summary>
    public float RenderScale
    {
        get;
        set => field = CheckRenderScale(value);
    } = 1f;

    /// <summary>
    /// How many pixels trace their screen-space reflection each frame: see <see cref="Rendition.ReflectionResolution"/>.
    /// A change rebuilds the targets before the next frame.
    /// </summary>
    public ReflectionResolution ReflectionResolution
    {
        get;
        set => field = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Reflection resolution can be Half or Full.");
    } = ReflectionResolution.Half;

    /// <summary>
    /// How many pixels work out their ambient occlusion each frame: see <see cref="Rendition.OcclusionResolution"/>.
    /// A change rebuilds the targets before the next frame.
    /// </summary>
    public OcclusionResolution OcclusionResolution
    {
        get;
        set => field = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Occlusion resolution can be Half or Full.");
    } = OcclusionResolution.Half;

    /// <summary>
    /// What anti-aliases the scene: the engine's own TAA, or AMD's FSR 3.1, which also rebuilds a scene drawn
    /// below a <see cref="RenderScale"/> of 1 at the window's size instead of stretching it. FSR needs its library,
    /// amd_fidelityfx_vk.dll, next to the game; without it, or above a render scale of 1, TAA is used, and a missing
    /// library is said once on the error stream. A change takes effect on the next frame and starts the history again.
    /// </summary>
    public Upscaler Upscaler
    {
        get;
        set
        {
            if (value != field)
            {
                field = value;
                UpscalerUsed = Upscale.Choose(value, value == Upscaler.Fsr && Fsr.IsAvailable, Console.Error);
            }
        }
    }

    /// <summary>The upscaler <see cref="Upscaler"/> comes to once FSR's library is looked for, or TAA after FSR failed.</summary>
    internal Upscaler UpscalerUsed { get; set; }

    /// <summary>
    /// How many light probes follow the camera, and how many are updated a frame: see
    /// <see cref="Rendition.ProbeQuality"/>. A change makes the probes' atlases again, all uncaptured,
    /// on the next frame with the probes on, which waits for the GPU once.
    /// </summary>
    public ProbeQuality ProbeQuality
    {
        get => Probes.Quality;
        set => Probes.Quality = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Probe quality can be Low, Medium or High.");
    }

    /// <summary>The live light probes, whose quality <see cref="ProbeQuality"/> sets.</summary>
    internal LightProbes Probes { private get; set; } = null!;

    /// <summary>Times the render passes. Off by default, because reading the times costs a little.</summary>
    public bool MeasureGpu { get; set; }

    /// <summary>Returns <paramref name="value"/> if it's a <see cref="RenderScale"/> from 0.5 to 2, and throws if not.</summary>
    public static float CheckRenderScale(float value) => value is >= MinRenderScale and <= MaxRenderScale
        ? value
        : throw new ArgumentOutOfRangeException(nameof(value), string.Create(
            CultureInfo.InvariantCulture, $"RenderScale must be from {MinRenderScale} to {MaxRenderScale}, not {value}."));

    /// <summary>
    /// How many pixels across and down the scene is drawn for a window this size at this
    /// <see cref="RenderScale"/>. At 1 it's the window's size. Otherwise each side is rounded to
    /// the nearest even number, so at 0.5 each scene pixel covers exactly two by two window pixels.
    /// A side longer than <paramref name="maxSize"/>, the largest image the device can make, shrinks
    /// the whole scene to fit, keeping its shape.
    /// </summary>
    public static (int Width, int Height) SceneSize(int width, int height, float scale, int maxSize = int.MaxValue)
    {
        if (scale != 1f)
        {
            width = Even(width * (double)scale);
            height = Even(height * (double)scale);
        }

        int longest = Math.Max(width, height);
        if (longest <= maxSize)
        {
            return (width, height);
        }

        // Whole numbers, so the longest side lands on the limit exactly.
        return (EvenBelow((long)width * maxSize / longest), EvenBelow((long)height * maxSize / longest));

        static int Even(double size) => Math.Max((int)Math.Round(size / 2d) * 2, 2);

        static int EvenBelow(long size) => (int)Math.Max(size / 2 * 2, 2);
    }
}
