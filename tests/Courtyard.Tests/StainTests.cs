using Super.Materials;

namespace Courtyard.Tests;

/// <summary>
/// The small stains the decal list lays round the yard: each fades out well inside its image, so the box it is drawn
/// in never shows as a straight edge.
/// </summary>
public sealed class StainTests
{
    private const int Size = 256;

    // The band along each border, 4 % of the image.
    private const int Band = 10;

    public static TheoryData<string, bool> Kinds() => new()
    {
        { "ring", false },
        { "ring", true },
        { "moss", false },
        { "water", false },
        { "boot", false },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Nothing_shows_along_any_of_the_four_borders(string kind, bool square)
    {
        var opacity = GraphRunner.Run(new StainPatch { Kind = kind, Square = square }, Size).Opacity;
        var shown = new List<string>();
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var nearBorder = x < Band || y < Band || x >= Size - Band || y >= Size - Band;
                if (nearBorder && opacity[y * Size + x] > 0f)
                {
                    shown.Add($"({x}, {y}) {opacity[y * Size + x]:0.000}");
                }
            }
        }

        Assert.True(shown.Count == 0, $"{shown.Count} pixels show near the border, such as {string.Join(", ", shown.Take(5))}");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void The_stain_shows_inside_the_border(string kind, bool square)
    {
        // So the border test above is looking at a stain, not an empty image: at least a twentieth of it shows.
        var opacity = GraphRunner.Run(new StainPatch { Kind = kind, Square = square }, Size).Opacity;
        var shown = opacity.Count(value => value > 0.2f);

        Assert.True(shown > Size * Size / 20, $"only {shown} of {Size * Size} pixels show the stain");
    }
}
