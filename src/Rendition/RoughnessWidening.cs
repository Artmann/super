using System.Numerics;

namespace Rendition;

/// <summary>Keeps a surface rough in the distance where its normal map's bumps get too small to see.</summary>
public static class RoughnessWidening
{
    /// <summary>
    /// A copy of a surface texture (roughness in green) whose smaller levels are rougher where
    /// the normal map's bumps get too small to see. Averaging bumpy normals leaves a flat one,
    /// which would turn a rough surface smooth and shiny in the distance. How much the normals
    /// disagree is kept instead, as extra roughness, so the highlight stays as broad as the
    /// bumps made it. The full-size level is left as it is. Returns the surface unchanged when
    /// the two aren't the same size.
    /// </summary>
    public static TextureData Widen(TextureData surface, TextureData normal)
    {
        if (surface.Width != normal.Width || surface.Height != normal.Height || surface.Mips.Length != normal.Mips.Length
            || surface.Format != TextureFormat.Rgba8 || normal.Format != TextureFormat.Rgba8)
        {
            return surface;
        }

        // The average of the full-size normals under each pixel of each level, left at the
        // length it comes out at: the shorter, the more the normals under it disagree.
        var average = UnitNormals(normal.Mips[0], surface.Width * surface.Height);
        var mips = new byte[surface.Mips.Length][];
        mips[0] = surface.Mips[0];
        int width = surface.Width;
        int height = surface.Height;
        for (int level = 1; level < mips.Length; level++)
        {
            int nextWidth = Math.Max(width / 2, 1);
            int nextHeight = Math.Max(height / 2, 1);
            average = AverageNormals(average, width, height, nextWidth, nextHeight);
            width = nextWidth;
            height = nextHeight;

            byte[] pixels = (byte[])surface.Mips[level].Clone();
            for (int i = 0; i < width * height; i++)
            {
                float roughness = Widened(pixels[i * 4 + 1] / 255f, average[i].Length());
                pixels[i * 4 + 1] = (byte)MathF.Round(Math.Clamp(roughness, 0f, 1f) * 255f);
            }

            mips[level] = pixels;
        }

        return new TextureData { Width = surface.Width, Height = surface.Height, Kind = surface.Kind, Mips = mips };
    }

    /// <summary>
    /// The roughness that gives as broad a highlight as <paramref name="roughness"/> does on
    /// bumps whose unit normals average to <paramref name="averageLength"/>. The normals'
    /// spread is taken as a von Mises-Fisher lobe, whose sharpness follows from the length,
    /// and its slopes add to those of the GGX alpha, which is roughness squared.
    /// </summary>
    public static float Widened(float roughness, float averageLength)
    {
        float r = Math.Clamp(averageLength, 0f, 1f);
        if (r >= 0.9999f)
        {
            return roughness;
        }

        float kappa = (3f * r - r * r * r) / (1f - r * r);
        float alpha = roughness * roughness;
        float widened = kappa > 0f ? MathF.Sqrt(alpha * alpha + 2f / kappa) : 1f;
        return MathF.Sqrt(MathF.Min(widened, 1f));
    }

    // The first pixels of a normal map's level as unit normals, with one straight out of the surface for a pixel that has no direction.
    private static Vector3[] UnitNormals(byte[] level, int pixels)
    {
        var normals = new Vector3[pixels];
        for (int i = 0; i < normals.Length; i++)
        {
            var n = new Vector3(level[i * 4], level[i * 4 + 1], level[i * 4 + 2]) / 255f * 2f - Vector3.One;
            normals[i] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
        }

        return normals;
    }

    // Each pixel of the smaller level is the plain average of the ones it covers, which keeps
    // the shortening that tells how much they disagree.
    private static Vector3[] AverageNormals(Vector3[] source, int width, int height, int nextWidth, int nextHeight)
    {
        var result = new Vector3[nextWidth * nextHeight];
        int stepX = width / nextWidth;
        int stepY = height / nextHeight;
        for (int y = 0; y < nextHeight; y++)
        {
            for (int x = 0; x < nextWidth; x++)
            {
                var sum = Vector3.Zero;
                for (int offsetY = 0; offsetY < stepY; offsetY++)
                {
                    for (int offsetX = 0; offsetX < stepX; offsetX++)
                    {
                        sum += source[x * stepX + offsetX + (y * stepY + offsetY) * width];
                    }
                }

                result[x + y * nextWidth] = sum / (stepX * stepY);
            }
        }

        return result;
    }
}
