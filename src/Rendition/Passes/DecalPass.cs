using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;
using BlendFactor = Rendition.Gpu.BlendFactor;

namespace Rendition.Passes;

/// <summary>
/// Lays decals onto the G-buffer after the opaque surfaces are drawn and before they're lit, through the GPU layer.
/// Each decal is one instance of a cube drawn round its box. Its fragment shader finds the surface already drawn at
/// each pixel from depth, keeps the pixels inside the box, and blends the material's colour into the base colour
/// target and its normal and roughness into the normal target. The normal target is read from a copy taken before the
/// pass, as it is also written. <see cref="OpaqueStage"/> moves the G-buffer's layouts itself, so its two targets are
/// adopted as ones the layer leaves alone.
/// </summary>
internal sealed unsafe class DecalPass : IDisposable
{
    private readonly VulkanGpu _gpu;
    private readonly ResourceTable _resources;
    private readonly PipelineId _pipeline;
    private readonly FrameBuffers _buffers;
    private readonly ImageGroup _targets;
    private readonly AdoptedTarget _baseColor;
    private readonly AdoptedTarget _normal;
    private readonly CommandList _commands = new();
    private readonly GpuDecal[] _packed = new GpuDecal[DecalBatch.MaxDecals];

    public DecalPass((VulkanContext Context, VulkanGpu Gpu) device, DescriptorSetLayout frameSetLayout, ResourceTable resources, int framesInFlight)
    {
        _gpu = device.Gpu;
        _resources = resources;
        _buffers = new FrameBuffers(device, framesInFlight, DecalBatch.MaxDecals * sizeof(GpuDecal), BufferUsageFlags.VertexBufferBit);
        _targets = new ImageGroup(_gpu, _gpu.CreateSampler(new SamplerDescription(SamplerFilter.Nearest)));
        _baseColor = new AdoptedTarget(_gpu);
        _normal = new AdoptedTarget(_gpu);

        // The colour blends over the base colour by the decal's alpha, leaving metallic in alpha alone. The normal
        // target is written whole: the shader has already mixed in what was there. Only the cube's far faces are
        // drawn, so each pixel is shaded once, even with the camera inside the box. The frame's set and the bindless
        // table are made outside the layer, so their layouts are lent; group 2 is the pass's own.
        _pipeline = _gpu.CreateRenderPipeline(new RenderPipelineDescription
        {
            VertexShader = _gpu.CreateShader("decal.vert"),
            FragmentShader = _gpu.CreateShader("decal.frag"),
            Targets =
            [
                new ColorTargetState(VulkanFormats.ToGpu(GBuffer.BaseColorFormat),
                    new BlendState(BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendFactor.Zero, BlendFactor.One), ColorWrites.Color),
                new ColorTargetState(VulkanFormats.ToGpu(GBuffer.NormalFormat)),
            ],
            Vertices = [new VertexLayout(sizeof(GpuDecal), PerInstance: true, InstanceAttributes())],
            Cull = CullMode.Front,
        }, [frameSetLayout, resources.Layout, default]);
    }

    // Each decal is one instance: its box's matrix and the inverse, a row to a location, then its weights and material.
    private static VertexAttribute[] InstanceAttributes()
    {
        int box = (int)Marshal.OffsetOf<GpuDecal>(nameof(GpuDecal.Box));
        int inverse = (int)Marshal.OffsetOf<GpuDecal>(nameof(GpuDecal.Inverse));
        return
        [
            new(0, VertexFormat.Float32x4, box),
            new(1, VertexFormat.Float32x4, box + 16),
            new(2, VertexFormat.Float32x4, box + 32),
            new(3, VertexFormat.Float32x4, box + 48),
            new(4, VertexFormat.Float32x4, inverse),
            new(5, VertexFormat.Float32x4, inverse + 16),
            new(6, VertexFormat.Float32x4, inverse + 32),
            new(7, VertexFormat.Float32x4, inverse + 48),
            new(8, VertexFormat.Float32x4, (int)Marshal.OffsetOf<GpuDecal>(nameof(GpuDecal.Weights))),
            new(9, VertexFormat.Uint32, (int)Marshal.OffsetOf<GpuDecal>(nameof(GpuDecal.Material))),
        ];
    }

    /// <summary>How many decals the last <see cref="Prepare"/> packed.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Points the pass at the current depth and normal copy. Makes its bind group again, so it must only be called
    /// while the GPU is idle, as after a resize.
    /// </summary>
    public void WriteTargets(ImageView depth, ImageView normalCopy) =>
        _targets.Make(_pipeline, 2, [("depthTexture", depth), ("normalCopy", normalCopy)]);

    /// <summary>Checks the decals and packs them, throwing before anything is recorded if one can't be drawn.</summary>
    public void Prepare(ReadOnlySpan<DecalDraw> decals)
    {
        Count = 0;
        DecalBatch.Pack(decals, _packed);
        Count = decals.Length;
    }

    /// <summary>Copies the prepared decals to the buffer of the given frame in flight, which the GPU must be done with.</summary>
    public void Upload(int frameIndex) =>
        _packed.AsSpan(0, Count).CopyTo(_buffers.Items<GpuDecal>(frameIndex, DecalBatch.MaxDecals));

    /// <summary>
    /// Draws the uploaded decals over the base colour and normal targets, which must be ready to
    /// draw into, with depth and the normal copy ready to sample.
    /// </summary>
    public void Record(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, int frameIndex)
    {
        var extent = targets.Extent;
        var baseColor = _baseColor.ForHeld(targets.BaseColor.View, targets.BaseColor.Image, extent);
        var normal = _normal.ForHeld(targets.Normal.View, targets.Normal.Image, extent);
        _commands.Reset();
        _commands.BeginRenderPass([new ColorTarget(baseColor, LoadOp.Load, default), new ColorTarget(normal, LoadOp.Load, default)], null);
        _commands.SetPipeline(_pipeline);
        _commands.SetBindGroups([_gpu.Adopt(frameSet), _gpu.Adopt(_resources.Set), _targets.Id]);
        _commands.SetVertexBuffer(0, _buffers[frameIndex], 0);

        // A cube is 36 corners, made in the vertex shader from the corner's number.
        _commands.Draw(36, Count, 0, 0);
        _commands.EndPass();
        _gpu.Play(_commands, cmd);
    }

    public void Dispose()
    {
        _baseColor.Dispose();
        _normal.Dispose();
        _targets.Dispose();
        _buffers.Dispose();
        _gpu.Destroy(_pipeline);
    }
}
