using Silk.NET.Vulkan;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The planar reflection: the scene drawn again, at half the resolution, from the camera mirrored in one level plane,
/// cut off below it by <see cref="MirrorView"/>'s near plane, and halved into mips the lighting pass blurs it by. A
/// second layer keeps how high each surface stands over the plane, so the blur is sharp where a thing touches the
/// water. It draws the camera's solid surfaces through <see cref="MirrorPipeline"/>, lit as a reflection probe's
/// capture lights them; grass, clouds, glass, particles, decals, fog and the screen's effects are left out. While no
/// frame wants a mirror its <see cref="MirrorTargets"/> are a single texel. See "Planar reflections" in docs/design.md.
/// </summary>
internal sealed class PlanarStage : IDisposable
{
    private readonly (VulkanContext Context, VulkanGpu Gpu) _device;
    private readonly FrameSwitches _switches;
    private readonly (List<Mesh?> Meshes, List<PartDetail> Details) _scene;
    private readonly MirrorPipeline _pipeline;
    private MirrorTargets _targets;

    // The frame's mirror and the plane it mirrors in, when it has one.
    private (MirrorView View, float Height)? _mirror;

    // The camera's draws the mirror picked, the first _drawn of them.
    private DrawItem[] _draws = [];
    private int _drawn;

    public PlanarStage((VulkanContext Context, VulkanGpu Gpu) device, DescriptorKit kit, ResourceTable resources, FrameSwitches switches,
        (List<Mesh?> Meshes, List<PartDetail> Details) scene)
    {
        _device = device;
        _switches = switches;
        _scene = scene;
        _pipeline = new MirrorPipeline(device, kit, resources);
        _targets = new MirrorTargets(device, new Extent2D(1, 1));
    }

    /// <summary>The mirror picture's two layers, colour and height over the plane, with their mips.</summary>
    public ImageView View => _targets.View;

    /// <summary>
    /// The height a frame mirrors at: its own plane, or with none the puddles' level while they're on, or null for no
    /// mirror, as when <see cref="RenderEffects.PlanarReflections"/> is off.
    /// </summary>
    public static float? PlaneHeight(in FrameView view, RenderEffects off)
    {
        if ((off & RenderEffects.PlanarReflections) != 0)
        {
            return null;
        }

        if (view.Extras.ReflectionPlane is { } plane)
        {
            return plane.Height;
        }

        return view.Weather.Puddles is { } puddles && (off & RenderEffects.Puddles) == 0 ? puddles.Level : null;
    }

    /// <summary>
    /// Makes the targets at half <paramref name="scene"/> when the frame wants a mirror, and a single texel when it
    /// doesn't. Each change waits for the GPU, which is rare. Returns whether the targets changed, so the sets that
    /// read them must be written again.
    /// </summary>
    public bool KeepTargets(in FrameView view, Extent2D scene)
    {
        var extent = PlaneHeight(view, _switches.Off) is null
            ? new Extent2D(1, 1)
            : new Extent2D(Math.Max(1, scene.Width / 2), Math.Max(1, scene.Height / 2));
        if (extent.Width == _targets.Extent.Width && extent.Height == _targets.Extent.Height)
        {
            return false;
        }

        // Frames in flight still read them.
        _device.Context.WaitIdle();
        _targets.Dispose();
        _targets = new MirrorTargets(_device, extent);
        return true;
    }

    /// <summary>Works out the frame's mirror, before its uniforms are written. None when the camera isn't above the plane.</summary>
    public void Plan(in FrameView view)
    {
        _mirror = null;
        _drawn = 0;
        if (_targets.Extent.Width < 2 || PlaneHeight(view, _switches.Off) is not { } height)
        {
            return;
        }

        if (MirrorView.TryMake(view.View, view.Projection, view.CameraPosition, height, out var mirror))
        {
            _mirror = (mirror, height);
        }
    }

    /// <summary>Fills the mirror's part of the frame block: all zero, which reads no mirror, when the frame has none.</summary>
    public unsafe void Write(FrameUniforms* uniforms, in FrameView view)
    {
        if (_mirror is not var (mirror, height))
        {
            uniforms->MirrorBlock = default;
            return;
        }

        // Pixels per unit of tangent, or under an orthographic camera per metre, which z's -1 marks.
        float pixels = MathF.Abs(view.Projection.M22) * _targets.Extent.Height / 2f;
        float above = view.Projection.M34 == 0f ? -1f : view.CameraPosition.Y - height;
        uniforms->MirrorBlock = new FrameMirrorBlock
        {
            MirrorViewProjection = mirror.ViewProjection,
            MirrorPlane = new(height, 1f, above, pixels),
            MirrorEye = new(mirror.Eye, _targets.Mips - 1),
        };
    }

    /// <summary>
    /// Picks what the mirror draws through <paramref name="picker"/>, after <see cref="Plan"/> and the uniforms, and
    /// returns how many instances it will add.
    /// </summary>
    public int Select(FrameDrawPicker picker, in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        if (_mirror is not var (mirror, _))
        {
            return 0;
        }

        // A draw near swapping to a finer version is drawn twice, cross-fading.
        if (_draws.Length < draws.Length * 2)
        {
            _draws = new DrawItem[draws.Length * 2];
        }

        _drawn = picker.SelectMirror(mirror, view.Projection, (int)_targets.Extent.Height, draws, _draws);
        return _drawn;
    }

    /// <summary>
    /// Points the mirror at the light probes' atlases and the environment's irradiance, for the soft light, and at the
    /// sun's cascades. Call again whenever any is made anew.
    /// </summary>
    public void WriteSet(DescriptorImageInfo irradiance, DescriptorImageInfo depth, DescriptorImageInfo sunShadow, DescriptorImageInfo environment) =>
        _pipeline.WriteSet(irradiance, depth, sunShadow, environment);

    /// <summary>
    /// Draws the mirror picture and halves it into its mips, when the frame has a mirror, and leaves the picture ready
    /// for the lighting pass to sample either way. After the shadow maps and the light probes, which it reads.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet, InstanceBuffer instances)
    {
        if (_mirror is null)
        {
            _targets.LayOutUnused(cmd);
            return;
        }

        _targets.Ready(cmd);
        _pipeline.Draw(cmd, frameSet, new SceneGeometry(_draws.AsSpan(0, _drawn), _scene.Meshes, _scene.Details, instances), _targets);
        _targets.Finish(cmd);
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _targets.Dispose();
    }
}
