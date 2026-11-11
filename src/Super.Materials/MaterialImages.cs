namespace Super.Materials;

/// <summary>
/// The pixels of a worked out material, row 0 first. <paramref name="Color"/> and <paramref name="Normal"/> have three
/// floats per pixel and the rest one. The colour has the occlusion multiplied in.
/// Parts made by the same node share one array, for example roughness and metalness both set to one image, so read
/// them and never change them in place.
/// </summary>
public sealed record MaterialImages(
    int Size,
    float Width,
    float[] Color,
    float[] Normal,
    float[] Roughness,
    float[] Metallic,
    float[] Opacity,
    float[] Height)
{
    /// <summary>
    /// How many metres a height of 1 stands above a height of 0: the material's <see cref="Material.Depth"/>,
    /// or 0 when it sets no <see cref="Material.Height"/>, so there's nothing to sink into the surface.
    /// </summary>
    public float HeightDepth { get; init; }
}
