using System.Numerics;
using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The reflection probe a frame captures: the one at the front of the line, taken when the frame is planned, and
/// photographed from the frame's draws and blurred into its cube when the frame is recorded.
/// </summary>
internal sealed class FrameReflectionCapture(ReflectionCapturePass pass, ReflectionProbes probes, FrameSwitches switches)
{
    // The probe this frame captures, or -1 for none, and where it's placed.
    private int _slot = -1;
    private ReflectionProbe _probe;

    /// <summary>The pass that photographs a probe's cube.</summary>
    public ReflectionCapturePass Pass => pass;

    /// <summary>How many captures the frames have drawn since the renderer was made.</summary>
    public int Drawn { get; private set; }

    /// <summary>The sun shadow map's matrix of this frame's capture, or zero when the frame captures nothing.</summary>
    public Matrix4x4 SunMatrix => _slot >= 0 ? pass.SunMatrix : default;

    /// <summary>
    /// Takes the reflection probe at the front of the line for this frame's capture, when there is one and reflections
    /// are on, and plans it. Before the uniforms, which carry the capture's sun shadow map. Returns how many instances
    /// the capture will draw.
    /// </summary>
    public int Plan(in FrameView view, ReadOnlySpan<DrawItem> draws, int frameNumber)
    {
        // A frame that threw between planning its capture and recording it gives the probe back its place.
        if (_slot >= 0)
        {
            probes.PutBack(new ReflectionProbeHandle(_slot), _probe);
        }

        _slot = -1;
        if (switches.Off.HasFlag(RenderEffects.Reflections) || !pass.Supported || !probes.TakeNext(out int slot, out var probe))
        {
            return 0;
        }

        _slot = slot;
        _probe = probe;
        return pass.Plan(draws, probe, view, switches.ShadowSun(view), frameNumber);
    }

    /// <summary>
    /// Records the capture <see cref="Plan"/> planned, if any, and blurs it into the probe's cube, after the lights'
    /// tiles and the light probes, which the capture reads.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, InstanceBuffer instances)
    {
        if (_slot < 0)
        {
            return;
        }

        pass.Record(cmd, frameSet, draws, instances);
        probes.RecordFilter(cmd, _slot, _probe, pass.Cube.CubeView);
        _slot = -1;
        Drawn++;
    }
}
