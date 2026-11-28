using System.Numerics;
using Silk.NET.Vulkan;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The clouds on the GPU: the <see cref="CloudNoise"/> they're made of, and each frame the <see cref="CloudPasses"/>,
/// which draw their shadow round the camera, march a quarter of the screen's pixels through them and fill out the rest
/// of a half-size picture from last frame's, which the lighting pass lays over the sky. The targets are made the first
/// time a frame draws clouds and kept from then on, so a game that never draws them spends no memory on them beyond
/// the 2 MB weather map, and one whose sky clears and clouds over again never waits for the GPU; until then the frame
/// sets read stand-ins that let all the light through and show no cloud. While the clouds are off nothing reads the
/// kept picture or shadow, since the frame block and the flare say they're off. See "Clouds" in docs/design.md.
/// </summary>
internal sealed class CloudStage : IDisposable
{
    private readonly VulkanContext _context;
    private readonly FrameSwitches _switches;
    private readonly CloudNoise _noise;
    private readonly CloudPasses _passes;

    // What the frame sets read until the targets are made: a shadow that lets all the light through and no cloud.
    private readonly (CellVolume Shadow, CellVolume Picture) _standIns;

    private CloudTargets? _targets;

    // Which of the two histories this frame writes, the other holding last frame's, and which pixel of each 2 by 2
    // the next march takes.
    private (int Current, int Subpixel) _history;

    // Whether the frame being recorded draws the clouds, and their shadow.
    private (bool Drawn, bool Shadowed) _frame;

    /// <param name="noise">The noise the clouds are made of, which the stage frees with itself.</param>
    public CloudStage((VulkanContext Context, VulkanGpu Gpu) device, DescriptorKit kit, FrameSwitches switches, CloudNoise noise)
    {
        var context = device.Context;
        _context = context;
        _switches = switches;
        _noise = noise;
        _passes = new CloudPasses(device.Gpu, kit.FrameSetLayout, _noise);
        _standIns = (new CellVolume(context, (1, 1, 1), new ClearColorValue(1f, 1f, 1f, 1f)),
            new CellVolume(context, (1, 1, 1), new ClearColorValue(0f, 0f, 0f, 1f)));
    }

    /// <summary>The clouds' shadow map, or a texel that lets all the light through until it's made.</summary>
    public ImageView Shadow => _targets?.Shadow.View ?? _standIns.Shadow.View;

    /// <summary>The clouds' half-size picture, or a texel that shows no cloud until it's made.</summary>
    public ImageView Picture => _targets?.Picture.View ?? _standIns.Picture.View;

    /// <summary>Whether last frame drew the clouds, so their picture can be carried on.</summary>
    public bool HistoryReady { get; private set; }

    /// <summary>
    /// Makes the noise and the targets the first time a frame draws clouds, and the targets again when a frame draws
    /// them at a new size. Each waits for the GPU. Once made they're kept while the clouds are off or the sky has none,
    /// so a sky that clears and clouds over again, as a look easing in and out does, never waits. Returns whether the
    /// targets changed, so the sets that read them must be written again.
    /// </summary>
    public bool KeepTargets(in FrameView view, Extent2D scene)
    {
        bool wanted = CloudModel.Tier(view, _switches.Off) > 0;
        if (!wanted || (_targets is not null && _targets.Size == CloudTargets.PictureSize(scene)))
        {
            return false;
        }

        // Frames in flight still read them. Drawing the first clouds or a new size is rare.
        _context.WaitIdle();
        Make(scene);
        Builds++;
        return true;
    }

    /// <summary>How many times the targets have been made, for tests.</summary>
    public int Builds { get; private set; }

    /// <summary>Points the march at the physical sky's sky-view table, which it takes the sky's light from.</summary>
    public void SetSources(ImageView skyView) => _passes.SetSources(skyView);

    /// <summary>
    /// Fills the clouds' part of the frame block, and decides whether this frame draws them and their shadow.
    /// <paramref name="previous"/> is last frame's view and projection, unshifted.
    /// </summary>
    public unsafe void Write(FrameUniforms* uniforms, in FrameView view, in Matrix4x4 previous, bool historyValid, Vector3 airExtinction)
    {
        var block = CloudModel.Pack(view, _switches.Off, previous, (_history.Subpixel, historyValid && HistoryReady), airExtinction);
        if (_targets is null)
        {
            block.CloudLight.W = 0f;
            block.CloudShadow.W = -1f;
        }
        else
        {
            // The march spreads its rays over the picture's own size, which an odd scene doesn't make twice the march's.
            block.CloudLean.Z = _targets.Size.Width;
            block.CloudLean.W = _targets.Size.Height;
        }

        uniforms->CloudBlock = block;
        bool drawn = block.CloudLight.W > 0f;
        _frame = (drawn, drawn && block.CloudShadow.W >= 0f);
        if (!drawn)
        {
            HistoryReady = false;
        }
    }

    /// <summary>
    /// Draws the shadow map, marches the clouds and fills out their picture, when the frame draws them. Must come after
    /// the sky's tables are drawn, and before the volumetric fog and the lighting pass, which read the shadow map and
    /// the picture.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet)
    {
        if (!_frame.Drawn || _targets is null)
        {
            return;
        }

        _passes.Record(cmd, frameSet, _targets, _history.Current, _frame.Shadowed);
        _history = (1 - _history.Current, (_history.Subpixel + 1) & 3);
        HistoryReady = true;
    }

    public void Dispose()
    {
        _targets?.Dispose();
        _standIns.Shadow.Dispose();
        _standIns.Picture.Dispose();
        _passes.Dispose();
        _noise.Dispose();
    }

    // The targets at this scene's size, and the noise if it isn't there yet, with no history. Only while no frame in
    // flight uses them.
    private void Make(Extent2D scene)
    {
        _targets?.Dispose();
        _noise.MakeVolumes();
        _targets = new CloudTargets(_context, CloudTargets.PictureSize(scene));
        _passes.PointAt(_targets);
        HistoryReady = false;
    }
}
