using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// The GPU baker. Its first stage, texel setup, draws each static draw into its lightmap rectangle and keeps, for every
/// texel of every page, the world position, geometric normal and shading normal of the surface under it, and whether
/// the texel is valid: covered, and not inside a wall. Owns the pages' images and its own pipelines and descriptor
/// sets, apart from the frame's. Its parts: <see cref="TexelPages"/> holds the pages, <see cref="TexelDraw"/> and
/// <see cref="TexelValidity"/> set them up, <see cref="BakeTracePass"/> traces paths over them and
/// <see cref="BakeProbePass"/> traces the probes. See "Texel setup" in docs/design.md.
/// </summary>
internal sealed unsafe class Baker : IDisposable
{
    private readonly VulkanContext _context;
    private readonly DescriptorPool _pool;
    private readonly TexelDraw _draw;
    private readonly TexelValidity _validity;

    /// <exception cref="InvalidOperationException">The device can't trace rays, or can't write single-channel images from compute shaders.</exception>
    public Baker(VulkanContext context, SceneStores stores)
    {
        _context = context;
        var tracing = stores.Tracing
            ?? throw new InvalidOperationException("Baking needs a GPU that can trace rays, and this one can't.");
        if (!context.Abilities.StorageImageExtendedFormats)
        {
            throw new InvalidOperationException("Baking needs shaderStorageImageExtendedFormats, which this GPU lacks.");
        }

        // One validity set and one resolve set a page, rewritten by each SetUpTexels.
        const uint pages = LightmapLayout.MaxPages;
        _pool = context.CreateDescriptorPool(
            pages * 2,
            new DescriptorPoolSize(DescriptorType.AccelerationStructureKhr, pages),
            new DescriptorPoolSize(DescriptorType.StorageBuffer, pages),
            new DescriptorPoolSize(DescriptorType.StorageImage, pages * 6),
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, pages * 3));

        Pages = new TexelPages(context);
        _draw = new TexelDraw(context, stores, Pages);
        _validity = new TexelValidity(context, stores, tracing);
        Tracing = new BakeTracePass(context, stores, tracing);
        Probes = new BakeProbePass(context, stores, tracing);
        Filtering = new BakeFilter(context);
        JobPools = new BakeJobPools(context);
    }

    /// <summary>
    /// Draws with multisampling even where the device has conservative rasterization, so tests run the fallback too.
    /// </summary>
    internal bool ForceMultisample { get; init; }

    /// <summary>The device the baker records for.</summary>
    internal VulkanContext Context => _context;

    /// <summary>Whether texels are drawn with conservative rasterization rather than multisampling.</summary>
    internal bool Conservative => !ForceMultisample && _context.Abilities.ConservativeRasterization;

    /// <summary>The pages' images, and which bake holds them.</summary>
    internal TexelPages Pages { get; }

    /// <summary>The path tracing of a page's texels, its sums and their readback.</summary>
    internal BakeTracePass Tracing { get; }

    /// <summary>The path tracing of a space's probes, and the probe volume they fold through.</summary>
    internal BakeProbePass Probes { get; }

    /// <summary>The filter and dilation every page's light, and a chunk's transfer, goes through.</summary>
    internal BakeFilter Filtering { get; }

    /// <summary>The descriptor pools of the jobs, one each.</summary>
    internal BakeJobPools JobPools { get; }

    /// <summary>
    /// Records texel setup for every page of <paramref name="layout"/> into <paramref name="cmd"/>: builds the scene's
    /// top-level structure from <paramref name="draws"/>, draws the static draws that have rectangles into their pages,
    /// and marks which texels are valid. Leaves every page's images in the general layout, ready for compute shaders
    /// and copies. The descriptor sets are rewritten, so the previous call's work must be done. The top-level structure
    /// is the frame's, recycled at the renderer's next frame; a <see cref="BakeJob"/> holds its own instead.
    /// </summary>
    public void SetUpTexels(CommandBuffer cmd, LightmapLayout layout, ReadOnlySpan<DrawItem> draws)
    {
        _context.Api.ResetDescriptorPool(_context.Device, _pool, 0).Check();
        var scene = Tracing.BuildScene(cmd, draws);
        RecordTexels(cmd, layout, draws, _pool, scene);
    }

    /// <summary>
    /// Records texel setup as <see cref="SetUpTexels"/> does, against <paramref name="scene"/>, with its descriptor
    /// sets from <paramref name="pool"/>, which needs room for <see cref="BakeJobPools.Create"/>'s sets for setup.
    /// </summary>
    internal void RecordTexels(
        CommandBuffer cmd, LightmapLayout layout, ReadOnlySpan<DrawItem> draws, DescriptorPool pool, in TopLevelStructure scene)
    {
        Pages.Ensure(layout.Pages);
        _draw.DrawPages(cmd, layout, draws, pool, Conservative);
        _validity.Record(cmd, Pages, layout.Pages, pool, scene);
    }

    /// <summary>
    /// The texels of a region of a page, <see cref="BakeGpu.TexelFloats"/> floats each, row by row. Waits for the GPU,
    /// so only call it once <see cref="SetUpTexels"/>'s work has been submitted; for tests.
    /// </summary>
    internal float[] ReadTexels(int page, int x, int y, int width, int height) => Pages.ReadTexels(page, x, y, width, height);

    /// <summary>The texels a page's rectangles cover, with <see cref="BakeGpu.TraceMargin"/> round them, clamped to the page.</summary>
    internal static (int X, int Y, int Width, int Height) UsedRegion(LightmapLayout layout, int page)
    {
        const int side = LightmapLayout.PageSize;
        const int margin = BakeGpu.TraceMargin;
        int left = side, top = side, right = 0, bottom = 0;
        foreach (var (_, rect) in layout.Draws)
        {
            if (rect.Page != page)
            {
                continue;
            }

            left = Math.Min(left, (int)MathF.Floor(rect.Offset.X * side) - margin);
            top = Math.Min(top, (int)MathF.Floor(rect.Offset.Y * side) - margin);
            right = Math.Max(right, (int)MathF.Ceiling((rect.Offset.X + rect.Scale.X) * side) + margin);
            bottom = Math.Max(bottom, (int)MathF.Ceiling((rect.Offset.Y + rect.Scale.Y) * side) + margin);
        }

        left = Math.Max(left, 0);
        top = Math.Max(top, 0);
        right = Math.Clamp(right, left + 1, side);
        bottom = Math.Clamp(bottom, top + 1, side);
        return (left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Cleans up a traced page before it is encoded, with the page's texels from the last <see cref="SetUpTexels"/>.
    /// Values that aren't finite become 0, and ones past the half-float range its limit. With <paramref name="smooth"/>,
    /// <see cref="BakeFilter.Passes"/> passes of an edge-aware à-trous filter then smooth the irradiance and direction;
    /// the shadow mask is left sharp. The filter stops at steps, creases and changes in light larger than the traced
    /// noise explains, using the variance in irradiance w, so converged shadow edges stay sharp. Last,
    /// <see cref="BakeFilter.DilationRings"/> rings of dilation fill the gutters and invalid texels from their
    /// neighbours. No texel ever takes light from a texel drawn for another rectangle, apart from gutter texels nothing
    /// covers. Waits for the GPU; a <see cref="BakeJob"/> records the same run as a step with
    /// <see cref="BakeFilter.Record"/>.
    /// </summary>
    /// <returns>
    /// The page with the irradiance and mask as they will be stored, the direction as a mean direction with w 1, and
    /// <see cref="TracedPage.Filled"/> marking the texels that hold light.
    /// </returns>
    public TracedPage FilterAndDilate(TracedPage traced, bool smooth)
    {
        if (traced.Page < 0 || traced.Page >= Pages.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(traced), traced.Page, $"There are {Pages.Count} pages set up, so page {traced.Page} doesn't exist.");
        }

        _context.Api.ResetDescriptorPool(_context.Device, Filtering.Pool, 0).Check();
        using var run = CreateFilter(Filtering.Pool, traced.Page, (traced.X, traced.Y, traced.Width, traced.Height));
        run.Fill(traced.Irradiance, traced.Direction, traced.Mask);
        _context.SubmitAndWait(cmd => Filtering.Record(cmd, run, true, smooth));
        return BakeEncoding.Filtered(traced, run);
    }

    /// <summary>
    /// A filter run's images and sets for a page's region, from <paramref name="pool"/>: two copies of its irradiance,
    /// direction and mask, one set each way between them, and the host-visible buffer they're uploaded from and read
    /// back into. One run uses it at a time; a chunk's page runs its light and both transfer runs through the same.
    /// </summary>
    internal FilterRun CreateFilter(DescriptorPool pool, int page, (int X, int Y, int Width, int Height) region)
    {
        var run = new FilterRun(_context, page, region);
        var targets = Pages[page];
        Filtering.Bind(pool, run, targets.Position.View, targets.GeometricNormal.View, targets.Validity.View);
        return run;
    }

    public void Dispose()
    {
        Pages.Dispose();
        _draw.Dispose();
        _validity.Dispose();
        Tracing.Dispose();
        Probes.Dispose();
        Filtering.Dispose();
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
    }
}

/// <summary>
/// The descriptor pools of <see cref="BakeJob"/>s, and how many are made and not yet freed: one for each job not yet
/// disposed, for tests.
/// </summary>
internal sealed unsafe class BakeJobPools(VulkanContext context)
{
    /// <summary>How many pools are made and not yet freed.</summary>
    public int Live { get; private set; }

    /// <summary>
    /// A descriptor pool of a <see cref="BakeJob"/>'s own, with room for every set the job allocates over a layout of
    /// <paramref name="pages"/> pages: texel setup's validity and resolve sets, the trace set and the filter's two sets
    /// a page, and one probe set. Each job has its own, so no two jobs share a set. Free it with <see cref="Destroy"/>
    /// once the GPU is done with the job.
    /// </summary>
    public DescriptorPool Create(int pages)
    {
        uint count = (uint)Math.Max(pages, 1);
        var pool = context.CreateDescriptorPool(
            count * 5 + 1,
            new DescriptorPoolSize(DescriptorType.AccelerationStructureKhr, count * 2 + 1),
            new DescriptorPoolSize(DescriptorType.StorageBuffer, count * 3 + 3),
            new DescriptorPoolSize(DescriptorType.StorageImage, count * (3 + 3 + 6 + BakeGpu.TransferSums + 18) + 2),
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, count * 4 + 1));
        Live++;
        return pool;
    }

    /// <summary>Frees a pool from <see cref="Create"/>, and every set in it.</summary>
    public void Destroy(DescriptorPool pool)
    {
        context.Api.DestroyDescriptorPool(context.Device, pool, null);
        Live--;
    }
}
