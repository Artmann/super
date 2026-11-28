using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>
/// An <see cref="AdoptedTarget"/> for each of the targets a stage draws to, found by the target, for a stage that
/// draws to many. <see cref="Clear"/> lets go of them all when the targets are made again.
/// </summary>
internal sealed class AdoptedTargets(VulkanGpu gpu) : IDisposable
{
    private readonly Dictionary<RenderTarget, AdoptedTarget> _targets = [];

    /// <summary>A target for a pass to draw <paramref name="target"/>, which is <paramref name="extent"/> big.</summary>
    public ColorTarget For(RenderTarget target, Extent2D extent, LoadOp load = LoadOp.DontCare)
    {
        if (!_targets.TryGetValue(target, out var adopted))
        {
            adopted = new AdoptedTarget(gpu);
            _targets[target] = adopted;
        }

        return new ColorTarget(adopted.For(target, extent), load, default);
    }

    /// <summary>Lets go of every target, such as when they've been made again and the old ones are gone.</summary>
    public void Clear()
    {
        foreach (var adopted in _targets.Values)
        {
            adopted.Dispose();
        }

        _targets.Clear();
    }

    public void Dispose() => Clear();
}
