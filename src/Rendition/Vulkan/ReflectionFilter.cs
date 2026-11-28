using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;

namespace Rendition.Vulkan;

/// <summary>
/// Blurs a reflection capture into the levels of a probe's cube, rougher with each level, with
/// Shaders/reflection_probe.comp, run through the GPU layer. Each level of each cube has its own bind group, made the
/// first time it is filtered and again when its views change or the cubes grow, which happens after the GPU is
/// idle.
/// </summary>
internal sealed class ReflectionFilter : IDisposable
{
    /// <summary>Must match the Filter block in Shaders/reflection_probe.comp.</summary>
    private struct FilterConstants
    {
        public float Roughness;
    }

    private readonly VulkanGpu _gpu;
    private readonly int _levels;
    private readonly uint _faceSize;
    private readonly SamplerId _sampler;
    private readonly ComputePass _pass;
    private readonly CommandList _commands = new();

    // Each cube's levels' groups, by slot and then mip, and the capture and level each was made for.
    private readonly List<(ImageGroup Group, ImageView Capture, ImageView Level)[]> _groups = [];

    /// <summary>A filter into cubes of <paramref name="levels"/> mips whose faces are <paramref name="faceSize"/> texels across.</summary>
    public ReflectionFilter(VulkanGpu gpu, int levels, uint faceSize)
    {
        _gpu = gpu;
        _levels = levels;
        _faceSize = faceSize;

        // The capture is read between its mips.
        _sampler = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear, Mipmapped: true));
        _pass = new ComputePass(gpu, "reflection_probe.comp");
    }

    /// <summary>
    /// Records the blur of <paramref name="capture"/>, with its mips ready to sample, into each of
    /// <paramref name="levels"/>, a view of one mip of <paramref name="slot"/>'s cube each, which must be in the general
    /// layout. A capture or level other than last time's must only come while the GPU is idle.
    /// </summary>
    public void Record(CommandBuffer cmd, int slot, ImageView capture, ReadOnlySpan<ImageView> levels)
    {
        var groups = Groups(slot);
        _commands.Reset();
        for (int mip = 0; mip < _levels; mip++)
        {
            ref var made = ref groups[mip];
            if (made.Capture.Handle != capture.Handle || made.Level.Handle != levels[mip].Handle)
            {
                made.Group.Make(_pass.Pipeline, 0, [("source", capture), new GroupImage("target", levels[mip], Use: GroupImageUse.Written)]);
                made = (made.Group, capture, levels[mip]);
            }

            var constants = new FilterConstants { Roughness = mip / (float)(_levels - 1) };
            int workgroups = (int)((_faceSize >> mip) + 7) / 8;
            _pass.Record(_commands, [made.Group.Id], MemoryMarshal.AsBytes(new ReadOnlySpan<FilterConstants>(in constants)), (workgroups, workgroups, 6));
        }

        _gpu.Play(_commands, cmd);
    }

    /// <summary>
    /// Forgets which views each group was made for, so the next <see cref="Record"/> of every slot makes its groups
    /// again. Called when the cubes grow: their old views are freed, and Vulkan may hand the new ones the same handles.
    /// </summary>
    public void Forget()
    {
        foreach (var cube in _groups)
        {
            for (int mip = 0; mip < cube.Length; mip++)
            {
                cube[mip] = (cube[mip].Group, default, default);
            }
        }
    }

    public void Dispose()
    {
        foreach (var cube in _groups)
        {
            foreach (var made in cube)
            {
                made.Group.Dispose();
            }
        }

        _pass.Dispose();
    }

    // The groups of one slot's levels, made empty the first time it is asked for.
    private (ImageGroup Group, ImageView Capture, ImageView Level)[] Groups(int slot)
    {
        while (_groups.Count <= slot)
        {
            var cube = new (ImageGroup, ImageView, ImageView)[_levels];
            for (int mip = 0; mip < _levels; mip++)
            {
                cube[mip] = (new ImageGroup(_gpu, _sampler), default, default);
            }

            _groups.Add(cube);
        }

        return _groups[slot];
    }
}
