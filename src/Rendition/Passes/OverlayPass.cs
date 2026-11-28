using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Draws rectangles over the finished picture, for text and interfaces, through the GPU layer. It comes after
/// anti-aliasing and color grading, so what's drawn here stays exactly as sharp and as
/// colored as it was asked to be. Rounding, borders and clipping all happen per pixel in
/// the shader, so everything stays one draw call in the order it was written. Images
/// come from the renderer's bindless table, so they don't break that either.
/// </summary>
internal sealed unsafe class OverlayPass : IDisposable
{
    /// <summary>Quads past this are dropped. Each character of text is one quad.</summary>
    public const int MaxQuads = OverlayQuad.MaxPerFrame;

    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly ResourceTable _resources;
    private readonly PipelineId _pipeline;
    private readonly FrameBuffers _buffers;
    private readonly ImageGroup _atlasGroup;
    private GpuTexture? _atlas;

    public OverlayPass((VulkanContext Context, VulkanGpu Gpu) device, GpuFormat targetFormat, ResourceTable resources, int framesInFlight)
    {
        (_context, _gpu) = device;
        _resources = resources;
        _buffers = new FrameBuffers(device, framesInFlight, MaxQuads * sizeof(OverlayQuad), BufferUsageFlags.VertexBufferBit);
        _atlasGroup = new ImageGroup(_gpu, _gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear)));

        // Each quad is one instance of the vertex buffer, blended over the picture. The layer can't make the bindless
        // table, so its layout is lent for group 1.
        _pipeline = _gpu.CreateRenderPipeline(new RenderPipelineDescription
        {
            VertexShader = _gpu.CreateShader("overlay.vert"),
            FragmentShader = _gpu.CreateShader("overlay.frag"),
            Targets = [new ColorTargetState(targetFormat, BlendState.Premultiplied)],
            Vertices = [new VertexLayout(sizeof(OverlayQuad), PerInstance: true, Attributes())],
        }, [default, resources.Layout]);
    }

    /// <summary>
    /// Replaces the image that the quads read from. Only its alpha is used. Waits for the GPU, so it isn't for every
    /// frame.
    /// </summary>
    public void SetAtlas(TextureData data)
    {
        _context.WaitIdle();
        _atlasGroup.Dispose();
        _atlas?.Dispose();
        _atlas = new GpuTexture(_context, data);
        _atlasGroup.Make(_pipeline, 0, [("atlas", _atlas.View)]);
    }

    /// <summary>Copies this frame's quads to the GPU and returns how many it draws: none before an atlas is set.</summary>
    public int Upload(int frameIndex, ReadOnlySpan<OverlayQuad> quads)
    {
        if (_atlas is null)
        {
            return 0;
        }

        var fitting = quads[..Math.Min(quads.Length, MaxQuads)];
        fitting.CopyTo(_buffers.Items<OverlayQuad>(frameIndex, MaxQuads));
        return fitting.Length;
    }

    /// <summary>Draws the frame's quads over <paramref name="target"/>, keeping what it holds.</summary>
    public void Record(CommandList commands, ViewId target, Extent2D extent, int frameIndex, int quadCount)
    {
        var screen = new Vector2(extent.Width, extent.Height);
        commands.BeginRenderPass([new ColorTarget(target, LoadOp.Load, default)], null);
        commands.SetPipeline(_pipeline);
        commands.SetBindGroups([_atlasGroup.Id, _gpu.Adopt(_resources.Set)]);
        commands.SetConstants(new ReadOnlySpan<byte>(&screen, sizeof(Vector2)));
        commands.SetVertexBuffer(0, _buffers[frameIndex], 0);
        commands.Draw(6, quadCount, 0, 0);
        commands.EndPass();
    }

    public void Dispose()
    {
        _atlasGroup.Dispose();
        _buffers.Dispose();
        _atlas?.Dispose();
        _gpu.Destroy(_pipeline);
    }

    private static VertexAttribute[] Attributes() =>
    [
        new(0, VertexFormat.Float32x2, Offset(nameof(OverlayQuad.Position))),
        new(1, VertexFormat.Float32x2, Offset(nameof(OverlayQuad.Size))),
        new(2, VertexFormat.Float32x2, Offset(nameof(OverlayQuad.UvMin))),
        new(3, VertexFormat.Float32x2, Offset(nameof(OverlayQuad.UvMax))),
        new(4, VertexFormat.Float32x4, Offset(nameof(OverlayQuad.Color))),
        new(5, VertexFormat.Float32x4, Offset(nameof(OverlayQuad.Color2))),
        new(6, VertexFormat.Float32x4, Offset(nameof(OverlayQuad.BorderColor))),
        new(7, VertexFormat.Float32x4, Offset(nameof(OverlayQuad.Clip))),
        new(8, VertexFormat.Float32x4, Offset(nameof(OverlayQuad.Radii))),
        new(9, VertexFormat.Float32x4, Offset(nameof(OverlayQuad.ClipRadii))),
        new(10, VertexFormat.Float32x4, Offset(nameof(OverlayQuad.GradientPoints))),
        new(11, VertexFormat.Float32x4, Offset(nameof(OverlayQuad.Params))),
        new(12, VertexFormat.Float32x2, Offset(nameof(OverlayQuad.BorderWidth))),
        new(13, VertexFormat.Sint32x3, Offset(nameof(OverlayQuad.TextureIndex))),
    ];

    private static int Offset(string field) => (int)Marshal.OffsetOf<OverlayQuad>(field);
}
