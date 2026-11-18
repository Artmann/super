namespace Rendition;

/// <summary>
/// What <see cref="SumsReadBack.Read"/> found for one page, over the region (<see cref="X"/>, <see cref="Y"/>,
/// <see cref="Width"/>, <see cref="Height"/>) its rectangles cover. Each array runs row by row over the region. The
/// irradiance and mask are means over the passes, four floats a texel, with the variance of the irradiance's mean
/// luminance in irradiance w; the direction is the luminance-weighted sum of directions in xyz and the weight in w.
/// After <see cref="Baker.FilterAndDilate"/> irradiance w is 1 where a texel holds light and the direction is a mean
/// direction with w 1. <see cref="Filled"/> marks the texels that hold light: the valid ones as traced,
/// and also the ones dilation filled after <see cref="Baker.FilterAndDilate"/>.
/// </summary>
internal sealed record TracedPage(
    int Page, int X, int Y, int Width, int Height, float[] Irradiance, float[] Direction, float[] Mask, bool[] Covered,
    bool[] Valid, bool[] Filled)
{
    /// <summary>
    /// Only for a chunk: the means of its transfer, <see cref="BakeGpu.TransferFloats"/> floats a texel. The sky's four
    /// coefficients for red, green and blue, the sun's four, then the sun's colour in rgb with w unused.
    /// </summary>
    public float[]? Transfer { get; init; }
}
