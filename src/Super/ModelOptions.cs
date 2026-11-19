using System.Globalization;
using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// How <see cref="View.Model(int, ModelRef, Matrix4x4, Vector3?)">view.Model</see> draws a model, beyond where it
/// stands. Every part may be left out: a model with no options is drawn as it was made.
/// </summary>
public readonly record struct ModelOptions
{
    /// <summary>
    /// Tints the model, as red, green and blue from 0 to 1. It multiplies the model's own colors and keeps its
    /// textures, and costs nothing extra, so every copy of a model can have its own. The cube is white under a color.
    /// </summary>
    public Vector3? Color { get; init; }

    /// <summary>
    /// Bends the model as it swims. Its head must face +z. Like a color, this costs nothing extra, so every fish can
    /// have its own.
    /// </summary>
    public SwimMotion Swim { get; init; }

    /// <summary>
    /// Shows only some meshes of the model, the ones <see cref="ContentModels.Meshes"/> picked for the mask. Meshes it leaves
    /// out are not drawn, and cost no triangles. The mask must have been made for this model. Null shows them all.
    /// </summary>
    public MeshMask? Meshes { get; init; }

    /// <summary>
    /// Marks a model that never moves, so baked lighting can hold it; see "Static draws" in docs/views.md. A static
    /// model can't swim or take a pose, since a bake holds it where it stands.
    /// </summary>
    public bool Static { get; init; }

    /// <summary>
    /// Fades the model out with distance from the camera, winning over its surfaces' <see cref="Surface.FadeDistance"/>.
    /// </summary>
    public DistanceFade? Fade { get; init; }

    /// <summary>
    /// Throws for a fade that ends before it starts, or for a static model that swims or has a pose, naming the
    /// options as <paramref name="name"/>.
    /// </summary>
    internal void Check(int key, bool posed, string name)
    {
        if (Fade is { } fading && DistanceFade.Problem(fading) is { } problem)
        {
            throw new ArgumentException($"Model {key}'s {problem}", name);
        }

        if (!Static)
        {
            return;
        }

        if (Swim.Amplitude != 0f)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"Model {key} is static, so it can't swim, but its swim has amplitude {Swim.Amplitude}. Draw it without Static, or without the swim."),
                name);
        }

        if (posed)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"Model {key} is static, so it can't move in a pose. Draw it without Static, or without the pose."),
                name);
        }
    }

    /// <summary>Throws if <paramref name="meshes"/> was made for another model than <paramref name="model"/>.</summary>
    internal static void CheckMeshes(MeshMask? meshes, ModelRef model, string name)
    {
        if (meshes is { } mask && mask.ModelIndex != model.Index)
        {
            throw new ArgumentException($"The mesh mask was made for model {mask.ModelIndex}, not model {model.Index}. Make it with content.Models.Meshes for this model.", name);
        }
    }
}
