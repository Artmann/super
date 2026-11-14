using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Passes;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// The GPU baker. Its first stage, texel setup, draws each static draw into its lightmap rectangle and keeps, for every
/// texel of every page, the world position, geometric normal and shading normal of the surface under it, and whether
/// the texel is valid: covered, and not inside a wall. Owns the pages' images and its own pipelines and descriptor
/// sets, apart from the frame's. See "Texel setup" in docs/design.md.
/// </summary>
internal sealed unsafe class Baker : IDisposable
{
    /// <summary>Floats a texel has in <see cref="ReadTexels(int, int, int, int, int)"/>: position xyz and the covering rectangle's number from 1 (0 where uncovered), geometric normal xyz, shading normal xyz, valid.</summary>
    internal const int TexelFloats = 11;

    /// <summary>Rays sent from each texel to look for the backs of walls.</summary>
    internal const int ProbeRays = 16;

    /// <summary>How far each of those rays reaches, in metres.</summary>
    internal const float ProbeRayLength = 0.05f;

    /// <summary>The share of a texel's rays that may hit backs before it is invalid: more than half.</summary>
    internal const float InvalidShare = 0.5f;

    /// <summary>How far along the normal the rays start, in metres, so they don't hit the texel's own surface.</summary>
    internal const float ProbeLift = 0.001f;

    /// <summary>The side of the multisampled tile drawn at a time without conservative rasterization, in texels.</summary>
    internal const int TileSize = 512;

    /// <summary>How far along the normal paths and shadow rays start, in metres, so they don't hit their own surface.</summary>
    internal const float TraceLift = 0.001f;

    /// <summary>Texels traced round the rectangles on a page, so the edge texels conservative drawing adds get light too.</summary>
    internal const int TraceMargin = 2;

    /// <summary>Probes traced side by side in one pass, each a row of its six faces.</summary>
    internal const int ProbesPerBatch = 64;

    /// <summary>A chunk's transfer sums: the sky's for red, green and blue, the sun's, and the sun's colour.</summary>
    internal const int TransferSums = 5;

    /// <summary>The floats of transfer each texel of <see cref="TracedPage.Transfer"/> holds, four a sum.</summary>
    internal const int TransferFloats = TransferSums * 4;

    private const uint GroupSize = 8;
    private const Format PositionFormat = Format.R32G32B32A32Sfloat;
    private const Format SumFormat = Format.R32G32B32A32Sfloat;
    private const Format NormalFormat = Format.R16G16B16A16Sfloat;
    private const Format ValidityFormat = Format.R8Unorm;

    private readonly VulkanContext _context;
    private readonly Renderer _renderer;
    private readonly RayTracing _tracing;
    private readonly TraceScene _traceScene;
    private readonly DescriptorSetLayout _validitySetLayout;
    private readonly DescriptorSetLayout _resolveSetLayout;
    private readonly DescriptorPool _pool;
    private readonly PipelineLayout _texelLayout;
    private readonly PipelineLayout _validityLayout;
    private readonly PipelineLayout _resolveLayout;
    private readonly LivePipeline _validityPipeline;
    private readonly LivePipeline _resolvePipeline;
    private readonly Sampler _sampler;
    private readonly List<PageTargets> _pages = [];
    private readonly DescriptorSetLayout _traceSetLayout;
    private readonly PipelineLayout _traceLayout;
    private readonly LivePipeline _tracePipeline;
    private readonly DescriptorSetLayout _probeSetLayout;
    private readonly PipelineLayout _probeLayout;
    private readonly LivePipeline _probePipeline;
    private readonly Dictionary<LightmapRect, int> _charts = [];
    private ProbeVolume? _probeVolume;
    private (object Owner, string Name)? _pagesClaimedBy;
    private LivePipeline? _texelPipeline;
    private bool _texelPipelineConservative;
    private TileTargets? _tile;

    /// <exception cref="InvalidOperationException">The device can't trace rays, or can't write single-channel images from compute shaders.</exception>
    public Baker(VulkanContext context, Renderer renderer)
    {
        _context = context;
        _renderer = renderer;
        _tracing = renderer.Tracing
            ?? throw new InvalidOperationException("Baking needs a GPU that can trace rays, and this one can't.");
        _traceScene = new TraceScene(renderer.TraceFlagsOf);
        if (!context.StorageImageExtendedFormats)
        {
            throw new InvalidOperationException("Baking needs shaderStorageImageExtendedFormats, which this GPU lacks.");
        }

        var api = context.Api;

        // Validity: the scene's top-level structure, its geometry table, the position and geometric normal read and the
        // validity written. The resource table is set 1, for the materials trace.glsl reads.
        var validityBindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.AccelerationStructureKhr, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
        };
        _validitySetLayout = CreateSetLayout(validityBindings, 5);

        // Resolve: the multisampled tile's three targets read and the page's three written.
        var resolveBindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(2, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(5, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
        };
        _resolveSetLayout = CreateSetLayout(resolveBindings, 6);

        // One validity set and one resolve set a page, rewritten by each SetUpTexels.
        const uint pages = LightmapLayout.MaxPages;
        _pool = context.CreateDescriptorPool(
            pages * 2,
            new DescriptorPoolSize(DescriptorType.AccelerationStructureKhr, pages),
            new DescriptorPoolSize(DescriptorType.StorageBuffer, pages),
            new DescriptorPoolSize(DescriptorType.StorageImage, pages * 6),
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, pages * 3));

        _texelLayout = CreatePipelineLayout([], ShaderStageFlags.VertexBit, (uint)sizeof(TexelConstants));
        _validityLayout = CreatePipelineLayout(
            [_validitySetLayout, renderer.Resources.Layout], ShaderStageFlags.ComputeBit, (uint)sizeof(ProbeConstants));
        _resolveLayout = CreatePipelineLayout([_resolveSetLayout], ShaderStageFlags.ComputeBit, (uint)sizeof(ResolveConstants));
        _validityPipeline = PipelineFactory.CreateCompute(context, "bake_validity.comp", _validityLayout);
        _resolvePipeline = PipelineFactory.CreateCompute(context, "bake_resolve.comp", _resolveLayout);

        // Trace: the scene and geometry table, the page's position, geometric normal and validity read, the lights, the
        // three sums read and written, the sky, and a chunk's five transfer sums. Set 1 is the resource table again. Each
        // job writes a set a page from its own pool.
        var traceBindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.AccelerationStructureKhr, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(5, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(6, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(7, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(8, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(9, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(10, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(11, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(12, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(13, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(14, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
        };
        _traceSetLayout = CreateSetLayout(traceBindings, 15);
        _traceLayout = CreatePipelineLayout(
            [_traceSetLayout, renderer.Resources.Layout], ShaderStageFlags.ComputeBit, (uint)sizeof(TraceConstants));
        _tracePipeline = PipelineFactory.CreateCompute(context, "bake_trace.comp", _traceLayout);

        // Probes: the scene and geometry table, the lights, the probes' positions, the sum read and written, the faces
        // written, and the sky. Set 1 is the resource table. Each job writes one set from its own pool.
        var probeBindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.AccelerationStructureKhr, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(2, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(3, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(5, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(6, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
        };
        _probeSetLayout = CreateSetLayout(probeBindings, 7);
        _probeLayout = CreatePipelineLayout(
            [_probeSetLayout, renderer.Resources.Layout], ShaderStageFlags.ComputeBit, (uint)sizeof(ProbeTraceConstants));
        _probePipeline = PipelineFactory.CreateCompute(context, "bake_probes.comp", _probeLayout);

        Filtering = new BakeFilter(context);

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        api.CreateSampler(context.Device, &samplerInfo, null, out _sampler).Check();
    }

    /// <summary>
    /// Draws with multisampling even where the device has conservative rasterization, so tests run the fallback too.
    /// </summary>
    internal bool ForceMultisample { get; init; }

    /// <summary>Whether texels are drawn with conservative rasterization rather than multisampling.</summary>
    internal bool Conservative => !ForceMultisample && _context.ConservativeRasterization;

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
        var scene = _tracing.TopLevel(cmd, _traceScene.Collect(draws));
        RecordTexels(cmd, layout, draws, _pool, scene);
    }

    /// <summary>
    /// Records a build of the top-level structure over <paramref name="draws"/> that is held until
    /// <paramref name="release"/> is disposed, so a bake's later steps, in later command buffers, can trace it.
    /// </summary>
    internal TopLevelStructure BuildScene(CommandBuffer cmd, ReadOnlySpan<DrawItem> draws, out IDisposable release) =>
        _tracing.TopLevel(cmd, _traceScene.Collect(draws), out release);

    /// <summary>
    /// Records texel setup as <see cref="SetUpTexels"/> does, against <paramref name="scene"/>, with its descriptor
    /// sets from <paramref name="pool"/>, which needs room for <see cref="CreateJobPool"/>'s sets for setup.
    /// </summary>
    internal void RecordTexels(
        CommandBuffer cmd, LightmapLayout layout, ReadOnlySpan<DrawItem> draws, DescriptorPool pool, in TopLevelStructure scene)
    {
        var api = _context.Api;
        EnsurePages(layout.Pages);
        EnsureTexelPipeline();

        // Each page's static draws and their rectangles.
        var onPage = new List<(DrawItem Draw, LightmapRect Rect)>[layout.Pages];
        for (int i = 0; i < onPage.Length; i++)
        {
            onPage[i] = [];
        }

        // Each rectangle gets a number from 1, drawn into its texels for the filter. Draws that share a rectangle share it.
        _charts.Clear();
        foreach (ref readonly var draw in draws)
        {
            if (draw.BakeId != 0 && draw.IndexCount != 0 && layout.TryGet(draw.BakeId, out var rect))
            {
                onPage[rect.Page].Add((draw, rect));
                _charts.TryAdd(rect, _charts.Count + 1);
            }
        }

        for (int page = 0; page < layout.Pages; page++)
        {
            if (Conservative)
            {
                DrawConservative(cmd, _pages[page], onPage[page]);
            }
            else
            {
                DrawMultisampled(cmd, _pages[page], onPage[page], pool);
            }
        }

        _tracing.ReadyToTrace(cmd);
        var probe = new ProbeConstants
        {
            Rays = ProbeRays,
            RayLength = ProbeRayLength,
            InvalidShare = InvalidShare,
            Lift = ProbeLift,
        };
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _validityPipeline);
        var sets = stackalloc DescriptorSet[] { default, _renderer.Resources.Set };
        for (int page = 0; page < layout.Pages; page++)
        {
            var targets = _pages[page];
            api.Transition(cmd, targets.Validity.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.General,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit, AccessFlags2.None,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit);

            var set = AllocateSet(_validitySetLayout, pool);
            WriteValiditySet(set, scene, targets);
            sets[0] = set;
            api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _validityLayout, 0, 2, sets, 0, null);
            api.CmdPushConstants(cmd, _validityLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(ProbeConstants), &probe);
            const uint groups = (LightmapLayout.PageSize + GroupSize - 1) / GroupSize;
            api.CmdDispatch(cmd, groups, groups, 1);
        }

        // Whatever comes next, the later bake stages or a copy, reads what was written.
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        api.CmdPipelineBarrier2(cmd, &dependency);
    }

    /// <summary>Every texel of a page, <see cref="TexelFloats"/> floats each, row by row. Waits for the GPU; for tests.</summary>
    internal float[] ReadTexels(int page) => ReadTexels(page, 0, 0, LightmapLayout.PageSize, LightmapLayout.PageSize);

    /// <summary>
    /// The texels of a region of a page, <see cref="TexelFloats"/> floats each, row by row. Waits for the GPU, so only
    /// call it once <see cref="SetUpTexels"/>'s work has been submitted; for tests.
    /// </summary>
    internal float[] ReadTexels(int page, int x, int y, int width, int height)
    {
        if (page < 0 || page >= _pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, $"There are {_pages.Count} pages, so page {page} doesn't exist.");
        }

        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > LightmapLayout.PageSize || y + height > LightmapLayout.PageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), $"The region at ({x}, {y}) of {width} by {height} texels isn't inside the page.");
        }

        var targets = _pages[page];
        ulong count = (ulong)width * (ulong)height;
        ulong positionAt = 0;
        ulong geometricAt = Align(positionAt + count * 16);
        ulong shadingAt = Align(geometricAt + count * 8);
        ulong validityAt = Align(shadingAt + count * 8);
        using var buffer = GpuBuffer.CreateHostVisible(_context, validityAt + count, BufferUsageFlags.TransferDstBit);

        _context.SubmitAndWait(cmd =>
        {
            var api = _context.Api;
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllCommandsBit,
                SrcAccessMask = AccessFlags2.MemoryWriteBit,
                DstStageMask = PipelineStageFlags2.CopyBit,
                DstAccessMask = AccessFlags2.TransferReadBit,
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &barrier,
            };
            api.CmdPipelineBarrier2(cmd, &dependency);
            Copy(cmd, targets.Position, buffer, positionAt, x, y, width, height);
            Copy(cmd, targets.GeometricNormal, buffer, geometricAt, x, y, width, height);
            Copy(cmd, targets.ShadingNormal, buffer, shadingAt, x, y, width, height);
            Copy(cmd, targets.Validity, buffer, validityAt, x, y, width, height);
        });

        var texels = new float[count * TexelFloats];
        var bytes = (byte*)buffer.Mapped;
        var positions = (float*)(bytes + positionAt);
        var geometric = (Half*)(bytes + geometricAt);
        var shading = (Half*)(bytes + shadingAt);
        var validity = bytes + validityAt;
        for (ulong i = 0; i < count; i++)
        {
            ulong at = i * TexelFloats;
            for (ulong c = 0; c < 4; c++)
            {
                texels[at + c] = positions[i * 4 + c];
            }

            for (ulong c = 0; c < 3; c++)
            {
                texels[at + 4 + c] = (float)geometric[i * 4 + c];
                texels[at + 7 + c] = (float)shading[i * 4 + c];
            }

            texels[at + 10] = validity[i] / 255f;
        }

        return texels;
    }

    /// <summary>
    /// A descriptor pool of a <see cref="BakeJob"/>'s own, with room for every set the job allocates over a layout of
    /// <paramref name="pages"/> pages: texel setup's validity and resolve sets, the trace set and the filter's two sets
    /// a page, and one probe set. Each job has its own, so no two jobs share a set. Free it with
    /// <see cref="DestroyPool"/> once the GPU is done with the job.
    /// </summary>
    internal DescriptorPool CreateJobPool(int pages)
    {
        uint count = (uint)Math.Max(pages, 1);
        var pool = _context.CreateDescriptorPool(
            count * 5 + 1,
            new DescriptorPoolSize(DescriptorType.AccelerationStructureKhr, count * 2 + 1),
            new DescriptorPoolSize(DescriptorType.StorageBuffer, count * 3 + 3),
            new DescriptorPoolSize(DescriptorType.StorageImage, count * (3 + 3 + 6 + TransferSums + 18) + 2),
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, count * 4 + 1));
        JobPools++;
        return pool;
    }

    /// <summary>Frees a pool from <see cref="CreateJobPool"/>, and every set in it.</summary>
    internal void DestroyPool(DescriptorPool pool)
    {
        _context.Api.DestroyDescriptorPool(_context.Device, pool, null);
        JobPools--;
    }

    /// <summary>How many job pools are made and not yet freed: one for each job not yet disposed, for tests.</summary>
    internal int JobPools { get; private set; }

    /// <summary>
    /// Takes the baker's pages for the bake of <paramref name="name"/> until <see cref="ReleasePages"/>. The pages hold
    /// one bake's texels at a time, so a second bake can't set up while the first still reads them.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another bake holds the pages.</exception>
    internal void ClaimPages(object owner, string name)
    {
        if (_pagesClaimedBy is { } held && !ReferenceEquals(held.Owner, owner))
        {
            throw new InvalidOperationException(
                $"The baker's pages hold the bake of \"{held.Name}\", so the bake of \"{name}\" can't set up its texels until that one is done.");
        }

        _pagesClaimedBy = (owner, name);
    }

    /// <summary>Whether a bake holds the pages, so another can't set up yet.</summary>
    internal bool PagesClaimed => _pagesClaimedBy is not null;

    /// <summary>Gives the pages back, if <paramref name="owner"/> holds them.</summary>
    internal void ReleasePages(object owner)
    {
        if (_pagesClaimedBy is { } held && ReferenceEquals(held.Owner, owner))
        {
            _pagesClaimedBy = null;
        }
    }

    /// <summary>The probe volume <see cref="EnsureProbeVolume"/> made, or null before it has run.</summary>
    internal ProbeVolume? ProbeVolume => _probeVolume;

    /// <summary>
    /// The baker's probe volume, made the first time at the lowest quality, for its faces, its fold and its staging
    /// slots. Its atlases stay unused, since a bake keeps the fixed layout of <see cref="BakedProbeAtlas"/>, and the
    /// frame's own probes are left alone. Its size is the same for every space, since a bake folds its probes a few at
    /// a time through the volume's slots, so one made once serves every bake. Waits for the GPU the first time only.
    /// </summary>
    /// <exception cref="InvalidOperationException">The GPU lacks what the live probes fold with.</exception>
    internal ProbeVolume EnsureProbeVolume(ImageView sky, Sampler skySampler)
    {
        if (_probeVolume is { } made)
        {
            return made;
        }

        var volume = new ProbeVolume(_context, skySampler, sky);
        if (!volume.Supported)
        {
            volume.Dispose();
            throw new InvalidOperationException("Baking light probes needs the storage image formats and multiview the live probes fold with.");
        }

        volume.EnsureStorage(ProbeQuality.Low);
        _probeVolume = volume;
        return volume;
    }

    /// <summary>
    /// A region's sums, <see cref="SumFormat"/> and cleared by <see cref="ClearSums"/>: irradiance, direction and mask,
    /// then the five transfer sums, of the region's size for a chunk and 1 x 1, never written, for a space.
    /// </summary>
    internal RenderTarget[] CreateSums(int width, int height, bool transfer)
    {
        var extent = new Extent2D((uint)width, (uint)height);
        var transferExtent = transfer ? extent : new Extent2D(1, 1);
        const ImageUsageFlags usage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        var sums = new RenderTarget[3 + TransferSums];
        for (int i = 0; i < sums.Length; i++)
        {
            sums[i] = new RenderTarget(_context, SumFormat, i < 3 ? extent : transferExtent, usage, ImageAspectFlags.ColorBit);
        }

        return sums;
    }

    /// <summary>
    /// The probes' sum and their faces, <see cref="ProbesPerBatch"/> rows of six <see cref="ProbeVolume.FaceSize"/>
    /// faces each.
    /// </summary>
    internal (RenderTarget Sum, RenderTarget Faces) CreateProbeTargets()
    {
        var extent = ProbeExtent;
        const ImageUsageFlags usage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        return (
            new RenderTarget(_context, SumFormat, extent, usage, ImageAspectFlags.ColorBit),
            new RenderTarget(_context, GBuffer.HdrFormat, extent, usage, ImageAspectFlags.ColorBit));
    }

    /// <summary>The size of a batch of probes' faces.</summary>
    internal static Extent2D ProbeExtent => new(6 * ProbeVolume.FaceSize, ProbeVolume.FaceSize * ProbesPerBatch);

    /// <summary>A trace set from <paramref name="pool"/> for one page, written for the scene, lights, sums and sky.</summary>
    internal DescriptorSet CreateTraceSet(
        DescriptorPool pool, int page, in TopLevelStructure scene, GpuBuffer lights, RenderTarget[] sums, ImageView sky,
        Sampler skySampler)
    {
        var set = AllocateSet(_traceSetLayout, pool);
        WriteTraceSet(set, scene, _pages[page], lights, sums, sky, skySampler);
        return set;
    }

    /// <summary>The probe set from <paramref name="pool"/>, written for the scene, lights, probes, targets and sky.</summary>
    internal DescriptorSet CreateProbeSet(
        DescriptorPool pool, in TopLevelStructure scene, GpuBuffer lights, GpuBuffer positions, RenderTarget sum,
        RenderTarget faces, ImageView sky, Sampler skySampler)
    {
        var set = AllocateSet(_probeSetLayout, pool);
        WriteProbeSet(set, scene, lights, positions, sum, faces, sky, skySampler);
        return set;
    }

    /// <summary>Records <paramref name="count"/> probe passes from <paramref name="first"/>, each waiting for the last's sum.</summary>
    internal void RecordProbePasses(CommandBuffer cmd, DescriptorSet set, ProbeTraceConstants constants, int first, int count, Extent2D extent)
    {
        var api = _context.Api;
        _tracing.ReadyToTrace(cmd);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _probePipeline);
        var sets = stackalloc DescriptorSet[] { set, _renderer.Resources.Set };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _probeLayout, 0, 2, sets, 0, null);
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        uint groupsX = (extent.Width + GroupSize - 1) / GroupSize;
        uint groupsY = (extent.Height + GroupSize - 1) / GroupSize;
        for (int pass = first; pass < first + count; pass++)
        {
            constants.Pass = (uint)pass;
            api.CmdPushConstants(cmd, _probeLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(ProbeTraceConstants), &constants);
            api.CmdDispatch(cmd, groupsX, groupsY, 1);
            api.CmdPipelineBarrier2(cmd, &dependency);
        }
    }

    /// <summary>
    /// Copies the six faces of <paramref name="count"/> of the batch's probe rows from <paramref name="firstRow"/> into
    /// the volume's faces, one slot each, ready for the fold.
    /// </summary>
    internal void RecordCopyFaces(CommandBuffer cmd, RenderTarget faces, int firstRow, int count, ProbeVolume volume)
    {
        var api = _context.Api;
        uint layers = (uint)(count * 6);
        api.Transition(cmd, volume.FacesImage, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
            layerCount: layers);
        const uint size = ProbeVolume.FaceSize;
        var regions = stackalloc ImageCopy[6];
        for (int slot = 0; slot < count; slot++)
        {
            for (int f = 0; f < 6; f++)
            {
                regions[f] = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    SrcOffset = new Offset3D(f * (int)size, (firstRow + slot) * (int)size, 0),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, (uint)(slot * 6 + f), 1),
                    Extent = new Extent3D(size, size, 1),
                };
            }

            api.CmdCopyImage(cmd, faces.Image, ImageLayout.General, volume.FacesImage, ImageLayout.TransferDstOptimal, 6, regions);
        }

        api.Transition(cmd, volume.FacesImage, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit,
            layerCount: layers);
    }

    /// <summary>The texels a page's rectangles cover, with <see cref="TraceMargin"/> round them, clamped to the page.</summary>
    internal static (int X, int Y, int Width, int Height) UsedRegion(LightmapLayout layout, int page)
    {
        const int side = LightmapLayout.PageSize;
        int left = side, top = side, right = 0, bottom = 0;
        foreach (var (_, rect) in layout.Draws)
        {
            if (rect.Page != page)
            {
                continue;
            }

            left = Math.Min(left, (int)MathF.Floor(rect.Offset.X * side) - TraceMargin);
            top = Math.Min(top, (int)MathF.Floor(rect.Offset.Y * side) - TraceMargin);
            right = Math.Max(right, (int)MathF.Ceiling((rect.Offset.X + rect.Scale.X) * side) + TraceMargin);
            bottom = Math.Max(bottom, (int)MathF.Ceiling((rect.Offset.Y + rect.Scale.Y) * side) + TraceMargin);
        }

        left = Math.Max(left, 0);
        top = Math.Max(top, 0);
        right = Math.Clamp(right, left + 1, side);
        bottom = Math.Clamp(bottom, top + 1, side);
        return (left, top, right - left, bottom - top);
    }

    internal void ClearSums(CommandBuffer cmd, RenderTarget[] sums)
    {
        var api = _context.Api;
        var clear = new ClearColorValue(0f, 0f, 0f, 0f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        foreach (var sum in sums)
        {
            api.Transition(cmd, sum.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.General,
                PipelineStageFlags2.None, AccessFlags2.None,
                PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit);
            api.CmdClearColorImage(cmd, sum.Image, ImageLayout.General, &clear, 1, &range);
            api.Transition(cmd, sum.Image, ImageAspectFlags.ColorBit,
                ImageLayout.General, ImageLayout.General,
                PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        }
    }

    /// <summary>Records <paramref name="count"/> passes from <paramref name="first"/>, each waiting for the last's sums.</summary>
    internal void RecordPasses(CommandBuffer cmd, DescriptorSet set, TraceConstants constants, int first, int count, int width, int height)
    {
        var api = _context.Api;
        _tracing.ReadyToTrace(cmd);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _tracePipeline);
        var sets = stackalloc DescriptorSet[] { set, _renderer.Resources.Set };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _traceLayout, 0, 2, sets, 0, null);
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        uint groupsX = ((uint)width + GroupSize - 1) / GroupSize;
        uint groupsY = ((uint)height + GroupSize - 1) / GroupSize;
        for (int pass = first; pass < first + count; pass++)
        {
            constants.Pass = (uint)pass;
            api.CmdPushConstants(cmd, _traceLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(TraceConstants), &constants);
            api.CmdDispatch(cmd, groupsX, groupsY, 1);
            api.CmdPipelineBarrier2(cmd, &dependency);
        }
    }

    /// <summary>
    /// The bytes <see cref="RecordSumsReadBack"/> copies for a region: its sums, then its texels' positions and validity.
    /// </summary>
    internal static ulong SumsReadBackBytes(int width, int height, bool transfer)
    {
        var (_, validityAt) = TexelOffsets(width, height, transfer);
        return validityAt + (ulong)width * (ulong)height;
    }

    // Where the positions and validity start in a sums readback, after the sums it reads.
    private static (ulong PositionAt, ulong ValidityAt) TexelOffsets(int width, int height, bool transfer)
    {
        ulong count = (ulong)width * (ulong)height;
        ulong positionAt = Align(count * 16 * (ulong)SumsRead(transfer));
        return (positionAt, Align(positionAt + count * 16));
    }

    // The sums a readback copies: the transfer's only for a chunk.
    private static int SumsRead(bool transfer) => transfer ? 3 + TransferSums : 3;

    /// <summary>
    /// Records copies of a page region's sums, and its texels' positions and validity, into <paramref name="buffer"/>,
    /// of <see cref="SumsReadBackBytes"/> at least. Read it with <see cref="ReadSumsBack"/> once the GPU is done.
    /// </summary>
    internal void RecordSumsReadBack(
        CommandBuffer cmd, int page, RenderTarget[] sums, GpuBuffer buffer, int x, int y, int width, int height, bool transfer)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.CopyBit,
            DstAccessMask = AccessFlags2.TransferReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);

        ulong bytes = (ulong)width * (ulong)height * 16;
        int read = SumsRead(transfer);
        for (int i = 0; i < read; i++)
        {
            Copy(cmd, sums[i], buffer, bytes * (ulong)i, 0, 0, width, height);
        }

        var (positionAt, validityAt) = TexelOffsets(width, height, transfer);
        Copy(cmd, _pages[page].Position, buffer, positionAt, x, y, width, height);
        Copy(cmd, _pages[page].Validity, buffer, validityAt, x, y, width, height);
    }

    /// <summary>
    /// A page region as <see cref="RecordSumsReadBack"/> copied it: the sums divided by the passes, and which texels
    /// are covered and valid.
    /// </summary>
    internal static TracedPage ReadSumsBack(
        GpuBuffer buffer, int page, int x, int y, int width, int height, int passes, bool transfer)
    {
        int count = width * height;
        int read = SumsRead(transfer);
        var floats = new ReadOnlySpan<float>(buffer.Mapped, count * 4 * read);
        var irradiance = new float[count * 4];
        var direction = new float[count * 4];
        var mask = new float[count * 4];
        float scale = 1f / passes;
        for (int i = 0; i < count * 4; i++)
        {
            irradiance[i] = floats[i] * scale;
            direction[i] = floats[count * 4 + i]; // Only its own direction matters, not its scale.
            mask[i] = floats[count * 8 + i] * scale;
        }

        // Irradiance w held the mean squared luminance; it becomes the variance of the mean luminance, which tells the
        // filter how much of the difference between two texels is noise.
        for (int i = 0; i < count; i++)
        {
            float mean = BakeEncoding.Luminance(irradiance[i * 4], irradiance[i * 4 + 1], irradiance[i * 4 + 2]);
            irradiance[i * 4 + 3] = MathF.Max(irradiance[i * 4 + 3] - mean * mean, 0f) * scale;
        }

        // Covered where the position's w names a rectangle, valid where the validity pass wrote 1.
        var (positionAt, validityAt) = TexelOffsets(width, height, transfer);
        var bytes = (byte*)buffer.Mapped;
        var positions = (float*)(bytes + positionAt);
        var validity = bytes + validityAt;
        var valid = new bool[count];
        var covered = new bool[count];
        for (int i = 0; i < count; i++)
        {
            covered[i] = positions[i * 4 + 3] > 0.5f;
            valid[i] = validity[i] / 255f > 0.5f;
        }

        float[]? transferMeans = null;
        if (transfer)
        {
            // Interleaved a texel at a time, each sum's four floats in turn, as TracedPage.Transfer holds them.
            transferMeans = new float[count * TransferFloats];
            for (int t = 0; t < count; t++)
            {
                for (int s = 0; s < TransferSums; s++)
                {
                    int from = (3 + s) * count * 4 + t * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        transferMeans[t * TransferFloats + s * 4 + c] = floats[from + c] * scale;
                    }
                }
            }
        }

        return new TracedPage(page, x, y, width, height, irradiance, direction, mask, covered, valid, valid)
        {
            Transfer = transferMeans,
        };
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
        if (traced.Page < 0 || traced.Page >= _pages.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(traced), traced.Page, $"There are {_pages.Count} pages set up, so page {traced.Page} doesn't exist.");
        }

        _context.Api.ResetDescriptorPool(_context.Device, Filtering.Pool, 0).Check();
        using var run = CreateFilter(Filtering.Pool, traced);
        run.Fill(traced.Irradiance, traced.Direction, traced.Mask);
        _context.SubmitAndWait(cmd => Filtering.Record(cmd, run, true, smooth));
        return BakeEncoding.Filtered(traced, run);
    }

    /// <summary>The filter and dilation every page's light, and a chunk's transfer, goes through.</summary>
    internal BakeFilter Filtering { get; }

    /// <summary>
    /// A filter run's images and sets for a page's region, from <paramref name="pool"/>: two copies of its irradiance,
    /// direction and mask, one set each way between them, and the host-visible buffer they're uploaded from and read
    /// back into. One run uses it at a time; a chunk's page runs its light and both transfer runs through the same.
    /// </summary>
    internal FilterRun CreateFilter(DescriptorPool pool, TracedPage traced) =>
        CreateFilter(pool, traced.Page, traced.X, traced.Y, traced.Width, traced.Height);

    /// <summary>A filter run as the other overload makes it, for a region before its traced page is read back.</summary>
    internal FilterRun CreateFilter(DescriptorPool pool, int page, int x, int y, int width, int height)
    {
        var run = new FilterRun(_context, page, (x, y, width, height));
        var targets = _pages[page];
        Filtering.Bind(pool, run, targets.Position.View, targets.GeometricNormal.View, targets.Validity.View);
        return run;
    }

    public void Dispose()
    {
        var api = _context.Api;
        foreach (var page in _pages)
        {
            page.Dispose();
        }

        _tile?.Dispose();
        _texelPipeline?.Destroy();
        _validityPipeline.Destroy();
        _resolvePipeline.Destroy();
        _tracePipeline.Destroy();
        api.DestroyPipelineLayout(_context.Device, _traceLayout, null);
        api.DestroyDescriptorSetLayout(_context.Device, _traceSetLayout, null);
        _probePipeline.Destroy();
        api.DestroyPipelineLayout(_context.Device, _probeLayout, null);
        api.DestroyDescriptorSetLayout(_context.Device, _probeSetLayout, null);
        Filtering.Dispose();
        api.DestroyPipelineLayout(_context.Device, _texelLayout, null);
        api.DestroyPipelineLayout(_context.Device, _validityLayout, null);
        api.DestroyPipelineLayout(_context.Device, _resolveLayout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _validitySetLayout, null);
        api.DestroyDescriptorSetLayout(_context.Device, _resolveSetLayout, null);
        _probeVolume?.Dispose();
        api.DestroySampler(_context.Device, _sampler, null);
    }

    /// <summary>Draws a page's draws straight into it: every texel a triangle touches is covered.</summary>
    private void DrawConservative(CommandBuffer cmd, PageTargets targets, List<(DrawItem Draw, LightmapRect Rect)> draws)
    {
        var api = _context.Api;
        foreach (var target in targets.Colors)
        {
            api.Transition(cmd, target.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit, AccessFlags2.None,
                PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit);
        }

        var extent = new Extent2D(LightmapLayout.PageSize, LightmapLayout.PageSize);
        DrawInto(cmd, targets.Colors, extent, default, draws);

        foreach (var target in targets.Colors)
        {
            api.Transition(cmd, target.Image, ImageAspectFlags.ColorBit,
                ImageLayout.ColorAttachmentOptimal, ImageLayout.General,
                PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit);
        }
    }

    /// <summary>
    /// Draws a page tile by tile into multisampled targets, then resolves each tile into the page, each texel taking a
    /// covered sample, so a triangle that misses a texel's centre but covers any sample still covers it.
    /// </summary>
    private void DrawMultisampled(
        CommandBuffer cmd, PageTargets targets, List<(DrawItem Draw, LightmapRect Rect)> draws, DescriptorPool pool)
    {
        var api = _context.Api;
        _tile ??= new TileTargets(_context, MultisampleCount());
        var tile = _tile;

        // Tiles nothing is drawn in stay cleared to uncovered.
        var clear = new ClearColorValue(0f, 0f, 0f, 0f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        foreach (var target in targets.Colors)
        {
            api.Transition(cmd, target.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit, AccessFlags2.None,
                PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit);
            api.CmdClearColorImage(cmd, target.Image, ImageLayout.TransferDstOptimal, &clear, 1, &range);
            api.Transition(cmd, target.Image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferDstOptimal, ImageLayout.General,
                PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit);
        }

        var set = AllocateSet(_resolveSetLayout, pool);
        WriteResolveSet(set, tile, targets);

        const int tiles = LightmapLayout.PageSize / TileSize;
        var tileDraws = new List<(DrawItem Draw, LightmapRect Rect)>();
        for (int tileY = 0; tileY < tiles; tileY++)
        {
            for (int tileX = 0; tileX < tiles; tileX++)
            {
                var origin = new Offset2D(tileX * TileSize, tileY * TileSize);
                tileDraws.Clear();
                foreach (var entry in draws)
                {
                    if (Touches(entry.Rect, origin))
                    {
                        tileDraws.Add(entry);
                    }
                }

                if (tileDraws.Count == 0)
                {
                    continue;
                }

                // The last tile's resolve must finish reading before this one draws over it.
                foreach (var target in tile.Colors)
                {
                    api.Transition(cmd, target.Image, ImageAspectFlags.ColorBit,
                        ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                        PipelineStageFlags2.ComputeShaderBit, AccessFlags2.None,
                        PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit);
                }

                DrawInto(cmd, tile.Colors, new Extent2D(TileSize, TileSize), origin, tileDraws);

                foreach (var target in tile.Colors)
                {
                    api.Transition(cmd, target.Image, ImageAspectFlags.ColorBit,
                        ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
                        PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
                        PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit);
                }

                var constants = new ResolveConstants { X = origin.X, Y = origin.Y, Samples = (int)tile.SampleCount };
                api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _resolvePipeline);
                api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _resolveLayout, 0, 1, &set, 0, null);
                api.CmdPushConstants(cmd, _resolveLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(ResolveConstants), &constants);
                const uint groups = TileSize / GroupSize;
                api.CmdDispatch(cmd, groups, groups, 1);
            }
        }

        // The validity pass reads the position and geometric normal the resolves wrote.
        foreach (var target in targets.Colors)
        {
            api.Transition(cmd, target.Image, ImageAspectFlags.ColorBit,
                ImageLayout.General, ImageLayout.General,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit);
        }
    }

    /// <summary>
    /// Draws into the three colour targets given, cleared first. They cover <paramref name="extent"/> of the page from
    /// <paramref name="origin"/>; the viewport still spans the whole page, so a rectangle lands on the same texels
    /// whichever tile draws it.
    /// </summary>
    private void DrawInto(
        CommandBuffer cmd, RenderTarget[] targets, Extent2D extent, Offset2D origin, List<(DrawItem Draw, LightmapRect Rect)> draws)
    {
        var api = _context.Api;
        var attachments = stackalloc RenderingAttachmentInfo[3];
        for (int i = 0; i < 3; i++)
        {
            attachments[i] = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = targets[i].View,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
            };
        }

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, extent),
            LayerCount = 1,
            ColorAttachmentCount = 3,
            PColorAttachments = attachments,
        };
        api.CmdBeginRendering(cmd, &rendering);

        var viewport = new Viewport(-origin.X, -origin.Y, LightmapLayout.PageSize, LightmapLayout.PageSize, 0, 1);
        var scissor = new Rect2D(default, extent);
        api.CmdSetViewport(cmd, 0, 1, &viewport);
        api.CmdSetScissor(cmd, 0, 1, &scissor);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _texelPipeline!);

        foreach (var (draw, rect) in draws)
        {
            var mesh = _renderer.MeshFor(draw.Mesh);
            var vertexBuffer = mesh.Vertices.Handle;
            ulong offset = 0;
            api.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
            api.CmdBindIndexBuffer(cmd, mesh.Indices.Handle, 0, IndexType.Uint32);
            var constants = new TexelConstants
            {
                Model = draw.Transform,
                Rect = new Vector4(rect.Scale, rect.Offset.X, rect.Offset.Y),
                Chart = _charts[rect],
            };
            api.CmdPushConstants(cmd, _texelLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(TexelConstants), &constants);
            api.CmdDrawIndexed(cmd, draw.IndexCount, 1, draw.FirstIndex, 0, 0);
        }

        api.CmdEndRendering(cmd);
    }

    /// <summary>Whether a rectangle, with a texel round it for conservative overestimation, reaches into a tile.</summary>
    private static bool Touches(LightmapRect rect, Offset2D tile)
    {
        float left = rect.Offset.X * LightmapLayout.PageSize - 1;
        float top = rect.Offset.Y * LightmapLayout.PageSize - 1;
        float right = (rect.Offset.X + rect.Scale.X) * LightmapLayout.PageSize + 1;
        float bottom = (rect.Offset.Y + rect.Scale.Y) * LightmapLayout.PageSize + 1;
        return right > tile.X && left < tile.X + TileSize && bottom > tile.Y && top < tile.Y + TileSize;
    }

    private void EnsurePages(int count)
    {
        while (_pages.Count < count)
        {
            _pages.Add(new PageTargets(_context));
        }
    }

    private void EnsureTexelPipeline()
    {
        bool conservative = Conservative;
        if (_texelPipeline is not null && _texelPipelineConservative == conservative)
        {
            return;
        }

        _texelPipeline?.Destroy();

        // Only position, normal and the second UVs: attributes 0, 1 and 6 of the scene's vertex layout.
        var all = SceneDraws.VertexAttributes;
        VertexInputAttributeDescription[] attributes = [all[0], all[1], all[6]];
        Format[] formats = [PositionFormat, NormalFormat, NormalFormat];
        _texelPipeline = PipelineFactory.Create(_context, new PipelineDescription
        {
            VertexShader = "bake_texels.vert",
            FragmentShader = "bake_texels.frag",
            Layout = _texelLayout,
            ColorFormats = formats,
            VertexBindings = SceneDraws.VertexBindings,
            VertexAttributes = attributes,
            CullMode = CullModeFlags.None,
            Samples = conservative ? SampleCountFlags.Count1Bit : MultisampleCount(),
            Conservative = conservative,
        });
        _texelPipelineConservative = conservative;
    }

    /// <summary>Eight samples, or the most below that the device can draw and sample.</summary>
    private SampleCountFlags MultisampleCount()
    {
        _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, out var properties);
        var supported = properties.Limits.FramebufferColorSampleCounts & properties.Limits.SampledImageColorSampleCounts;
        foreach (var count in (ReadOnlySpan<SampleCountFlags>)[SampleCountFlags.Count8Bit, SampleCountFlags.Count4Bit, SampleCountFlags.Count2Bit])
        {
            if ((supported & count) != 0)
            {
                return count;
            }
        }

        throw new InvalidOperationException("Baking without conservative rasterization needs multisampling, which this GPU lacks.");
    }

    private DescriptorSet AllocateSet(DescriptorSetLayout layout) => AllocateSet(layout, _pool);

    private DescriptorSet AllocateSet(DescriptorSetLayout layout, DescriptorPool pool) => _context.AllocateSet(pool, layout);

    private void WriteValiditySet(DescriptorSet set, in TopLevelStructure scene, PageTargets targets)
    {
        var handle = scene.Handle;
        var structureInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &handle,
        };
        var tableInfo = new DescriptorBufferInfo(scene.GeometryTable.Handle, 0, Vk.WholeSize);
        var images = stackalloc DescriptorImageInfo[]
        {
            new(default, targets.Position.View, ImageLayout.General),
            new(default, targets.GeometricNormal.View, ImageLayout.General),
            new(default, targets.Validity.View, ImageLayout.General),
        };
        var writes = stackalloc WriteDescriptorSet[5];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            PNext = &structureInfo,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &tableInfo,
        };
        for (int i = 0; i < 3; i++)
        {
            writes[2 + i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = (uint)(2 + i),
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &images[i],
            };
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 5, writes, 0, null);
    }

    private void WriteTraceSet(
        DescriptorSet set, in TopLevelStructure scene, PageTargets targets, GpuBuffer lights, RenderTarget[] sums, ImageView sky,
        Sampler skySampler)
    {
        var handle = scene.Handle;
        var structureInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &handle,
        };
        var buffers = stackalloc DescriptorBufferInfo[]
        {
            new(scene.GeometryTable.Handle, 0, Vk.WholeSize),
            new(lights.Handle, 0, Vk.WholeSize),
        };
        var images = stackalloc DescriptorImageInfo[]
        {
            new(default, targets.Position.View, ImageLayout.General),
            new(default, targets.GeometricNormal.View, ImageLayout.General),
            new(default, targets.Validity.View, ImageLayout.General),
            new(default, sums[0].View, ImageLayout.General),
            new(default, sums[1].View, ImageLayout.General),
            new(default, sums[2].View, ImageLayout.General),
            new(skySampler, sky, ImageLayout.ShaderReadOnlyOptimal),
            new(default, sums[3].View, ImageLayout.General),
            new(default, sums[4].View, ImageLayout.General),
            new(default, sums[5].View, ImageLayout.General),
            new(default, sums[6].View, ImageLayout.General),
            new(default, sums[7].View, ImageLayout.General),
        };
        var writes = stackalloc WriteDescriptorSet[15];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            PNext = &structureInfo,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
        };

        // Bindings 1 and 5 are buffers, 2 to 4 and 6 to 8 storage images, 9 the sky, and 10 to 14 the transfer sums.
        ReadOnlySpan<int> imageOf = [-1, -1, 0, 1, 2, -1, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        for (uint binding = 1; binding < 15; binding++)
        {
            bool buffer = binding is 1 or 5;
            writes[binding] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = binding,
                DescriptorCount = 1,
                DescriptorType = buffer ? DescriptorType.StorageBuffer
                    : binding == 9 ? DescriptorType.CombinedImageSampler : DescriptorType.StorageImage,
                PBufferInfo = buffer ? &buffers[binding == 1 ? 0 : 1] : null,
                PImageInfo = buffer ? null : &images[imageOf[(int)binding]],
            };
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 15, writes, 0, null);
    }

    private void WriteProbeSet(
        DescriptorSet set, in TopLevelStructure scene, GpuBuffer lights, GpuBuffer positions, RenderTarget sum, RenderTarget faces,
        ImageView sky,
        Sampler skySampler)
    {
        var handle = scene.Handle;
        var structureInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &handle,
        };
        var buffers = stackalloc DescriptorBufferInfo[]
        {
            new(scene.GeometryTable.Handle, 0, Vk.WholeSize),
            new(lights.Handle, 0, Vk.WholeSize),
            new(positions.Handle, 0, Vk.WholeSize),
        };
        var images = stackalloc DescriptorImageInfo[]
        {
            new(default, sum.View, ImageLayout.General),
            new(default, faces.View, ImageLayout.General),
            new(skySampler, sky, ImageLayout.ShaderReadOnlyOptimal),
        };
        var writes = stackalloc WriteDescriptorSet[7];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            PNext = &structureInfo,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
        };

        // Bindings 1 to 3 are buffers, 4 and 5 storage images, and 6 the sky.
        for (uint binding = 1; binding < 7; binding++)
        {
            bool buffer = binding <= 3;
            writes[binding] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = binding,
                DescriptorCount = 1,
                DescriptorType = buffer ? DescriptorType.StorageBuffer
                    : binding == 6 ? DescriptorType.CombinedImageSampler : DescriptorType.StorageImage,
                PBufferInfo = buffer ? &buffers[binding - 1] : null,
                PImageInfo = buffer ? null : &images[binding - 4],
            };
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 7, writes, 0, null);
    }

    private void WriteResolveSet(DescriptorSet set, TileTargets tile, PageTargets targets)
    {
        var images = stackalloc DescriptorImageInfo[6];
        var writes = stackalloc WriteDescriptorSet[6];
        for (int i = 0; i < 3; i++)
        {
            images[i] = new DescriptorImageInfo(_sampler, tile.Colors[i].View, ImageLayout.ShaderReadOnlyOptimal);
            images[3 + i] = new DescriptorImageInfo(default, targets.Colors[i].View, ImageLayout.General);
        }

        for (int i = 0; i < 6; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = i < 3 ? DescriptorType.CombinedImageSampler : DescriptorType.StorageImage,
                PImageInfo = &images[i],
            };
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 6, writes, 0, null);
    }

    private DescriptorSetLayout CreateSetLayout(DescriptorSetLayoutBinding* bindings, uint count) =>
        _context.CreateSetLayout(new ReadOnlySpan<DescriptorSetLayoutBinding>(bindings, (int)count));

    private PipelineLayout CreatePipelineLayout(DescriptorSetLayout[] setLayouts, ShaderStageFlags pushStages, uint pushSize) =>
        _context.CreatePipelineLayout(setLayouts, new PushConstantRange(pushStages, 0, pushSize));

    private void Copy(CommandBuffer cmd, RenderTarget target, GpuBuffer buffer, ulong offset, int x, int y, int width, int height)
    {
        var region = new BufferImageCopy
        {
            BufferOffset = offset,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(x, y, 0),
            ImageExtent = new Extent3D((uint)width, (uint)height, 1),
        };
        _context.Api.CmdCopyImageToBuffer(cmd, target.Image, ImageLayout.General, buffer.Handle, 1, &region);
    }

    private static ulong Align(ulong offset) => (offset + 15) & ~15ul;

    /// <summary>A page's position, geometric normal, shading normal and validity.</summary>
    private sealed class PageTargets : IDisposable
    {
        public PageTargets(VulkanContext context)
        {
            var extent = new Extent2D(LightmapLayout.PageSize, LightmapLayout.PageSize);
            const ImageUsageFlags usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.StorageBit
                | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
            Position = new RenderTarget(context, PositionFormat, extent, usage, ImageAspectFlags.ColorBit);
            GeometricNormal = new RenderTarget(context, NormalFormat, extent, usage, ImageAspectFlags.ColorBit);
            ShadingNormal = new RenderTarget(context, NormalFormat, extent, usage, ImageAspectFlags.ColorBit);
            Validity = new RenderTarget(context, ValidityFormat, extent,
                ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit, ImageAspectFlags.ColorBit);
            Colors = [Position, GeometricNormal, ShadingNormal];
        }

        /// <summary>World position, w the number (from 1) of the rectangle covering the texel and 0 elsewhere.</summary>
        public RenderTarget Position { get; }

        /// <summary>The normal of the triangle itself, unit length.</summary>
        public RenderTarget GeometricNormal { get; }

        /// <summary>The vertex normals blended, unit length.</summary>
        public RenderTarget ShadingNormal { get; }

        /// <summary>1 for a covered texel outside walls, 0 otherwise.</summary>
        public RenderTarget Validity { get; }

        /// <summary>The three drawn into, in the fragment shader's order.</summary>
        public RenderTarget[] Colors { get; }

        public void Dispose()
        {
            Position.Dispose();
            GeometricNormal.Dispose();
            ShadingNormal.Dispose();
            Validity.Dispose();
        }
    }

    /// <summary>The multisampled targets one tile of a page is drawn into without conservative rasterization.</summary>
    private sealed class TileTargets : IDisposable
    {
        public TileTargets(VulkanContext context, SampleCountFlags samples)
        {
            SampleCount = (uint)samples;
            var extent = new Extent2D(TileSize, TileSize);
            const ImageUsageFlags usage = ImageUsageFlags.ColorAttachmentBit;
            Colors =
            [
                new RenderTarget(context, PositionFormat, extent, usage, ImageAspectFlags.ColorBit, samples: samples),
                new RenderTarget(context, NormalFormat, extent, usage, ImageAspectFlags.ColorBit, samples: samples),
                new RenderTarget(context, NormalFormat, extent, usage, ImageAspectFlags.ColorBit, samples: samples),
            ];
        }

        public uint SampleCount { get; }

        public RenderTarget[] Colors { get; }

        public void Dispose()
        {
            foreach (var target in Colors)
            {
                target.Dispose();
            }
        }
    }

    // Must match the push constants in bake_texels.vert.
    [StructLayout(LayoutKind.Sequential)]
    private struct TexelConstants
    {
        public Matrix4x4 Model;
        public Vector4 Rect;
        public float Chart;
    }

    // Must match the push constants in bake_validity.comp.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeConstants
    {
        public uint Rays;
        public float RayLength;
        public float InvalidShare;
        public float Lift;
    }

    // Must match the push constants in bake_trace.comp.
    [StructLayout(LayoutKind.Sequential)]
    internal struct TraceConstants
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Pass;
        public uint LightCount;
        public float Lift;
        public float SkyIntensity;
        public uint Chunk;
    }

    // Must match the push constants in bake_probes.comp.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProbeTraceConstants
    {
        public uint FirstProbe;
        public uint ProbeCount;
        public uint Pass;
        public uint Passes;
        public uint LightCount;
        public float Lift;
        public float SkyIntensity;
        public float MaxDistance;
    }

    // Must match the push constants in bake_resolve.comp.
    [StructLayout(LayoutKind.Sequential)]
    private struct ResolveConstants
    {
        public int X;
        public int Y;
        public int Samples;
    }
}

/// <summary>
/// One light as the path tracer samples it. Must match BakeLight in Shaders/bake_lights.glsl. Make one with
/// <see cref="Sun"/> or <see cref="Local"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuBakeLight
{
    // A spot's fade divisor never goes below this, as in the runtime's light data.
    private const float MinFade = 1e-4f;

    public Vector4 PositionRange;
    public Vector4 RadianceRadius;
    public Vector4 DirectionCone;
    public Vector4 FadeKind;

    /// <summary>The shadow mask channel the light is measured in, or -1 for none, which both makers start with.</summary>
    public int MaskChannel
    {
        readonly get => (int)FadeKind.W;
        set => FadeKind.W = value;
    }

    /// <summary>A sun, its disc <paramref name="lightAngle"/> radians across (<see cref="ShadowSettings.LightAngle"/>).</summary>
    public static GpuBakeLight Sun(in DirectionalLight sun, float lightAngle) => new()
    {
        PositionRange = new Vector4(Vector3.Normalize(sun.Direction), 0f),
        RadianceRadius = new Vector4(sun.Radiance, MathF.Cos(lightAngle * 0.5f)),
        DirectionCone = new Vector4(0f, 0f, 0f, -2f),
        FadeKind = new Vector4(0f, 1f, sun.Mode == LightMode.Mixed ? 1f : 0f, -1f),
    };

    /// <summary>A point or spot light, dimming as the runtime dims it.</summary>
    public static GpuBakeLight Local(in LocalLight light)
    {
        float cosOuter = -2f;
        float fade = 0f;
        var direction = Vector3.Zero;
        if (light.IsSpot)
        {
            float outer = light.OuterAngle;
            float cosInner = MathF.Cos(MathF.Min(light.InnerAngle, outer));
            cosOuter = MathF.Cos(outer);
            fade = 1f / MathF.Max(cosInner - cosOuter, MinFade);
            direction = Vector3.Normalize(light.Direction);
        }

        return new GpuBakeLight
        {
            PositionRange = new Vector4(light.Position, light.Range),
            RadianceRadius = new Vector4(light.Radiance, Passes.LocalLights.RadiusOf(light)),
            DirectionCone = new Vector4(direction, cosOuter),
            FadeKind = new Vector4(fade, 0f, light.Mode == LightMode.Mixed ? 1f : 0f, -1f),
        };
    }
}

/// <summary>
/// What <see cref="Baker.ReadSumsBack"/> found for one page, over the region (<see cref="X"/>, <see cref="Y"/>,
/// <see cref="Width"/>, <see cref="Height"/>) its rectangles cover. Each array runs row by row over the region. The
/// irradiance and mask are means over the passes, four floats a texel, with the variance of the irradiance's mean
/// luminance in irradiance w; the direction is the luminance-weighted sum of directions in xyz and the weight in w.
/// After <see cref="Baker.FilterAndDilate"/> irradiance w is 1 where a texel holds light and the direction is a mean
/// direction with w 1. <see cref="Filled"/> marks the texels that hold light: the valid ones as traced,
/// and also the ones dilation filled after <see cref="Baker.FilterAndDilate"/>.
/// </summary>
internal sealed record TracedPage(
    int Page, int X, int Y, int Width, int Height, float[] Irradiance, float[] Direction, float[] Mask, bool[] Covered,
    bool[] Valid, bool[] Filled)
{
    /// <summary>
    /// Only for a chunk: the means of its transfer, <see cref="Baker.TransferFloats"/> floats a texel. The sky's four
    /// coefficients for red, green and blue, the sun's four, then the sun's colour in rgb with w unused.
    /// </summary>
    public float[]? Transfer { get; init; }
}
