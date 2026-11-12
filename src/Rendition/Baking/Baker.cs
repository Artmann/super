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
    /// <summary>Floats a texel has in <see cref="ReadTexels(int, int, int, int, int)"/>: position xyz and covered, geometric normal xyz, shading normal xyz, valid.</summary>
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

    /// <summary>What a mask channel holds when no light has it: fully lit, so the runtime never darkens by it.</summary>
    internal const uint NoMaskLight = uint.MaxValue;

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

        foreach (ref readonly var draw in draws)
        {
            if (draw.BakeId != 0 && draw.IndexCount != 0 && layout.TryGet(draw.BakeId, out var rect))
            {
                onPage[rect.Page].Add((draw, rect));
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
    /// <param name="lights">Every light of the bake, sampled at each path vertex.</param>
    /// <param name="maskLights">For each of the four mask channels, the index into <paramref name="lights"/> it measures, or <see cref="NoMaskLight"/>.</param>
    /// <param name="skyIntensity">What the sky is scaled by; 0 leaves it out.</param>
    /// <param name="sky">The sky to light by, a cube sampled by world direction, in the shader-read layout.</param>
    public TracedPage[] Trace(
        LightmapLayout layout, ReadOnlySpan<GpuBakeLight> lights, ReadOnlySpan<uint> maskLights, int passes,
        float skyIntensity, ImageView sky, Sampler skySampler, TimeSpan batchBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(passes, 1);
        if (maskLights.Length != ShadowMaskChannels.Count)
        {
            throw new ArgumentException(
                $"maskLights holds {maskLights.Length} entries, not one for each of the {ShadowMaskChannels.Count} channels.",
                nameof(maskLights));
        }

        // An empty storage buffer isn't allowed, so there is always room for one light.
        ulong lightBytes = (ulong)Math.Max(lights.Length, 1) * (ulong)sizeof(GpuBakeLight);
        using var lightBuffer = GpuBuffer.CreateHostVisible(_context, lightBytes, BufferUsageFlags.StorageBufferBit);
        lights.CopyTo(new Span<GpuBakeLight>(lightBuffer.Mapped, lights.Length));

        var constants = new TraceConstants
        {
            LightCount = (uint)lights.Length,
            Lift = TraceLift,
            SkyIntensity = skyIntensity,
            MaskLight0 = maskLights[0],
            MaskLight1 = maskLights[1],
            MaskLight2 = maskLights[2],
            MaskLight3 = maskLights[3],
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
    /// A page's traced light as the <c>.lightbake</c> file stores it, 2048 texels square: irradiance in RGBA16F, the
    /// direction it mostly comes from in RGBA8 (<c>d * 0.5 + 0.5</c>, alpha <c>|d|</c>) and the shadow mask in RGBA8.
    /// A texel that isn't valid holds no light, no direction and a mask of 1, for the filter to fill later.
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
                if (!traced.Valid[here])
                {
                    continue;
                }

                int at = (traced.Y + row) * side + traced.X + column;
                for (int c = 0; c < 3; c++)
                {
                    halves[at * 4 + c] = (Half)traced.Irradiance[here * 4 + c];
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

    private static byte Unorm(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);

    /// <summary>The texels a page's rectangles cover, with <see cref="TraceMargin"/> round them, clamped to the page.</summary>
    private static (int X, int Y, int Width, int Height) UsedRegion(LightmapLayout layout, int page)
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

        float[] texels = ReadTexels(page, x, y, width, height);
        var valid = new bool[count];
        var covered = new bool[count];
        for (int i = 0; i < (int)count; i++)
        {
            covered[i] = texels[i * TexelFloats + 3] > 0.5f;
            valid[i] = texels[i * TexelFloats + 10] > 0.5f;
        }

        return new TracedPage(page, x, y, width, height, irradiance, direction, mask, covered, valid);
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

        /// <summary>World position, w 1 where a surface covers the texel and 0 elsewhere.</summary>
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
        public uint MaskLight0;
        public uint MaskLight1;
        public uint MaskLight2;
        public uint MaskLight3;
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

    /// <summary>A sun, its disc <paramref name="lightAngle"/> radians across (<see cref="ShadowSettings.LightAngle"/>).</summary>
    public static GpuBakeLight Sun(in DirectionalLight sun, float lightAngle) => new()
    {
        PositionRange = new Vector4(Vector3.Normalize(sun.Direction), 0f),
        RadianceRadius = new Vector4(sun.Radiance, MathF.Cos(lightAngle * 0.5f)),
        DirectionCone = new Vector4(0f, 0f, 0f, -2f),
        FadeKind = new Vector4(0f, 1f, sun.Mode == LightMode.Mixed ? 1f : 0f, 0f),
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
            FadeKind = new Vector4(fade, 0f, light.Mode == LightMode.Mixed ? 1f : 0f, 0f),
        };
    }
}

/// <summary>
/// What <see cref="Baker.Trace"/> found for one page, over the region (<see cref="X"/>, <see cref="Y"/>,
/// <see cref="Width"/>, <see cref="Height"/>) its rectangles cover. Each array runs row by row over the region. The
/// irradiance and mask are means over the passes, four floats a texel; the direction is the luminance-weighted sum of
/// directions in xyz and the weight in w.
/// </summary>
internal sealed record TracedPage(
    int Page, int X, int Y, int Width, int Height, float[] Irradiance, float[] Direction, float[] Mask, bool[] Covered,
    bool[] Valid);
