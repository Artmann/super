using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The layout of a bake's light probe atlases in a <c>.lightbake</c> file: one 2D page each, <see cref="Columns"/>
/// bordered tiles across, probe i at column i % 64 and row i / 64, so the 4,096 probes a space holds fit in 64 rows.
/// The tiles are the live probes' own, as <see cref="ProbeVolume.RecordFold"/> writes them, but the live atlases are
/// layered and sized by quality, so a bake keeps its own fixed layout. probes.glsl reads both.
/// </summary>
internal static class BakedProbeAtlas
{
    /// <summary>Tiles across each page.</summary>
    public const int Columns = 64;

    /// <summary>Texels across the irradiance page: <see cref="Columns"/> tiles, each with a one-texel border round it.</summary>
    public const int IrradianceAtlasSize = Columns * (ProbeVolume.IrradianceTexels + 2);

    /// <summary>Texels across the depth page.</summary>
    public const int DepthAtlasSize = Columns * (ProbeVolume.DepthTexels + 2);
}
