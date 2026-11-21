using System.Globalization;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// Baked lighting: where bakes are looked up, how they fade in, what the last frame's baked areas came to, the bakes
/// that wait for the GPU and the ones the renderer makes by itself in the background. Reached through
/// <see cref="Renderer.Baking"/>. See "Baked lighting" in docs/rendering.md.
/// </summary>
public sealed class RendererBaking
{
    /// <summary>The longest <see cref="FadeSeconds"/> can be.</summary>
    public const float MaxFadeSeconds = 10f;

    private readonly VulkanContext _context;
    private readonly UploadQueue _uploads;
    private readonly EnvironmentSky _sky;

    // The view's lights with the sun coloured by the physical sky's air; see SkyModel.Tinted.
    private DirectionalLight[] _tintedLights = [];

    internal RendererBaking(
        (VulkanContext Context, UploadQueue Uploads, EnvironmentSky Sky) gpu,
        FrameBakes frameBakes,
        (BakeJobs Jobs, BackgroundBakes Background, ChunkBakePool Chunks) work)
    {
        (_context, _uploads, _sky) = gpu;
        Frame = frameBakes;
        (Jobs, Background, ChunkBakes) = work;
    }

    /// <summary>
    /// Where bakes are looked up, such as a <see cref="LightBakeStore"/> over the game's content. With none, or with
    /// <see cref="RenderEffects.BakedLighting"/> in <see cref="FrameView.Off"/>, every bake counts as missing and every
    /// area is lit live. See "Baked lighting" in docs/rendering.md.
    /// </summary>
    public IBakeStore Bakes
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    } = NoBakes.Instance;

    /// <summary>
    /// How long a bake that arrives while its area is on screen takes to fade in, in seconds of
    /// <see cref="FrameView.Time"/>. 0 shows it at once. A bake already there on the first frame that names its area
    /// always shows at once. From 0 to 10. See "With a fresh bake" in docs/rendering.md.
    /// </summary>
    public float FadeSeconds
    {
        get => Frame.Resolver.FadeSeconds;
        set => Frame.Resolver.FadeSeconds = value is >= 0f and <= MaxFadeSeconds
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), string.Create(
                CultureInfo.InvariantCulture, $"FadeSeconds must be from 0 to {MaxFadeSeconds}, not {value}."));
    }

    /// <summary>
    /// What the last frame's baked areas came to, or null when it named no baked space or chunk. See "Baked lighting"
    /// in docs/rendering.md.
    /// </summary>
    public BakeFrame? LastFrame => Frame.Last;

    /// <summary>
    /// What the renderer bakes by itself, in the background, a slice a frame: each area a frame names whose bake is
    /// missing or stale, once its lights have settled. Null, the default, bakes nothing. Ignored where
    /// <see cref="Renderer.CanTrace"/> is false. Setting it stops the running bake and forgets what was baked, and
    /// prepares baking, which waits for the GPU the first time, so set it outside a frame. Where the GPU can trace rays
    /// but can't bake, that is reported once and nothing is baked. See "Baking in the background" in docs/rendering.md.
    /// </summary>
    public AutoBake? AutoBake
    {
        get => Background.AutoBake;
        set => Background.AutoBake = value;
    }

    /// <summary>
    /// How the background bake is getting on, or null when none runs and no area waits for one. While areas wait and
    /// none runs yet, its name is empty and its fraction 0. See <see cref="AutoBake"/>.
    /// </summary>
    public BakeProgress? Progress => Background.Progress;

    /// <summary>
    /// Makes the jobs that bake a frame's space or chunks, on the baker it prepares. Tests prepare it and make jobs
    /// through this; <see cref="Bake"/> and <see cref="BakeChunk"/> run a job to the end.
    /// </summary>
    internal BakeJobs Jobs { get; }

    /// <summary>The bakes the renderer makes by itself, a slice a frame. See <see cref="AutoBake"/>.</summary>
    internal BackgroundBakes Background { get; }

    /// <summary>The chunk bakes being read, uploaded or on the GPU, shared by every copy of a chunk.</summary>
    internal ChunkBakePool ChunkBakes { get; }

    /// <summary>The frame's baked areas and what it draws of them.</summary>
    internal FrameBakes Frame { get; }

    /// <summary>
    /// Path traces the view's baked space into a lightmap and returns it as a <c>.lightbake</c> file's bytes, made from
    /// the frame's inputs so a frame drawn like this one finds it fresh. Every static draw in the frame blocks and
    /// bounces light; those the space owns, without wind and not transparent, get a lightmap rectangle. Emissive
    /// surfaces, the sky and Baked lights are baked in full; a Mixed light only after its first bounce, since its direct
    /// light stays real-time, and it gets a shadow mask channel. A Realtime light or one that is moving is left out.
    /// The space's light probes are baked too, by the same rules, into tiles laid out as the live probes' are.
    /// Waits for the GPU, taking seconds at <see cref="BakeQuality.Final"/>. See "Baked lighting" in docs/rendering.md.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The view names no baked space, the space holds more than <see cref="BakeFiles.MaxSpaceProbes"/> probes, some of its
    /// draws don't fit in <see cref="LightmapLayout.MaxPages"/> pages, or the GPU can't trace rays.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="settings"/> asks for another texel density than the space's
    /// <see cref="BakedSpace.TexelsPerMetre"/>, so no frame would find the bake fresh.
    /// </exception>
    public byte[] Bake(in FrameView view, ReadOnlySpan<DrawItem> draws, BakeSettings settings)
    {
        var tinted = SkyModel.Tinted(view, ref _tintedLights);
        var space = BakeJobs.StartSpace(tinted, draws, settings);
        SettleForBake(tinted);
        Jobs.Bakers.Prepare(probes: true);
        using var job = Jobs.SpaceJob(tinted, draws, settings, space);
        return Jobs.RunToEnd(job);
    }

    /// <summary>
    /// Path traces chunk <paramref name="chunk"/> of the frame in its own space and returns its <c>.lightbake</c> bytes,
    /// made from the frame's inputs so a frame placing the chunk like this, or anywhere else, finds it fresh. Only the
    /// static draws the chunk owns are in it, moved into the chunk's space as <see cref="BakeAreas.Relative"/> moves
    /// them, so they alone block and bounce light; those without wind and not transparent share one lightmap page,
    /// <see cref="LightmapLayout.BuildSingle"/>. Its plain light holds its own Baked lights in full, its Mixed lights
    /// after their first bounce, and glowing surfaces, but no sky and no sun; it is only stored when there is such a
    /// light or surface. Its transfer holds how the sky and a sun from any direction light it, so the game can light a
    /// placed copy by its own sky and sun. A chunk has no probes. Waits for the GPU. See "Baked lighting" in
    /// docs/rendering.md and "Chunk transfer" in docs/design.md.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The frame has no chunk <paramref name="chunk"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="settings"/> asks for another texel density than the chunk's <see cref="BakedChunk.TexelsPerMetre"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The chunk owns no static draws, some of its draws don't fit in its one page, or the GPU can't trace rays.
    /// </exception>
    public byte[] BakeChunk(in FrameView view, ReadOnlySpan<DrawItem> draws, int chunk, BakeSettings settings)
    {
        var tinted = SkyModel.Tinted(view, ref _tintedLights);
        var resolved = Jobs.StartChunk(tinted, draws, chunk, settings);
        SettleForBake(tinted);
        Jobs.Bakers.Prepare(probes: false);
        using var job = Jobs.ChunkJob(tinted, draws, settings, resolved);
        return Jobs.RunToEnd(job);
    }

    // Readies the GPU for a bake that waits: nothing in flight, no background bake on the baker's pages, every upload
    // done, and the view's sky drawn.
    private void SettleForBake(in FrameView view)
    {
        _context.WaitIdle();
        Background.DropAll();
        _uploads.Flush();
        _sky.Follow(view, view.Off);
        _sky.Flush();
    }
}
