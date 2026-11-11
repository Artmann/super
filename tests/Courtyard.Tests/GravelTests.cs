using Super.Materials;

namespace Courtyard.Tests;

/// <summary>
/// The Courtyard's gravel decal: a spill that fades out well inside its image, so nothing shows of the square it is
/// drawn in.
/// </summary>
public sealed class GravelTests
{
    private const int Size = 256;

    // The band along each border, 4 % of the image, about 2 cm of the half-metre decal.
    private const int Band = 10;

    private static readonly Lazy<MaterialImages> Cooked = new(() => GraphRunner.Run(new Gravel(), Size));

    [Fact]
    public void Nothing_shows_along_any_of_the_four_borders()
    {
        var opacity = Cooked.Value.Opacity;
        var shown = new List<(int X, int Y, float Opacity)>();
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var nearBorder = x < Band || y < Band || x >= Size - Band || y >= Size - Band;
                if (nearBorder && opacity[y * Size + x] > 0f)
                {
                    shown.Add((x, y, opacity[y * Size + x]));
                }
            }
        }

        Assert.Empty(shown);
    }

    [Fact]
    public void At_least_a_tenth_of_the_image_shows_the_spill()
    {
        // So the border test above is looking at a spill, not an empty image.
        var shown = Cooked.Value.Opacity.Count(value => value > 0.5f);

        Assert.True(shown > Size * Size / 10, $"only {shown} of {Size * Size} pixels show the spill");
    }
}
