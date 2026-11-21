using System.Globalization;
using Rendition;

namespace Super;

/// <summary>How models are drawn: their detail with distance, how far grass reaches, culling, the depth-first pass and fading.</summary>
public sealed class GraphicsDrawing
{
    /// <summary>
    /// How long models keep their finer versions as they move away, from 0.5 to 2. At 1 a model switches to a simpler
    /// version once that would stray by a pixel on screen. At 0.5 it may stray by two pixels, so it switches
    /// sooner and fewer triangles are drawn; at 2 it keeps the finer one longer. Shadows follow in step.
    /// </summary>
    public float MeshDetail
    {
        get;
        set => field = value is >= RenderSettings.MinMeshDetail and <= RenderSettings.MaxMeshDetail
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, string.Create(CultureInfo.InvariantCulture,
                $"Mesh detail can be from {RenderSettings.MinMeshDetail} to {RenderSettings.MaxMeshDetail}."));
    } = 1f;

    /// <summary>
    /// How far <see cref="Super.Grass"/> grows from the camera, from 0.25 to 2. At 1 its densest blades reach 16 metres
    /// and its sparsest 120. The blades drawn go as its square, so 0.5 draws about a quarter as many.
    /// <see cref="GraphicsQuality.Low"/> sets 0.5, <see cref="GraphicsQuality.Medium"/> 0.75 and
    /// <see cref="GraphicsQuality.Ultra"/> 1.25.
    /// </summary>
    public float GrassDetail
    {
        get;
        set => field = value is >= RenderSettings.MinGrassDetail and <= RenderSettings.MaxGrassDetail
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, string.Create(CultureInfo.InvariantCulture,
                $"Grass detail can be from {RenderSettings.MinGrassDetail} to {RenderSettings.MaxGrassDetail}."));
    } = 1f;

    /// <summary>
    /// Leaving out models the camera, the sun's cascades and the lights' shadow tiles can't see. Off, every model is drawn
    /// into each of them, which only costs time: it's there to check that culling hides nothing it shouldn't. Every
    /// preset leaves it on.
    /// </summary>
    public bool Culling { get; set; } = true;

    /// <summary>
    /// Models switching to their simpler versions as they move away, as <see cref="MeshDetail"/> tunes. Off, every model
    /// is drawn at full detail at any distance, which only costs time: it's there to check what the simpler versions
    /// change. Every preset leaves it on.
    /// </summary>
    public bool SimplerModels { get; set; } = true;

    /// <summary>
    /// Drawing the depth of large solid surfaces before shading anything, so each pixel is shaded once, by the surface
    /// that shows. Off, surfaces drawn before the ones that hide them are shaded too, which only costs time: it's
    /// there to measure what the extra pass saves. The picture is the same either way, but where two surfaces lie in
    /// the very same plane, where the other of the two may show. Every preset leaves it on.
    /// </summary>
    public bool DepthFirst { get; set; } = true;

    /// <summary>
    /// Things fading out smoothly with distance, from <see cref="Surface.FadeDistance"/> and a model's own fade, models
    /// cross-fading into their simpler versions instead of popping, and glass fading where it meets the ground, from
    /// <see cref="Surface.SoftEdge"/>. Off, a fade is a hard cut halfway through it, a simpler version swaps in at once
    /// and glass ends in a hard line. It costs little: fading things skip the early depth test, and a model near a swap
    /// is drawn twice. Every preset leaves it on.
    /// </summary>
    public bool Fades { get; set; } = true;

    /// <summary>Takes every setting from <paramref name="other"/>.</summary>
    internal void CopyFrom(GraphicsDrawing other)
    {
        MeshDetail = other.MeshDetail;
        GrassDetail = other.GrassDetail;
        Culling = other.Culling;
        SimplerModels = other.SimplerModels;
        DepthFirst = other.DepthFirst;
        Fades = other.Fades;
    }
}
