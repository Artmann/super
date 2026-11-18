using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The baker every bake runs on, made the first time a bake is prepared, since making it waits for the GPU. A bake job
/// never makes it itself. Nothing is made on a GPU that can't trace rays.
/// </summary>
internal sealed class PreparedBakers(VulkanContext context, EnvironmentMap environment, Func<Baker> makeBaker) : IDisposable
{
    private Baker? _baker;

    /// <summary>The environment a bake lights its area with: its sky and the sampler it is read with.</summary>
    public EnvironmentMap Environment => environment;

    /// <summary>Whether the last bake still holds the baker's pages, so the next can't set up on them yet.</summary>
    public bool PagesClaimed => _baker!.PagesClaimed;

    /// <summary>How many bake jobs are made and not yet disposed, for tests.</summary>
    public int LiveJobs => _baker?.JobPools ?? 0;

    /// <summary>
    /// Makes the baker and, unless <paramref name="probes"/> is false, the probe volume a space's probes fold through.
    /// Waits for the GPU the first time; does nothing once it has run, or when the GPU can't trace rays.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The GPU can trace rays but lacks the storage image formats a bake needs, or, with <paramref name="probes"/>, what
    /// the live probes fold with.
    /// </exception>
    public void Prepare(bool probes)
    {
        if (!context.CanTrace)
        {
            return;
        }

        _baker ??= makeBaker();
        if (probes)
        {
            _baker.EnsureProbeVolume(environment.Sky, environment.Sampler);
        }
    }

    /// <summary>The baker <see cref="Prepare"/> made, and its probe volume when the bake has probes.</summary>
    public (Baker Baker, ProbeVolume? Volume) Prepared(string name, bool probes)
    {
        if (!context.CanTrace)
        {
            throw new InvalidOperationException("Baking needs a GPU that can trace rays, and this one can't.");
        }

        if (_baker is not { } baker || (probes && baker.ProbeVolume is null))
        {
            throw new InvalidOperationException(
                $"The bake of \"{name}\" can't start yet: baking isn't prepared, and preparing it waits for the GPU, which a bake job never does. Prepare baking first, outside a frame.");
        }

        return (baker, baker.ProbeVolume);
    }

    public void Dispose() => _baker?.Dispose();
}
