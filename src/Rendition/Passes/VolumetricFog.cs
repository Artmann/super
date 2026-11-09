using System.Numerics;

namespace Rendition.Passes;

/// <summary>
/// The volumetric fog's grid and sums, worked out the way volumetric_inject.comp,
/// volumetric_integrate.comp and volumetric.glsl do, so they can be tested here. The grid has
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

    /// <summary>Of each cell's light, the share that comes from last frame's, reprojected.</summary>
    public const float History = 0.9f;

    /// <summary>The cells across, down and deep for a scene of this many pixels, after render scale.</summary>
    public static (int X, int Y, int Z) GridSize(int sceneWidth, int sceneHeight) =>
        ((sceneWidth + CellPixels - 1) / CellPixels, (sceneHeight + CellPixels - 1) / CellPixels, Slices);

    /// <summary>
    /// How far from the camera, as view depth in metres, slice boundary <paramref name="slice"/> lies:
    /// <paramref name="near"/> at 0 and <paramref name="far"/> at <paramref name="slices"/>, each
    /// slice the same ratio deeper than the one before. Cell 0 runs from the camera to boundary 1.
    /// </summary>
    public static float SliceDepth(int slice, int slices, float near, float far) =>
        near * MathF.Pow(far / near, (float)slice / slices);

    /// <summary>The slice boundary, with a fraction, that lies at view depth <paramref name="depth"/>.</summary>
    public static float SliceOf(float depth, int slices, float near, float far) =>
        MathF.Log(depth / near) / MathF.Log(far / near) * slices;

    /// <summary>
    /// Henyey-Greenstein: the share of light, per steradian, turned by an angle whose cosine is
    /// <paramref name="cosTheta"/>, where 1 is straight on. Positive anisotropy scatters onwards.
    /// </summary>
    public static float Phase(float cosTheta, float anisotropy)
    {
        float g2 = anisotropy * anisotropy;
        float denominator = 1f + g2 - 2f * anisotropy * cosTheta;
        return (1f - g2) / (4f * MathF.PI * denominator * MathF.Sqrt(denominator));
    }

    /// <summary>
    /// The front-to-back sum along one column, as volumetric_integrate.comp makes it. Each cell
    /// holds the light its air takes on in xyz and how much light it takes per metre in w, and
    /// <paramref name="lengths"/> how many metres the view ray spends in it. A cell that hides a
    /// share of what's behind it shows that share of its own light, as the plain fog does.
    /// Returns the light added in front of the last cell's far side in xyz and the share of the
    /// light behind that gets through in w.
    /// </summary>
    public static Vector4 Integrate(ReadOnlySpan<Vector4> cells, ReadOnlySpan<float> lengths)
    {
        var light = Vector3.Zero;
        float kept = 1f;
        for (int i = 0; i < cells.Length; i++)
        {
            float through = MathF.Exp(-cells[i].W * lengths[i]);
            light += kept * (1f - through) * new Vector3(cells[i].X, cells[i].Y, cells[i].Z);
            kept *= through;
        }

        return new Vector4(light, kept);
    }

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
