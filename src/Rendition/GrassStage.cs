using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Passes;
using Rendition.Vulkan;

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
    public Vector4 Details;
    public int Tiles;
    private readonly int _unused0;
    private readonly int _unused1;
    private readonly int _unused2;

    /// <summary>Where each detail's blades start in the buffer, and in the last, the blades it holds in all.</summary>
    public int FirstRegion;
    public int SecondRegion;
    public int ThirdRegion;
    public int AllBlades;
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

    /// <summary>The detail a draw of the blades draws them at, as GrassField.Details counts them.</summary>
    public int Detail;

    /// <summary>The density map's corners in the ground's own x and z: Min in xy, Max in zw.</summary>
    public Vector4 DensityArea;

    /// <summary>The constants for a draw of the blades at <paramref name="detail"/>.</summary>
    public static GrassConstants Blades(int detail) => new() { Detail = detail };

    /// <summary>The constants for a shadow draw of the blades at <paramref name="detail"/> into <paramref name="cascade"/>.</summary>
    public static GrassConstants Shadow(int cascade, int detail) => new() { DensityMap = cascade, Detail = detail };
}

/// <summary>
/// Grows the frame's grass. Each frame the ground under it is drawn from above into a map of its height and how thick
/// the grass stands, a compute pass scatters blades over the squares of the lattice the camera sees, and the blades
/// that stand are drawn into the G-buffer and the sun's nearest two cascades from one indirect draw. See "Grass" in
/// docs/design.md.
/// </summary>
internal sealed unsafe class GrassStage(VulkanContext context, SceneStores stores, GpuTimer timer) : IDisposable
{
    /// <summary>The sun's cascades the blades are drawn into: the nearest, where their shadows show.</summary>
    public const int ShadowCascades = 2;

    /// <summary>
    /// The details whose blades cast shadows: the nearer ones. Further blades are too small in the cascades they fall
    /// in to shade anything the ground's own shadow doesn't.
    /// </summary>
    public const int ShadowDetails = 2;

    /// <summary>The indirect draws' arguments: four words for each detail.</summary>
    public const int ArgsSize = GrassField.Details * ArgsStride;

    private const int ArgsStride = 16;

    private readonly GrassPipelines _pipelines = new(context, stores);
    private readonly GrassPlan _plan = new(stores.Meshes, stores.Resources);
    private readonly CommandList _list = new();

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

        _targets ??= new GrassTargets((context, stores.Gpu), stores.Kit.NearestSampler, _pipelines.Sets);
        int slot = stores.Ring.Index;
        Unsafe.Write(_targets.Uniforms(slot), _plan.Uniforms);
        var tiles = _plan.Tiles;
        tiles.CopyTo(new Span<GrassTile>(_targets.Tiles(slot), tiles.Length));
    }

    /// <summary>
    /// Draws the ground map and scatters the blades, before the shadow maps and the G-buffer draw them. The copy that
    /// resets the draws' arguments waits for last frame's draws of the blades, as the blades are shared.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet)
    {
        if (!Active)
        {
            return;
        }

        var targets = _targets!;
        Span<BindGroupId> groups = stackalloc BindGroupId[3];
        _pipelines.Groups(groups, frameSet, stores.Ring.Index);
        _list.Reset();
        _list.CopyBuffer(targets.ArgsStart, 0, targets.Args, 0, ArgsSize);
        RecordGround(groups);
        if (_plan.Tiles.Length > 0)
        {
            _pipelines.Scatter.Record(_list, groups, default, (_plan.Tiles.Length, 1, 1));
        }

        stores.Gpu.Play(_list, cmd);
    }

    // Draws the grounds' tops into the ground map, cleared first to no ground: a depth of 0 and no grass.
    private void RecordGround(ReadOnlySpan<BindGroupId> groups)
    {
        var targets = _targets!;
        _list.BeginRenderPass([new ColorTarget(targets.Info, LoadOp.Clear, default)], new DepthTarget(targets.Height, LoadOp.Clear, 0f, ReadOnly: false));
        _list.SetPipeline(_pipelines.Ground);
        _list.SetBindGroups(groups);
        var draws = _plan.Draws;
        var kinds = _plan.DrawKinds;
        for (int i = 0; i < draws.Length; i++)
        {
            DrawGround(draws[i], kinds[i]);
        }

        _list.EndPass();
    }

    private void DrawGround(in GrassDraw draw, int kind)
    {
        var mesh = stores.Meshes.MeshSlots[draw.Ground.Mesh.Index]!;
        _list.SetVertexBuffer(0, mesh.VertexBuffer, 0);
        _list.SetIndexBuffer(mesh.IndexBuffer, 0);
        GrassPipelines.SetConstants(_list, new GrassConstants
        {
            Model = draw.Ground.Transform,
            DensityMap = draw.Density?.Map.Index ?? -1,
            DensityChannel = draw.Density?.Channel ?? 0,
            Kind = kind,
            DensityArea = draw.Density is { } density ? new Vector4(density.Min, density.Max.X, density.Max.Y) : default,
        });
        uint count = draw.Ground.IndexCount > 0 ? draw.Ground.IndexCount : mesh.IndexCount;
        _list.DrawIndexed((int)count, 1, (int)draw.Ground.FirstIndex, 0, 0);
    }

    /// <summary>
    /// Draws the scattered blades in a pass of their own over what the G-buffer's draws left in it. Marks the timer
    /// before and after, with grass or without, so the G-buffer's draws and the blades are each timed as a pass.
    /// </summary>
    public void RecordBlades(CommandBuffer cmd, DescriptorSet frameSet, (GBufferTargets Targets, GBuffer GBuffer) into)
    {
        timer.Mark(cmd);
        if (Active)
        {
            _list.Reset();
            into.Targets.Begin(_list, into.GBuffer, clear: false);
            BeginDraws(_pipelines.Blades, frameSet);
            for (int detail = 0; detail < GrassField.Details; detail++)
            {
                GrassPipelines.SetConstants(_list, GrassConstants.Blades(detail));
                _list.DrawIndirect(_targets!.Args, detail * ArgsStride);
            }

            _list.EndPass();
            stores.Gpu.Play(_list, cmd);
        }

        timer.Mark(cmd);
    }

    /// <summary>
    /// Draws the nearer details' blades over what the casters left in the nearest of <paramref name="cascades"/>, the
    /// sun's map's layers, a pass for each.
    /// </summary>
    public void RecordShadows(CommandBuffer cmd, DescriptorSet frameSet, ReadOnlySpan<ViewId> cascades)
    {
        if (!Active)
        {
            return;
        }

        _list.Reset();
        for (int cascade = 0; cascade < Math.Min(cascades.Length, ShadowCascades); cascade++)
        {
            _list.BeginRenderPass([], new DepthTarget(cascades[cascade], LoadOp.Load, 0f, ReadOnly: false));
            BeginDraws(_pipelines.Shadow, frameSet);
            for (int detail = 0; detail < ShadowDetails; detail++)
            {
                GrassPipelines.SetConstants(_list, GrassConstants.Shadow(cascade, detail));
                _list.DrawIndirect(_targets!.Args, detail * ArgsStride);
            }

            _list.EndPass();
        }

        stores.Gpu.Play(_list, cmd);
    }

    // Sets a draw pipeline and the groups every grass pipeline reads.
    private void BeginDraws(PipelineId pipeline, DescriptorSet frameSet)
    {
        Span<BindGroupId> groups = stackalloc BindGroupId[3];
        _pipelines.Groups(groups, frameSet, stores.Ring.Index);
        _list.SetPipeline(pipeline);
        _list.SetBindGroups(groups);
    }

    public void Dispose()
    {
        _pipelines.Dispose();
        _targets?.Dispose();
    }
}
