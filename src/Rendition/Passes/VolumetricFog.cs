using System.Numerics;

namespace Rendition.Passes;

/// <summary>
/// The volumetric fog's grid size and the fog settings the shaders get. The grid has one cell
/// for every <see cref="Renderer.VolumetricFogCellSize"/> by that many pixels of the scene and
/// <see cref="Renderer.VolumetricFogSlices"/> slices in depth, split exponentially so the near
/// cells are thin and the far ones deep.
/// </summary>
internal static class VolumetricFog
{
    /// <summary>Scene pixels across one cell, each way, unless a game picks another.</summary>
    public const int CellPixels = 8;

    /// <summary>The fewest scene pixels across a cell.</summary>
    public const int MinCellPixels = 4;

    /// <summary>The most scene pixels across a cell.</summary>
    public const int MaxCellPixels = 32;

    /// <summary>Cells in depth, from the camera out to <see cref="FogSettings.VolumetricDistance"/>, unless a game picks another.</summary>
    public const int Slices = 64;

    /// <summary>The fewest slices in depth.</summary>
    public const int MinSlices = 16;

    /// <summary>The most slices in depth.</summary>
    public const int MaxSlices = 128;

    /// <summary>Where the first slice ends at most, in metres; it ends sooner in a volume under twice this deep.</summary>
    public const float FirstSlice = 1f;

    /// <summary>The cells across, down and deep for a scene of this many pixels, after render scale.</summary>
    public static (int X, int Y, int Z) GridSize(int sceneWidth, int sceneHeight, int cellPixels = CellPixels, int slices = Slices) =>
        ((sceneWidth + cellPixels - 1) / cellPixels, (sceneHeight + cellPixels - 1) / cellPixels, slices);

    /// <summary>Throws unless a cell this many pixels across fits the grid.</summary>
    public static int CheckCellPixels(int value) => value is >= MinCellPixels and <= MaxCellPixels
        ? value
        : throw new ArgumentOutOfRangeException(nameof(value), value, $"A volumetric fog cell can be from {MinCellPixels} to {MaxCellPixels} pixels across.");

    /// <summary>Throws unless the grid can have this many slices.</summary>
    public static int CheckSlices(int value) => value is >= MinSlices and <= MaxSlices
        ? value
        : throw new ArgumentOutOfRangeException(nameof(value), value, $"The volumetric fog can have from {MinSlices} to {MaxSlices} slices.");

    /// <summary>
    /// What the frame block gets: x the distance the volume reaches, y where its first slice ends,
    /// z the anisotropy, w left for the renderer to say whether last frame's volume can be used.
    /// All zero, which draws the plain fog, with the volumetric switch off, a volumetric distance
    /// of 0, while a probe is captured or under water, and when there's nothing to draw: no
    /// scene fog (or its switch off) and no fog volumes (or their switch off).
    /// </summary>
    public static Vector4 Pack(in FogSettings settings, int fogVolumes, RenderEffects off, bool capturingProbe, bool underwater)
    {
        if (capturingProbe || underwater || (off & RenderEffects.VolumetricFog) != 0
            || !(settings.VolumetricDistance > 0f) || !HasFog(settings, fogVolumes, off))
        {
            return Vector4.Zero;
        }

        float distance = settings.VolumetricDistance;
        return new Vector4(distance, MathF.Min(FirstSlice, distance * 0.5f), settings.Anisotropy, 0f);
    }

    /// <summary>Whether the grid has any fog to draw: the scene's own, or fog volumes.</summary>
    public static bool HasFog(in FogSettings settings, int fogVolumes, RenderEffects off) =>
        ((off & RenderEffects.Fog) == 0 && settings.Density > 0f)
        || ((off & RenderEffects.FogVolumes) == 0 && fogVolumes > 0);
}
