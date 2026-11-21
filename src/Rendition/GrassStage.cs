using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>One look of grass as Shaders/grass.glsl reads it, its GrassKind.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GrassKindData
{
    public Vector4 Base;
    public Vector4 Tip;
    public Vector4 Shape;
    public Vector4 More;

    public static GrassKindData Of(in GrassSettings look) => new()
    {
        Base = new Vector4(look.BaseColor, look.Translucency),
        Tip = new Vector4(look.TipColor, look.Sway),
        Shape = new Vector4(look.Height, look.HeightVariation, look.Width, look.Bend),
        More = new Vector4(look.Density, look.ClumpSize, 0f, 0f),
    };
}

[InlineArray(5)]
internal struct GrassPlanes
{
    private Vector4 _plane;
}

[InlineArray(GrassField.MaxKinds)]
internal struct GrassKinds
{
    private GrassKindData _kind;
}

/// <summary>The grass's uniforms, as Shaders/grass.glsl lays out its GrassField block.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GrassUniforms
{
    public GrassPlanes Planes;
    public Vector4 Capture;
    public Vector4 Heights;
    public Vector4 Rings;
    public Vector4 Camera;
    public int Tiles;
    public int MaxBlades;
    private readonly int _unused0;
    private readonly int _unused1;
    public GrassKinds Kinds;
}

/// <summary>The push constants of the grass's draws, as the grass shaders' GrassDraw block lays them out.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GrassConstants
{
    public Matrix4x4 Model;
    public int DensityMap;
    public int DensityChannel;
    public int Kind;
    private readonly int _unused;

    /// <summary>The density map's corners in the ground's own x and z: Min in xy, Max in zw.</summary>
    public Vector4 DensityArea;

    /// <summary>The constants for a shadow draw into <paramref name="cascade"/>.</summary>
    public static GrassConstants Shadow(int cascade) => new() { DensityMap = cascade };
}

/// <summary>
/// Grows the frame's grass. Each frame the ground under it is drawn from above into a map of its height and how thick
/// the grass stands, a compute pass scatters blades over the squares of the lattice the camera sees, and the blades
/// that stand are drawn into the G-buffer and the sun's nearest two cascades from one indirect draw. See "Grass" in
/// docs/design.md.
/// </summary>
internal sealed unsafe class GrassStage(VulkanContext context, SceneStores stores) : IDisposable
{
    /// <summary>The most blades a frame draws. Past it, the scatter keeps the first it finds.</summary>
    public const int MaxBlades = 1 << 18;

    /// <summary>The sun's cascades the blades are drawn into: the nearest, where their shadows show.</summary>
    public const int ShadowCascades = 2;

    private const uint BladeVertices = 15;

    private readonly GrassPipelines _pipelines = new(context, stores);
    private readonly GrassPlan _plan = new(stores.Meshes, stores.Resources);

    // Made the first frame with grass, and kept.
    private GrassTargets? _targets;

    /// <summary>Whether this frame has grass to grow: the switch is on and the view has some.</summary>
    public bool Active { get; private set; }

    /// <summary>
    /// Checks the view's grass and plans the frame's: its looks, and the squares of the lattice the camera sees. None
    /// while <paramref name="off"/> has <see cref="RenderEffects.Grass"/>. Throws for grass that can't be drawn, and
    /// then grows none: a look out of range, too many looks, a ground mesh or density map that doesn't exist, or a
    /// channel other than 0 to 3.
    /// </summary>
    public void Prepare(in FrameView view, RenderEffects off, float detail)
    {
        Active = false;
        if (!off.HasFlag(RenderEffects.Grass) && view.Extras.Grass.Length > 0)
        {
            _plan.Prepare(view, detail);
            Active = true;
        }
    }

    /// <summary>Writes the planned grass into the frame's slot, making the buffers and the ground map the first time.</summary>
    public void Upload()
    {
        if (!Active)
        {
            return;
        }

        _targets ??= new GrassTargets(context, stores.Kit.NearestSampler, _pipelines.Sets);
        int slot = stores.Ring.Index;
        Unsafe.Write(_targets.Uniforms(slot), _plan.Uniforms);
        var tiles = _plan.Tiles;
        tiles.CopyTo(new Span<GrassTile>(_targets.Tiles(slot), tiles.Length));
    }

    /// <summary>
    /// Draws the ground map and scatters the blades, before the shadow maps and the G-buffer draw them. Last frame's
    /// draws of the blades finish first, as the blades are shared.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet)
    {
        if (!Active)
        {
            return;
        }

        var api = context.Api;
        api.MemoryBarrier(cmd,
            (PipelineStageFlags2.DrawIndirectBit | PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.IndirectCommandReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit));
        var reset = stackalloc uint[] { BladeVertices, 0, 0, 0 };
        api.CmdUpdateBuffer(cmd, _targets!.Args.Handle, 0, 16, reset);

        RecordGround(cmd, frameSet);

        api.MemoryBarrier(cmd,
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit));
        if (_plan.Tiles.Length > 0)
        {
            api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipelines.Scatter);
            _pipelines.Bind(cmd, frameSet, PipelineBindPoint.Compute, stores.Ring.Index);
            api.CmdDispatch(cmd, (uint)_plan.Tiles.Length, 1, 1);
        }

        api.MemoryBarrier(cmd,
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.DrawIndirectBit | PipelineStageFlags2.VertexShaderBit,
                AccessFlags2.IndirectCommandReadBit | AccessFlags2.ShaderStorageReadBit));
    }

    // Draws the grounds' tops into the ground map, then makes it ready for the scatter to read.
    private void RecordGround(CommandBuffer cmd, DescriptorSet frameSet)
    {
        var api = context.Api;
        var (height, info) = (_targets!.Height, _targets.Info);
        api.Transition(cmd, new ImageTransition(height.Image, ImageAspectFlags.DepthBit, ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit),
            (DepthStages, DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit));
        api.Transition(cmd, new ImageTransition(info.Image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit), (ColorStage, ColorWrite));

        BeginGround(cmd);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipelines.Ground);
        _pipelines.Bind(cmd, frameSet, PipelineBindPoint.Graphics, stores.Ring.Index);
        var draws = _plan.Draws;
        var kinds = _plan.DrawKinds;
        for (int i = 0; i < draws.Length; i++)
        {
            DrawGround(cmd, draws[i], kinds[i]);
        }

        api.CmdEndRendering(cmd);
        api.Transition(cmd, new ImageTransition(height.Image, ImageAspectFlags.DepthBit, ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal),
            (DepthStages, DepthWrite), (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit));
        api.Transition(cmd, new ImageTransition(info.Image, ImageAspectFlags.ColorBit, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal),
            (ColorStage, ColorWrite), (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit));
    }

    // Begins drawing into the ground map, cleared to no ground: a depth of 0 and no grass.
    private void BeginGround(CommandBuffer cmd)
    {
        var extent = new Extent2D(GrassField.CaptureSize, GrassField.CaptureSize);
        var color = Cleared(_targets!.Info.View, ImageLayout.ColorAttachmentOptimal, default);
        var depth = Cleared(_targets.Height.View, ImageLayout.DepthAttachmentOptimal, new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0)));
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &color,
            PDepthAttachment = &depth,
        };
        context.Api.CmdBeginRendering(cmd, &rendering);
        context.Api.SetFullViewport(cmd, extent);
    }

    private static RenderingAttachmentInfo Cleared(ImageView view, ImageLayout layout, ClearValue clear) => new()
    {
        SType = StructureType.RenderingAttachmentInfo,
        ImageView = view,
        ImageLayout = layout,
        LoadOp = AttachmentLoadOp.Clear,
        StoreOp = AttachmentStoreOp.Store,
        ClearValue = clear,
    };

    private void DrawGround(CommandBuffer cmd, in GrassDraw draw, int kind)
    {
        var api = context.Api;
        var mesh = stores.Meshes.MeshSlots[draw.Ground.Mesh.Index]!;
        var vertices = mesh.Vertices.Handle;
        ulong offset = 0;
        api.CmdBindVertexBuffers(cmd, 0, 1, &vertices, &offset);
        api.CmdBindIndexBuffer(cmd, mesh.Indices.Handle, 0, IndexType.Uint32);
        _pipelines.Push(cmd, new GrassConstants
        {
            Model = draw.Ground.Transform,
            DensityMap = draw.Density?.Map.Index ?? -1,
            DensityChannel = draw.Density?.Channel ?? 0,
            Kind = kind,
            DensityArea = draw.Density is { } density ? new Vector4(density.Min, density.Max.X, density.Max.Y) : default,
        });
        uint count = draw.Ground.IndexCount > 0 ? draw.Ground.IndexCount : mesh.IndexCount;
        api.CmdDrawIndexed(cmd, count, 1, draw.Ground.FirstIndex, 0, 0);
    }

    /// <summary>Draws the scattered blades inside the G-buffer pass, after its draws.</summary>
    public void RecordBlades(CommandBuffer cmd, DescriptorSet frameSet)
    {
        if (!Active)
        {
            return;
        }

        context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipelines.Blades);
        _pipelines.Bind(cmd, frameSet, PipelineBindPoint.Graphics, stores.Ring.Index);
        context.Api.CmdDrawIndirect(cmd, _targets!.Args.Handle, 0, 1, 16);
    }

    /// <summary>Draws the scattered blades into a cascade of the sun's map, inside its pass, for the nearest cascades only.</summary>
    public void RecordShadow(CommandBuffer cmd, DescriptorSet frameSet, int cascade)
    {
        if (!Active || cascade < 0 || cascade >= ShadowCascades)
        {
            return;
        }

        context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipelines.Shadow);
        _pipelines.Bind(cmd, frameSet, PipelineBindPoint.Graphics, stores.Ring.Index);
        _pipelines.Push(cmd, GrassConstants.Shadow(cascade));
        context.Api.CmdDrawIndirect(cmd, _targets!.Args.Handle, 0, 1, 16);
    }

    public void Dispose()
    {
        _pipelines.Dispose();
        _targets?.Dispose();
    }
}
