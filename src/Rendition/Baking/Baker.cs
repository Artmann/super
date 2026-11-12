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

    /// <summary>À-trous passes over the irradiance and direction, texels 1, 2, 4 and 8 apart.</summary>
    internal const int FilterPasses = 4;

    /// <summary>Rings of dilation after the filter, each filling empty texels beside full ones.</summary>
    internal const int DilationRings = 8;

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
    private readonly Pipeline _validityPipeline;
    private readonly Pipeline _resolvePipeline;
    private readonly Sampler _sampler;
    private readonly List<PageTargets> _pages = [];
    private readonly DescriptorSetLayout _traceSetLayout;
    private readonly DescriptorPool _tracePool;
    private readonly PipelineLayout _traceLayout;
    private readonly Pipeline _tracePipeline;
    private readonly DescriptorSetLayout _probeSetLayout;
    private readonly DescriptorPool _probePool;
    private readonly PipelineLayout _probeLayout;
    private readonly Pipeline _probePipeline;
    private readonly DescriptorSetLayout _filterSetLayout;
    private readonly DescriptorPool _filterPool;
    private readonly PipelineLayout _filterLayout;
    private readonly Pipeline _filterPipeline;
    private readonly Pipeline _dilatePipeline;
    private readonly Dictionary<LightmapRect, int> _charts = [];
    private TopLevelStructure _scene;
    private Pipeline _texelPipeline;
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
        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.AccelerationStructureKhr, pages),
            new(DescriptorType.StorageBuffer, pages),
            new(DescriptorType.StorageImage, pages * 6),
            new(DescriptorType.CombinedImageSampler, pages * 3),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = pages * 2,
            PoolSizeCount = 4,
            PPoolSizes = poolSizes,
        };
        api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool).Check();

        _texelLayout = CreatePipelineLayout([], ShaderStageFlags.VertexBit, (uint)sizeof(TexelConstants));
        _validityLayout = CreatePipelineLayout(
            [_validitySetLayout, renderer.Resources.Layout], ShaderStageFlags.ComputeBit, (uint)sizeof(ProbeConstants));
        _resolveLayout = CreatePipelineLayout([_resolveSetLayout], ShaderStageFlags.ComputeBit, (uint)sizeof(ResolveConstants));
        _validityPipeline = PipelineFactory.CreateCompute(context, "bake_validity.comp", _validityLayout);
        _resolvePipeline = PipelineFactory.CreateCompute(context, "bake_resolve.comp", _resolveLayout);

        // Trace: the scene and geometry table, the page's position, geometric normal and validity read, the lights, the
        // three sums read and written, and the sky. Set 1 is the resource table again. One set, rewritten for each page.
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
        };
        _traceSetLayout = CreateSetLayout(traceBindings, 10);
        var tracePoolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.AccelerationStructureKhr, 1),
            new(DescriptorType.StorageBuffer, 2),
            new(DescriptorType.StorageImage, 6),
            new(DescriptorType.CombinedImageSampler, 1),
        };
        var tracePoolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 4,
            PPoolSizes = tracePoolSizes,
        };
        api.CreateDescriptorPool(context.Device, &tracePoolInfo, null, out _tracePool).Check();
        _traceLayout = CreatePipelineLayout(
            [_traceSetLayout, renderer.Resources.Layout], ShaderStageFlags.ComputeBit, (uint)sizeof(TraceConstants));
        _tracePipeline = PipelineFactory.CreateCompute(context, "bake_trace.comp", _traceLayout);

        // Probes: the scene and geometry table, the lights, the probes' positions, the sum read and written, the faces
        // written, and the sky. Set 1 is the resource table. One set, written once a bake.
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
        var probePoolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.AccelerationStructureKhr, 1),
            new(DescriptorType.StorageBuffer, 3),
            new(DescriptorType.StorageImage, 2),
            new(DescriptorType.CombinedImageSampler, 1),
        };
        var probePoolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 4,
            PPoolSizes = probePoolSizes,
        };
        api.CreateDescriptorPool(context.Device, &probePoolInfo, null, out _probePool).Check();
        _probeLayout = CreatePipelineLayout(
            [_probeSetLayout, renderer.Resources.Layout], ShaderStageFlags.ComputeBit, (uint)sizeof(ProbeTraceConstants));
        _probePipeline = PipelineFactory.CreateCompute(context, "bake_probes.comp", _probeLayout);

        // Filter and dilation: the page's position, geometric normal and validity, then the region's irradiance,
        // direction and mask read and the same three written. Two sets, one each way between the region's two copies.
        var filterBindings = stackalloc DescriptorSetLayoutBinding[9];
        for (uint i = 0; i < 9; i++)
        {
            filterBindings[i] = new DescriptorSetLayoutBinding(i, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        }

        _filterSetLayout = CreateSetLayout(filterBindings, 9);
        var filterPoolSize = new DescriptorPoolSize(DescriptorType.StorageImage, 18);
        var filterPoolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 2,
            PoolSizeCount = 1,
            PPoolSizes = &filterPoolSize,
        };
        api.CreateDescriptorPool(context.Device, &filterPoolInfo, null, out _filterPool).Check();
        _filterLayout = CreatePipelineLayout([_filterSetLayout], ShaderStageFlags.ComputeBit, (uint)sizeof(FilterConstants));
        _filterPipeline = PipelineFactory.CreateCompute(context, "bake_filter.comp", _filterLayout);
        _dilatePipeline = PipelineFactory.CreateCompute(context, "bake_dilate.comp", _filterLayout);

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
    /// and copies. The descriptor sets are rewritten, so the previous call's work must be done.
    /// </summary>
    public void SetUpTexels(CommandBuffer cmd, LightmapLayout layout, ReadOnlySpan<DrawItem> draws)
    {
        var api = _context.Api;
        EnsurePages(layout.Pages);
        EnsureTexelPipeline();
        api.ResetDescriptorPool(_context.Device, _pool, 0).Check();

        // Kept for Trace. The renderer recycles it at its next frame, so it lives through every pass a bake submits.
        var scene = _scene = _tracing.TopLevel(cmd, _traceScene.Collect(draws));

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
                DrawMultisampled(cmd, _pages[page], onPage[page]);
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

            var set = AllocateSet(_validitySetLayout);
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
    /// Path traces every page of <paramref name="layout"/>, <paramref name="passes"/> paths a valid texel, and reads the
    /// mean back, one region a page. Call it once <see cref="SetUpTexels"/>'s work is done, and before the renderer draws
    /// another frame, which recycles the scene's top-level structure. Passes are submitted in batches sized from the
    /// first, so no submit runs much past <paramref name="batchBudget"/>. Waits for the GPU.
    /// </summary>
    /// <param name="lights">
    /// Every light of the bake, sampled at each path vertex. Each light with a <see cref="GpuBakeLight.MaskChannel"/>
    /// is also measured in that channel of the mask, wherever it reaches; lights that share a channel must not overlap.
    /// </param>
    /// <param name="skyIntensity">What the sky is scaled by; 0 leaves it out.</param>
    /// <param name="sky">The sky to light by, a cube sampled by world direction, in the shader-read layout.</param>
    public TracedPage[] Trace(
        LightmapLayout layout, ReadOnlySpan<GpuBakeLight> lights, int passes,
        float skyIntensity, ImageView sky, Sampler skySampler, TimeSpan batchBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(passes, 1);

        // An empty storage buffer isn't allowed, so there is always room for one light.
        ulong lightBytes = (ulong)Math.Max(lights.Length, 1) * (ulong)sizeof(GpuBakeLight);
        using var lightBuffer = GpuBuffer.CreateHostVisible(_context, lightBytes, BufferUsageFlags.StorageBufferBit);
        lights.CopyTo(new Span<GpuBakeLight>(lightBuffer.Mapped, lights.Length));

        var constants = new TraceConstants
        {
            LightCount = (uint)lights.Length,
            Lift = TraceLift,
            SkyIntensity = skyIntensity,
        };

        var traced = new TracedPage[layout.Pages];
        int batch = 1;
        for (int page = 0; page < layout.Pages; page++)
        {
            var (x, y, width, height) = UsedRegion(layout, page);
            constants.X = x;
            constants.Y = y;
            constants.Width = width;
            constants.Height = height;
            var extent = new Extent2D((uint)width, (uint)height);
            const ImageUsageFlags usage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
            using var irradiance = new RenderTarget(_context, SumFormat, extent, usage, ImageAspectFlags.ColorBit);
            using var direction = new RenderTarget(_context, SumFormat, extent, usage, ImageAspectFlags.ColorBit);
            using var mask = new RenderTarget(_context, SumFormat, extent, usage, ImageAspectFlags.ColorBit);
            RenderTarget[] sums = [irradiance, direction, mask];

            _context.Api.ResetDescriptorPool(_context.Device, _tracePool, 0).Check();
            var set = AllocateSet(_traceSetLayout, _tracePool);
            WriteTraceSet(set, _pages[page], lightBuffer, sums, sky, skySampler);

            _context.SubmitAndWait(cmd => ClearSums(cmd, sums));
            for (int pass = 0; pass < passes;)
            {
                int count = Math.Min(batch, passes - pass);
                int first = pass;
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                _context.SubmitAndWait(cmd => RecordPasses(cmd, set, constants, first, count, width, height));
                var took = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                pass += count;

                // As many passes as fit the budget at the rate this batch ran, never more than double at once.
                double perPass = Math.Max(took.TotalMilliseconds / count, 1e-3);
                batch = Math.Clamp((int)(batchBudget.TotalMilliseconds / perPass), 1, Math.Max(count * 2, 1));
            }

            traced[page] = ReadSums(page, sums, x, y, width, height, passes);
        }

        return traced;
    }

    /// <summary>
    /// Path traces the light probes of <paramref name="probes"/> with the scene and lights of the last
    /// <see cref="SetUpTexels"/>, and returns their atlases as the <c>.lightbake</c> file stores them. Each pass sends a
    /// path through each quarter of every texel of each probe's six <see cref="ProbeVolume.FaceSize"/> faces; the faces are then folded
    /// by <see cref="ProbeVolume.RecordFold"/>, the live probes' own fold, and the tiles written in <see cref="BakedProbeAtlas"/>'s
    /// layout. A Mixed light's direct light at the probe is left out, as it is at a texel. Passes go in batches
    /// that fit <paramref name="batchBudget"/>. Waits for the GPU.
    /// </summary>
    /// <returns>The irradiance atlas in RGBA16F and the depth atlas's two moments in RGBA16F, blue and alpha 0.</returns>
    public (LightBakePage Irradiance, LightBakePage Depth) TraceProbes(
        in ProbeVolumeSettings probes, ReadOnlySpan<GpuBakeLight> lights, int passes, float skyIntensity, ImageView sky,
        Sampler skySampler, TimeSpan batchBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(passes, 1);
        long total = ProbeGrid.Total(probes);
        if (total > ProbeGrid.MaxProbes)
        {
            throw new ArgumentException(
                $"The probes number {total}, more than the {ProbeGrid.MaxProbes} the atlases hold.", nameof(probes));
        }

        int count = (int)total;
        ulong lightBytes = (ulong)Math.Max(lights.Length, 1) * (ulong)sizeof(GpuBakeLight);
        using var lightBuffer = GpuBuffer.CreateHostVisible(_context, lightBytes, BufferUsageFlags.StorageBufferBit);
        lights.CopyTo(new Span<GpuBakeLight>(lightBuffer.Mapped, lights.Length));
        using var positionBuffer = GpuBuffer.CreateHostVisible(
            _context, (ulong)count * (ulong)sizeof(Vector4), BufferUsageFlags.StorageBufferBit);
        var positions = new Span<Vector4>(positionBuffer.Mapped, count);
        for (int i = 0; i < count; i++)
        {
            positions[i] = new Vector4(ProbeGrid.Position(probes, i), 1f);
        }

        // A volume of its own at the lowest quality, for its faces, its fold and its staging slots; its atlases stay
        // unused, since a bake keeps the fixed layout of BakedProbeAtlas, and the frame's own probes are left alone.
        using var volume = new ProbeVolume(_context, skySampler, sky);
        if (!volume.Supported)
        {
            throw new InvalidOperationException("Baking light probes needs the storage image formats and multiview the live probes fold with.");
        }

        volume.EnsureStorage(ProbeQuality.Low);
        int slots = Math.Min(volume.FaceSlots, volume.StagingSlots);
        const int face = (int)ProbeVolume.FaceSize;
        var extent = new Extent2D(6 * face, (uint)(face * ProbesPerBatch));
        const ImageUsageFlags usage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        using var sum = new RenderTarget(_context, SumFormat, extent, usage, ImageAspectFlags.ColorBit);
        using var faces = new RenderTarget(_context, GBuffer.HdrFormat, extent, usage, ImageAspectFlags.ColorBit);
        using var readBack = GpuBuffer.CreateHostVisible(
            _context, (ulong)slots * ProbeVolume.StagedTileBytes, BufferUsageFlags.TransferDstBit);

        _context.Api.ResetDescriptorPool(_context.Device, _probePool, 0).Check();
        var set = AllocateSet(_probeSetLayout, _probePool);
        WriteProbeSet(set, lightBuffer, positionBuffer, sum, faces, sky, skySampler);

        const int irradianceSide = BakedProbeAtlas.IrradianceAtlasSize;
        const int depthSide = BakedProbeAtlas.DepthAtlasSize;
        var irradiance = new byte[irradianceSide * irradianceSide * 8];
        var depth = new byte[depthSide * depthSide * 8];
        float spacing = probes.Spacing;
        var constants = new ProbeTraceConstants
        {
            ProbeCount = (uint)count,
            Passes = (uint)passes,
            LightCount = (uint)lights.Length,
            Lift = TraceLift,
            SkyIntensity = skyIntensity,
            MaxDistance = spacing * 2f,
        };
        int batch = 1;
        for (int first = 0; first < count; first += ProbesPerBatch)
        {
            constants.FirstProbe = (uint)first;
            _context.SubmitAndWait(cmd => ClearSums(cmd, [sum, faces]));
            for (int pass = 0; pass < passes;)
            {
                int passCount = Math.Min(batch, passes - pass);
                int firstPass = pass;
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                _context.SubmitAndWait(cmd => RecordProbePasses(cmd, set, constants, firstPass, passCount, extent));
                var took = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                pass += passCount;

                double perPass = Math.Max(took.TotalMilliseconds / passCount, 1e-3);
                batch = Math.Clamp((int)(batchBudget.TotalMilliseconds / perPass), 1, Math.Max(passCount * 2, 1));
            }

            // The live fold, a few probes at a time, as many as the volume has slots for. The traced distances are
            // already clamped short of the fold's sky mark, so it keeps the sky the paths brought back.
            int inBatch = Math.Min(ProbesPerBatch, count - first);
            for (int row = 0; row < inBatch; row += slots)
            {
                int inGroup = Math.Min(slots, inBatch - row);
                int firstRow = row;
                _context.SubmitAndWait(cmd =>
                {
                    RecordCopyFaces(cmd, faces, firstRow, inGroup, volume);
                    volume.BeginFolds(cmd);
                    for (int slot = 0; slot < inGroup; slot++)
                    {
                        volume.RecordFold(cmd, slot, spacing, skyIntensity, Vector4.Zero);
                    }

                    volume.RecordStagingReadBack(cmd, readBack, inGroup);
                });
                for (int slot = 0; slot < inGroup; slot++)
                {
                    var tile = new ReadOnlySpan<float>(
                        (byte*)readBack.Mapped + (long)slot * ProbeVolume.StagedTileBytes, ProbeVolume.StagedTileBytes / sizeof(float));
                    WriteTile(tile, first + firstRow + slot, irradiance, depth);
                }
            }
        }

        return (
            new LightBakePage(irradianceSide, irradianceSide, LightBakePageFormat.Rgba16f, irradiance),
            new LightBakePage(depthSide, depthSide, LightBakePageFormat.Rgba16f, depth));
    }

    /// <summary>
    /// Writes one probe's staged tiles, as <see cref="ProbeVolume.RecordFold"/> left them in float32, into the bake's
    /// pages at the probe's tile: irradiance in RGBA16F with alpha 1, the depth's two moments in RGBA16F with blue and
    /// alpha 0, since the file has no two-channel format.
    /// </summary>
    internal static void WriteTile(ReadOnlySpan<float> staged, int probe, Span<byte> irradiance, Span<byte> depth)
    {
        const int irradianceTile = ProbeVolume.IrradianceTexels + 2;
        const int depthTile = ProbeVolume.DepthTexels + 2;
        int column = probe % BakedProbeAtlas.Columns;
        int row = probe / BakedProbeAtlas.Columns;
        var irradianceHalves = MemoryMarshal.Cast<byte, Half>(irradiance);
        var depthHalves = MemoryMarshal.Cast<byte, Half>(depth);
        for (int y = 0; y < irradianceTile; y++)
        {
            for (int x = 0; x < irradianceTile; x++)
            {
                int from = (x + y * irradianceTile) * 4;
                int to = ((column * irradianceTile + x) + (row * irradianceTile + y) * BakedProbeAtlas.IrradianceAtlasSize) * 4;
                irradianceHalves[to + 0] = (Half)staged[from + 0];
                irradianceHalves[to + 1] = (Half)staged[from + 1];
                irradianceHalves[to + 2] = (Half)staged[from + 2];
                irradianceHalves[to + 3] = (Half)1f;
            }
        }

        int depthStart = irradianceTile * irradianceTile * 4;
        for (int y = 0; y < depthTile; y++)
        {
            for (int x = 0; x < depthTile; x++)
            {
                int from = depthStart + (x + y * depthTile) * 2;
                int to = ((column * depthTile + x) + (row * depthTile + y) * BakedProbeAtlas.DepthAtlasSize) * 4;
                depthHalves[to + 0] = (Half)staged[from + 0];
                depthHalves[to + 1] = (Half)staged[from + 1];
                depthHalves[to + 2] = Half.Zero;
                depthHalves[to + 3] = Half.Zero;
            }
        }
    }

    /// <summary>Records <paramref name="count"/> probe passes from <paramref name="first"/>, each waiting for the last's sum.</summary>
    private void RecordProbePasses(CommandBuffer cmd, DescriptorSet set, ProbeTraceConstants constants, int first, int count, Extent2D extent)
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
    private void RecordCopyFaces(CommandBuffer cmd, RenderTarget faces, int firstRow, int count, ProbeVolume volume)
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

    /// <summary>
    /// A page's traced light as the <c>.lightbake</c> file stores it, 2048 texels square: irradiance in RGBA16F, the
    /// direction it mostly comes from in RGBA8 (<c>d * 0.5 + 0.5</c>, alpha <c>|d|</c>) and the shadow mask in RGBA8.
    /// A texel that isn't <see cref="TracedPage.Filled"/> holds no light, no direction and a mask of 1; run
    /// <see cref="FilterAndDilate"/> first so gutters and invalid texels are filled.
    /// </summary>
    internal static (LightBakePage Irradiance, LightBakePage Direction, LightBakePage Mask) Encode(TracedPage traced)
    {
        const int side = LightmapLayout.PageSize;
        var irradiance = new byte[side * side * 8];
        var direction = new byte[side * side * 4];
        var mask = new byte[side * side * 4];
        var halves = MemoryMarshal.Cast<byte, Half>(irradiance.AsSpan());

        // Outside the region, and on texels that aren't valid: unlit, no direction, nothing masked.
        for (int i = 0; i < side * side; i++)
        {
            direction[i * 4 + 0] = 128;
            direction[i * 4 + 1] = 128;
            direction[i * 4 + 2] = 128;
            mask[i * 4 + 0] = 255;
            mask[i * 4 + 1] = 255;
            mask[i * 4 + 2] = 255;
            mask[i * 4 + 3] = 255;
        }

        for (int row = 0; row < traced.Height; row++)
        {
            for (int column = 0; column < traced.Width; column++)
            {
                int here = row * traced.Width + column;
                if (!traced.Filled[here])
                {
                    continue;
                }

                int at = (traced.Y + row) * side + traced.X + column;
                for (int c = 0; c < 3; c++)
                {
                    halves[at * 4 + c] = Light(traced.Irradiance[here * 4 + c]);
                }

                halves[at * 4 + 3] = (Half)1f;
                var d = new Vector3(traced.Direction[here * 4], traced.Direction[here * 4 + 1], traced.Direction[here * 4 + 2]);
                float weight = traced.Direction[here * 4 + 3];
                d = weight > 1e-8f ? d / weight : Vector3.Zero;
                direction[at * 4 + 0] = Unorm(d.X * 0.5f + 0.5f);
                direction[at * 4 + 1] = Unorm(d.Y * 0.5f + 0.5f);
                direction[at * 4 + 2] = Unorm(d.Z * 0.5f + 0.5f);
                direction[at * 4 + 3] = Unorm(d.Length());
                for (int c = 0; c < 4; c++)
                {
                    mask[at * 4 + c] = Unorm(traced.Mask[here * 4 + c]);
                }
            }
        }

        return (
            new LightBakePage(side, side, LightBakePageFormat.Rgba16f, irradiance),
            new LightBakePage(side, side, LightBakePageFormat.Rgba8, direction),
            new LightBakePage(side, side, LightBakePageFormat.Rgba8, mask));
    }

    // The same weights as luminance() in bake_paths.glsl.
    private static float Luminance(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    private static byte Unorm(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);

    // Irradiance as a half float the lighting can add up: not a number and anything below 0 are no light, and anything
    // past a half's largest, 65504, is held there rather than becoming infinity, which bloom would spread.
    private static Half Light(float value) => float.IsNaN(value) ? Half.Zero : (Half)Math.Clamp(value, 0f, 65504f);

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

    private void ClearSums(CommandBuffer cmd, RenderTarget[] sums)
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
    private void RecordPasses(CommandBuffer cmd, DescriptorSet set, TraceConstants constants, int first, int count, int width, int height)
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

    /// <summary>Reads a page's sums and validity back and divides the sums by the passes.</summary>
    private TracedPage ReadSums(int page, RenderTarget[] sums, int x, int y, int width, int height, int passes)
    {
        ulong count = (ulong)width * (ulong)height;
        ulong bytes = count * 16;
        using var buffer = GpuBuffer.CreateHostVisible(_context, bytes * 3, BufferUsageFlags.TransferDstBit);
        _context.SubmitAndWait(cmd =>
        {
            for (int i = 0; i < 3; i++)
            {
                Copy(cmd, sums[i], buffer, bytes * (ulong)i, 0, 0, width, height);
            }
        });

        var floats = new ReadOnlySpan<float>(buffer.Mapped, (int)(count * 12));
        var irradiance = new float[count * 4];
        var direction = new float[count * 4];
        var mask = new float[count * 4];
        float scale = 1f / passes;
        for (int i = 0; i < (int)count * 4; i++)
        {
            irradiance[i] = floats[i] * scale;
            direction[i] = floats[(int)count * 4 + i]; // Only its own direction matters, not its scale.
            mask[i] = floats[(int)count * 8 + i] * scale;
        }

        // Irradiance w held the mean squared luminance; it becomes the variance of the mean luminance, which tells the
        // filter how much of the difference between two texels is noise.
        for (int i = 0; i < (int)count; i++)
        {
            float mean = Luminance(irradiance[i * 4], irradiance[i * 4 + 1], irradiance[i * 4 + 2]);
            irradiance[i * 4 + 3] = MathF.Max(irradiance[i * 4 + 3] - mean * mean, 0f) * scale;
        }

        float[] texels = ReadTexels(page, x, y, width, height);
        var valid = new bool[count];
        var covered = new bool[count];
        for (int i = 0; i < (int)count; i++)
        {
            covered[i] = texels[i * TexelFloats + 3] > 0.5f;
            valid[i] = texels[i * TexelFloats + 10] > 0.5f;
        }

        return new TracedPage(page, x, y, width, height, irradiance, direction, mask, covered, valid, valid);
    }

    /// <summary>
    /// Cleans up a traced page before it is encoded, with the page's texels from the last <see cref="SetUpTexels"/>.
    /// Values that aren't finite become 0, and ones past the half-float range its limit. With <paramref name="smooth"/>,
    /// <see cref="FilterPasses"/> passes of an edge-aware à-trous filter then smooth the irradiance and direction; the
    /// shadow mask is left sharp. The filter stops at steps, creases and changes in light larger than the traced
    /// noise explains, using the variance in irradiance w, so converged shadow edges stay sharp. Last, <see cref="DilationRings"/> rings of dilation fill the gutters and invalid
    /// texels from their neighbours. No texel ever takes light from a texel drawn for another rectangle, apart from
    /// gutter texels nothing covers. Waits for the GPU.
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

        var api = _context.Api;
        int width = traced.Width;
        int height = traced.Height;
        int count = width * height;
        ulong bytes = (ulong)count * 16;
        var extent = new Extent2D((uint)width, (uint)height);
        const ImageUsageFlags usage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        var a = new RenderTarget[3];
        var b = new RenderTarget[3];
        for (int i = 0; i < 3; i++)
        {
            a[i] = new RenderTarget(_context, SumFormat, extent, usage, ImageAspectFlags.ColorBit);
            b[i] = new RenderTarget(_context, SumFormat, extent, usage, ImageAspectFlags.ColorBit);
        }

        try
        {
            using var staging = GpuBuffer.CreateHostVisible(
                _context, bytes * 3, BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit);
            var floats = new Span<float>(staging.Mapped, count * 12);
            traced.Irradiance.AsSpan(0, count * 4).CopyTo(floats);
            traced.Direction.AsSpan(0, count * 4).CopyTo(floats[(count * 4)..]);
            traced.Mask.AsSpan(0, count * 4).CopyTo(floats[(count * 8)..]);

            api.ResetDescriptorPool(_context.Device, _filterPool, 0).Check();
            var forward = AllocateSet(_filterSetLayout, _filterPool);
            var backward = AllocateSet(_filterSetLayout, _filterPool);
            var targets = _pages[traced.Page];
            WriteFilterSet(forward, targets, a, b);
            WriteFilterSet(backward, targets, b, a);
            var constants = new FilterConstants { X = traced.X, Y = traced.Y, Width = width, Height = height };

            // Upload into a, then prepare into b.
            _context.SubmitAndWait(cmd =>
            {
                for (int i = 0; i < 3; i++)
                {
                    api.Transition(cmd, a[i].Image, ImageAspectFlags.ColorBit,
                        ImageLayout.Undefined, ImageLayout.General,
                        PipelineStageFlags2.None, AccessFlags2.None,
                        PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit);
                    var region = new BufferImageCopy
                    {
                        BufferOffset = bytes * (ulong)i,
                        ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                        ImageExtent = new Extent3D((uint)width, (uint)height, 1),
                    };
                    api.CmdCopyBufferToImage(cmd, staging.Handle, a[i].Image, ImageLayout.General, 1, &region);
                    api.Transition(cmd, a[i].Image, ImageAspectFlags.ColorBit,
                        ImageLayout.General, ImageLayout.General,
                        PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
                        PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
                    api.Transition(cmd, b[i].Image, ImageAspectFlags.ColorBit,
                        ImageLayout.Undefined, ImageLayout.General,
                        PipelineStageFlags2.None, AccessFlags2.None,
                        PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
                }

                RecordFilterPass(cmd, _filterPipeline, forward, constants with { Stage = 0 });
            });

            // Each pass reads one copy and writes the other; after the prepare the values are in b.
            bool inB = true;
            _context.SubmitAndWait(cmd =>
            {
                if (smooth)
                {
                    for (int pass = 0; pass < FilterPasses; pass++)
                    {
                        RecordFilterPass(cmd, _filterPipeline, inB ? backward : forward, constants with { Stage = 1 << pass });
                        inB = !inB;
                    }
                }

                for (int ring = 0; ring < DilationRings; ring++)
                {
                    RecordFilterPass(cmd, _dilatePipeline, inB ? backward : forward, constants);
                    inB = !inB;
                }
            });

            var result = inB ? b : a;
            _context.SubmitAndWait(cmd =>
            {
                for (int i = 0; i < 3; i++)
                {
                    Copy(cmd, result[i], staging, bytes * (ulong)i, 0, 0, width, height);
                }
            });

            var irradiance = floats[..(count * 4)].ToArray();
            var direction = floats.Slice(count * 4, count * 4).ToArray();
            var mask = floats.Slice(count * 8, count * 4).ToArray();
            var filled = new bool[count];
            for (int i = 0; i < count; i++)
            {
                filled[i] = irradiance[i * 4 + 3] > 0.5f;
            }

            return traced with { Irradiance = irradiance, Direction = direction, Mask = mask, Filled = filled };
        }
        finally
        {
            for (int i = 0; i < 3; i++)
            {
                a[i].Dispose();
                b[i].Dispose();
            }
        }
    }

    /// <summary>Records one filter or dilation pass over the region, then a barrier so the next pass reads what it wrote.</summary>
    private void RecordFilterPass(CommandBuffer cmd, Pipeline pipeline, DescriptorSet set, FilterConstants constants)
    {
        var api = _context.Api;
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _filterLayout, 0, 1, &set, 0, null);
        api.CmdPushConstants(cmd, _filterLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FilterConstants), &constants);
        api.CmdDispatch(cmd, ((uint)constants.Width + GroupSize - 1) / GroupSize, ((uint)constants.Height + GroupSize - 1) / GroupSize, 1);
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        api.CmdPipelineBarrier2(cmd, &dependency);
    }

    private void WriteFilterSet(DescriptorSet set, PageTargets page, RenderTarget[] from, RenderTarget[] to)
    {
        var images = stackalloc DescriptorImageInfo[]
        {
            new(default, page.Position.View, ImageLayout.General),
            new(default, page.GeometricNormal.View, ImageLayout.General),
            new(default, page.Validity.View, ImageLayout.General),
            new(default, from[0].View, ImageLayout.General),
            new(default, from[1].View, ImageLayout.General),
            new(default, from[2].View, ImageLayout.General),
            new(default, to[0].View, ImageLayout.General),
            new(default, to[1].View, ImageLayout.General),
            new(default, to[2].View, ImageLayout.General),
        };
        var writes = stackalloc WriteDescriptorSet[9];
        for (int i = 0; i < 9; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &images[i],
            };
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 9, writes, 0, null);
    }

    public void Dispose()
    {
        var api = _context.Api;
        foreach (var page in _pages)
        {
            page.Dispose();
        }

        _tile?.Dispose();
        if (_texelPipeline.Handle != 0)
        {
            api.DestroyPipeline(_context.Device, _texelPipeline, null);
        }

        api.DestroyPipeline(_context.Device, _validityPipeline, null);
        api.DestroyPipeline(_context.Device, _resolvePipeline, null);
        api.DestroyPipeline(_context.Device, _tracePipeline, null);
        api.DestroyPipelineLayout(_context.Device, _traceLayout, null);
        api.DestroyDescriptorPool(_context.Device, _tracePool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _traceSetLayout, null);
        api.DestroyPipeline(_context.Device, _probePipeline, null);
        api.DestroyPipelineLayout(_context.Device, _probeLayout, null);
        api.DestroyDescriptorPool(_context.Device, _probePool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _probeSetLayout, null);
        api.DestroyPipeline(_context.Device, _filterPipeline, null);
        api.DestroyPipeline(_context.Device, _dilatePipeline, null);
        api.DestroyPipelineLayout(_context.Device, _filterLayout, null);
        api.DestroyDescriptorPool(_context.Device, _filterPool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _filterSetLayout, null);
        api.DestroyPipelineLayout(_context.Device, _texelLayout, null);
        api.DestroyPipelineLayout(_context.Device, _validityLayout, null);
        api.DestroyPipelineLayout(_context.Device, _resolveLayout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _validitySetLayout, null);
        api.DestroyDescriptorSetLayout(_context.Device, _resolveSetLayout, null);
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
    private void DrawMultisampled(CommandBuffer cmd, PageTargets targets, List<(DrawItem Draw, LightmapRect Rect)> draws)
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

        var set = AllocateSet(_resolveSetLayout);
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
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _texelPipeline);

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
        if (_texelPipeline.Handle != 0 && _texelPipelineConservative == conservative)
        {
            return;
        }

        if (_texelPipeline.Handle != 0)
        {
            _context.Api.DestroyPipeline(_context.Device, _texelPipeline, null);
        }

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

    private DescriptorSet AllocateSet(DescriptorSetLayout layout, DescriptorPool pool)
    {
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        _context.Api.AllocateDescriptorSets(_context.Device, &allocInfo, out var set).Check();
        return set;
    }

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
        DescriptorSet set, PageTargets targets, GpuBuffer lights, RenderTarget[] sums, ImageView sky, Sampler skySampler)
    {
        var handle = _scene.Handle;
        var structureInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &handle,
        };
        var buffers = stackalloc DescriptorBufferInfo[]
        {
            new(_scene.GeometryTable.Handle, 0, Vk.WholeSize),
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
        };
        var writes = stackalloc WriteDescriptorSet[10];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            PNext = &structureInfo,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
        };

        // Bindings 1 and 5 are buffers, 2 to 4 and 6 to 8 storage images, and 9 the sky.
        ReadOnlySpan<int> imageOf = [-1, -1, 0, 1, 2, -1, 3, 4, 5, 6];
        for (uint binding = 1; binding < 10; binding++)
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

        _context.Api.UpdateDescriptorSets(_context.Device, 10, writes, 0, null);
    }

    private void WriteProbeSet(
        DescriptorSet set, GpuBuffer lights, GpuBuffer positions, RenderTarget sum, RenderTarget faces, ImageView sky,
        Sampler skySampler)
    {
        var handle = _scene.Handle;
        var structureInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &handle,
        };
        var buffers = stackalloc DescriptorBufferInfo[]
        {
            new(_scene.GeometryTable.Handle, 0, Vk.WholeSize),
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

    private DescriptorSetLayout CreateSetLayout(DescriptorSetLayoutBinding* bindings, uint count)
    {
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = count,
            PBindings = bindings,
        };
        _context.Api.CreateDescriptorSetLayout(_context.Device, &layoutInfo, null, out var layout).Check();
        return layout;
    }

    private PipelineLayout CreatePipelineLayout(DescriptorSetLayout[] setLayouts, ShaderStageFlags pushStages, uint pushSize)
    {
        var range = new PushConstantRange(pushStages, 0, pushSize);
        fixed (DescriptorSetLayout* layouts = setLayouts)
        {
            var info = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)setLayouts.Length,
                PSetLayouts = layouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &range,
            };
            _context.Api.CreatePipelineLayout(_context.Device, &info, null, out var layout).Check();
            return layout;
        }
    }

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
    private struct TraceConstants
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Pass;
        public uint LightCount;
        public float Lift;
        public float SkyIntensity;
    }

    // Must match the push constants in bake_probes.comp.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeTraceConstants
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

    // Must match the push constants in bake_filter.comp and bake_dilate.comp.
    [StructLayout(LayoutKind.Sequential)]
    private struct FilterConstants
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int Stage;
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
/// What <see cref="Baker.Trace"/> found for one page, over the region (<see cref="X"/>, <see cref="Y"/>,
/// <see cref="Width"/>, <see cref="Height"/>) its rectangles cover. Each array runs row by row over the region. The
/// irradiance and mask are means over the passes, four floats a texel, with the variance of the irradiance's mean
/// luminance in irradiance w; the direction is the luminance-weighted sum of directions in xyz and the weight in w.
/// After <see cref="Baker.FilterAndDilate"/> irradiance w is 1 where a texel holds light and the direction is a mean
/// direction with w 1. <see cref="Filled"/> marks the texels that hold light: the valid ones as traced,
/// and also the ones dilation filled after <see cref="Baker.FilterAndDilate"/>.
/// </summary>
internal sealed record TracedPage(
    int Page, int X, int Y, int Width, int Height, float[] Irradiance, float[] Direction, float[] Mask, bool[] Covered,
    bool[] Valid, bool[] Filled);
