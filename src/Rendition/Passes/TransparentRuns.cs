using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Records <see cref="TransparentPass"/>'s plan through the GPU layer, as runs between the copies of the scene. Each
/// run is one list played into a pass over Hdr and read-only depth; between plays, each copy is drawn
/// through the layer by <see cref="MipDraws"/>, with only Hdr's moves around it made by hand. Glass
/// and particles have different layouts, so each sets every group it reads whenever the kind of draw changes.
/// </summary>
internal sealed class TransparentRuns((VulkanGpu Gpu, VulkanContext Context) device, SceneList list, ScenePipelines pipelines, DescriptorSet environment)
    : IDisposable
{
    private readonly TransparentTargets _targets = new(device.Gpu, device.Context);
    private readonly BindGroupId _environment = device.Gpu.Adopt(environment);

    // What the last glass bound, forgotten whenever particles draw between.
    private SceneDraws.Bound _drawn = new();

    private enum Binding
    {
        None,
        Particles,
        Glass,
    }

    /// <summary>Records <paramref name="steps"/> in order. Hdr must be a colour attachment and depth read-only.</summary>
    public void Record(CommandBuffer cmd, (GBuffer Targets, DescriptorSet Frame) into, List<TransparentStep> steps,
        in SceneGeometry glass, in ParticleDraws particles)
    {
        var frame = device.Gpu.Adopt(into.Frame);
        var run = BeginRun(into.Targets);
        var bound = Binding.None;
        foreach (var step in steps)
        {
            if (step.Kind == TransparentStepKind.Copy)
            {
                EndRun(cmd, run);
                _targets.CopyScene(cmd, into.Targets);
                run = BeginRun(into.Targets);
                bound = Binding.None;
            }
            else if (step.Kind == TransparentStepKind.Particles)
            {
                if (bound != Binding.Particles)
                {
                    particles.Pass.Bind(run.Commands, frame, particles.FrameIndex, particles.Camera);
                    bound = Binding.Particles;
                }

                ParticlePass.Draw(run.Commands, step.First, step.Count);
            }
            else
            {
                if (bound != Binding.Glass)
                {
                    BindGlass(run.Commands, frame);
                    bound = Binding.Glass;
                }

                DrawGlass(run, step, glass);
            }
        }

        EndRun(cmd, run);
    }

    private SceneTarget BeginRun(GBuffer targets)
    {
        var run = list.Begin();
        _targets.Begin(run.Commands, targets);
        return run;
    }

    private void EndRun(CommandBuffer cmd, SceneTarget run)
    {
        run.Commands.EndPass();
        list.Play(cmd);
    }

    // Sets the groups glass reads, and forgets what the last glass bound, since particles may have drawn in between
    // with a layout of their own.
    private void BindGlass(CommandList commands, BindGroupId frame)
    {
        commands.SetBindGroup(0, frame);
        commands.SetBindGroup(2, _environment);
        _drawn = new SceneDraws.Bound();
    }

    private void DrawGlass(SceneTarget run, TransparentStep step, in SceneGeometry glass)
    {
        int index = step.First;
        var constants = SceneDraws.Constants(glass.Draws[index], cascade: 0, pipelines.Resources.Materials.SwayOf, glass.Details);
        constants.Tint.W = step.Refracts ? 1f : 0f;
        var copies = new SceneCopies(glass.Draws, MemoryMarshal.CreateReadOnlySpan(ref index, 1), glass.Meshes, glass.Instances);
        SceneDraws.Draw(run, pipelines, copies, constants, ref _drawn);
    }

    public void Dispose()
    {
        _targets.Dispose();
        device.Gpu.Destroy(_environment);
    }
}
