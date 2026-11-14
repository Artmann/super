using System.Numerics;

namespace Rendition.Passes;

/// <summary>
/// The volumetric fog's grid size and the fog settings the shaders get. The grid has
/// one cell for every 8 by 8 pixels of the scene and <see cref="Slices"/> slices in depth, split
/// exponentially so the near cells are thin and the far ones deep.
/// </summary>
internal static class VolumetricFog
{
    /// <summary>Scene pixels across one cell, each way.</summary>
    public const int CellPixels = 8;

    /// <summary>Cells in depth, from the camera out to <see cref="FogSettings.VolumetricDistance"/>.</summary>
    public const int Slices = 64;

    /// <summary>Where the first slice ends at most, in metres; it ends sooner in a volume under twice this deep.</summary>
    public const float FirstSlice = 1f;

    /// <summary>The cells across, down and deep for a scene of this many pixels, after render scale.</summary>
    public static (int X, int Y, int Z) GridSize(int sceneWidth, int sceneHeight) =>
        ((sceneWidth + CellPixels - 1) / CellPixels, (sceneHeight + CellPixels - 1) / CellPixels, Slices);

    /// <summary>
    /// What the frame block gets: x the distance the volume reaches, y where its first slice ends,
    /// z the anisotropy, w left for the renderer to say whether last frame's volume can be used.
    /// All zero, which draws the plain fog, with either fog switch off, no fog, a volumetric
    /// distance of 0, while a probe is captured or under water.
    /// </summary>
    public static Vector4 Pack(in FogSettings settings, RenderEffects off, bool capturingProbe, bool underwater)
    {
        if (capturingProbe || underwater || (off & (RenderEffects.Fog | RenderEffects.VolumetricFog)) != 0
            || !(settings.Density > 0f) || !(settings.VolumetricDistance > 0f))
        {
            return Vector4.Zero;
        }

        float distance = settings.VolumetricDistance;
        return new Vector4(distance, MathF.Min(FirstSlice, distance * 0.5f), settings.Anisotropy, 0f);
    }
}
