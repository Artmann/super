using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Lays decals onto the G-buffer after the opaque surfaces are drawn and before they're lit. Each
/// decal is one instance of a cube drawn round its box. Its fragment shader finds the surface
/// already drawn at each pixel from depth, keeps the pixels inside the box, and blends the
/// material's colour into the base colour target and its normal and roughness into the normal
/// target. The normal target is read from a copy taken before the pass, as it is also written.
/// </summary>
internal sealed unsafe class DecalPass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _layout;
    private readonly LivePipeline _pipeline;
    private readonly GpuBuffer[] _buffers;
    private readonly GpuDecal[] _packed = new GpuDecal[DecalBatch.MaxDecals];

    public DecalPass(VulkanContext context, DescriptorSetLayout frameSetLayout, ResourceTable resources, int framesInFlight)
    {
        _context = context;
        _resources = resources;
        var api = context.Api;
        _buffers = new GpuBuffer[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            _buffers[i] = GpuBuffer.CreateHostVisible(
                context, (ulong)(DecalBatch.MaxDecals * sizeof(GpuDecal)), BufferUsageFlags.VertexBufferBit);
        }

        // Depth, and the copy of the normal target taken before the pass.
        _setLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
        ]);
        _pool = context.CreateDescriptorPool(1, new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 2));
        _set = context.AllocateSet(_pool, _setLayout);
        _layout = context.CreatePipelineLayout([frameSetLayout, resources.Layout, _setLayout]);

        uint box = (uint)Marshal.OffsetOf<GpuDecal>(nameof(GpuDecal.Box));
        uint inverse = (uint)Marshal.OffsetOf<GpuDecal>(nameof(GpuDecal.Inverse));
        ReadOnlySpan<VertexInputBindingDescription> vertexBindings =
        [
            new(0, (uint)sizeof(GpuDecal), VertexInputRate.Instance),
        ];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new(0, 0, Format.R32G32B32A32Sfloat, box),
            new(1, 0, Format.R32G32B32A32Sfloat, box + 16),
            new(2, 0, Format.R32G32B32A32Sfloat, box + 32),
            new(3, 0, Format.R32G32B32A32Sfloat, box + 48),
            new(4, 0, Format.R32G32B32A32Sfloat, inverse),
            new(5, 0, Format.R32G32B32A32Sfloat, inverse + 16),
            new(6, 0, Format.R32G32B32A32Sfloat, inverse + 32),
            new(7, 0, Format.R32G32B32A32Sfloat, inverse + 48),
            new(8, 0, Format.R32G32B32A32Sfloat, (uint)Marshal.OffsetOf<GpuDecal>(nameof(GpuDecal.Weights))),
            new(9, 0, Format.R32Uint, (uint)Marshal.OffsetOf<GpuDecal>(nameof(GpuDecal.Material))),
        ];

        // The colour blends over the base colour by the decal's alpha, leaving metallic in alpha alone.
        // The normal target is written whole: the shader has already mixed in what was there.
        ReadOnlySpan<PipelineColorBlendAttachmentState> blends =
        [
            new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.Zero,
                DstAlphaBlendFactor = BlendFactor.One,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit,
            },
            new PipelineColorBlendAttachmentState
            {
                BlendEnable = false,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit
                    | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            },
        ];

        // Only the cube's far faces are drawn, so each pixel is shaded once, even with the camera inside the box.
        _pipeline = PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "decal.vert",
            FragmentShader = "decal.frag",
            Layout = _layout,
            ColorFormats = [GBuffer.BaseColorFormat, GBuffer.NormalFormat],
            VertexBindings = vertexBindings,
            VertexAttributes = attributes,
            Blends = blends,
            CullMode = CullModeFlags.FrontBit,
        });
    }

    /// <summary>How many decals the last <see cref="Prepare"/> packed.</summary>
    public int Count { get; private set; }

    /// <summary>Points the pass at the current depth and normal copy. Call again after a resize.</summary>
    public void WriteTargets(DescriptorImageInfo depth, DescriptorImageInfo normalCopy)
    {
        var images = stackalloc DescriptorImageInfo[] { depth, normalCopy };
        var writes = stackalloc WriteDescriptorSet[2];
        for (int i = 0; i < 2; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &images[i],
            };
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
    }

    /// <summary>Checks the decals and packs them, throwing before anything is recorded if one can't be drawn.</summary>
    public void Prepare(ReadOnlySpan<DecalDraw> decals)
    {
        Count = 0;
        DecalBatch.Pack(decals, _packed);
        Count = decals.Length;
    }

    /// <summary>Copies the prepared decals to the buffer of the given frame in flight, which the GPU must be done with.</summary>
    public void Upload(int frameIndex) =>
        _packed.AsSpan(0, Count).CopyTo(new Span<GpuDecal>(_buffers[frameIndex].Mapped, DecalBatch.MaxDecals));

    /// <summary>
    /// Draws the uploaded decals over the base colour and normal targets, which must be ready to
    /// draw into, with depth and the normal copy ready to sample.
    /// </summary>
    public void Record(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, int frameIndex)
    {
        var api = _context.Api;
        var colorAttachments = stackalloc RenderingAttachmentInfo[2];
        colorAttachments[0] = Attachment(targets.BaseColor.View);
        colorAttachments[1] = Attachment(targets.Normal.View);
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, targets.Extent),
            LayerCount = 1,
            ColorAttachmentCount = 2,
            PColorAttachments = colorAttachments,
        };
        api.CmdBeginRendering(cmd, &rendering);
        api.SetFullViewport(cmd, targets.Extent);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set, _set };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 3, sets, 0, null);
        var buffer = _buffers[frameIndex].Handle;
        ulong offset = 0;
        api.CmdBindVertexBuffers(cmd, 0, 1, &buffer, &offset);

        // A cube is 36 corners, made in the vertex shader from the corner's number.
        api.CmdDraw(cmd, 36, (uint)Count, 0, 0);
        api.CmdEndRendering(cmd);
    }

    private static RenderingAttachmentInfo Attachment(ImageView view) => new()
    {
        SType = StructureType.RenderingAttachmentInfo,
        ImageView = view,
        ImageLayout = ImageLayout.ColorAttachmentOptimal,
        LoadOp = AttachmentLoadOp.Load,
        StoreOp = AttachmentStoreOp.Store,
    };

    public void Dispose()
    {
        foreach (var buffer in _buffers)
        {
            buffer.Dispose();
        }

        var api = _context.Api;
        _pipeline.Destroy();
        api.DestroyPipelineLayout(_context.Device, _layout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }
}
