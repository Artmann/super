namespace Rendition;

/// <summary>What a <see cref="BakeJob"/> step asks of whoever runs it.</summary>
internal enum BakeStepResult
{
    /// <summary>Submit the step's work and step again whenever suits.</summary>
    Continue,

    /// <summary>
    /// The step copied results into memory the next step reads, so wait until the GPU is done with this step's work
    /// before the next one.
    /// </summary>
    AwaitGpu,

    /// <summary>
    /// The step recorded nothing and started work on the thread pool; step again whenever suits, and the step that
    /// finds the work done records what follows it. A caller that may wait can call <see cref="BakeJob.WaitForCpu"/>.
    /// </summary>
    AwaitCpu,

    /// <summary>Nothing is left to record; <see cref="BakeJob.Finish"/> makes the file.</summary>
    Done,
}

/// <summary>
/// One area to bake, a space or a chunk, everything in the area's own space.
/// </summary>
/// <param name="Draws">Every draw that blocks and bounces light; the job builds its top level from them.</param>
/// <param name="Lights">The lights of the bake, each Mixed one with the mask channel it was assigned.</param>
/// <param name="IsChunk">Whether the paths also sum the sky and sun transfer, on a page of the layout's own side.</param>
/// <param name="Layout">Where each draw sits: from <see cref="LightmapLayout.Build"/> for a space, <see cref="LightmapLayout.BuildSingle"/> for a chunk.</param>
/// <param name="SkyIntensity">What the sky in the plain light is scaled by; 0 leaves it out.</param>
/// <param name="PlainLight">Whether the irradiance and direction pages are kept.</param>
/// <param name="Probes">The space's light probes, or null for a chunk, which has none.</param>
internal sealed record AreaJob(
    DrawItem[] Draws, GpuBakeLight[] Lights, bool IsChunk, LightmapLayout Layout, float SkyIntensity, bool PlainLight,
    ProbeVolumeSettings? Probes);

/// <summary>What a <see cref="BakeJob"/> made: the pages as the file stores them, and what the report needs.</summary>
internal sealed record BakeResult(
    List<LightBakePage>? Irradiance, List<LightBakePage>? Direction, List<LightBakePage> Mask,
    LightBakeTransfer? Transfer, float InvalidShare, int Passes, LightBakePage? ProbeIrradiance,
    LightBakePage? ProbeDepth);
