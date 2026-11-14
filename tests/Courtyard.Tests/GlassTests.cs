using System.Runtime.CompilerServices;
using Super.Materials;

namespace Courtyard.Tests;

/// <summary>
/// The window's glass: its panes in the glass graph line up with the glazing bars of the model's window, so the dust
/// packed into each pane's corners sits in the corners the bars make.
/// </summary>
[Trait("Speed", "Slow")]
public sealed class GlassTests
{
    // The window in art/courtyard.py: WIN_X 2.4 and WIN_W 2.0 put the opening from 1.4 to 3.4 m across, and the frame,
    // 7.5 cm wide, leaves the glass from 1.475 to 3.325 m, in three columns 0.6167 m wide. The sill at 1.15 m plus
    // 2 cm and the frame put the bottom bar's top at 1.245 m, and five rows of 0.461 m reach SPRING_Z, 3.55 m.
    private const float Left = 1.475f;
    private const float Bottom = 1.245f;
    private static readonly float[] Bars = [2.0917f, 2.7083f];
    private static readonly float[] Rails = [1.706f, 2.167f, 2.628f, 3.089f];

    // The glass graph is 3.7 m wide.
    private const float Width = 3.7f;
    private const int Size = 1024;

    private static string Models([CallerFilePath] string here = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "examples", "Courtyard", "content", "models"));
    }

    [Fact]
    public void The_glass_measures_its_uvs_in_metres_from_the_frames_inner_corner()
    {
        var (positions, uvs) = Glb.Mesh(Path.Combine(Models(), "courtyard.glb"), "cy_glass");

        Assert.NotEmpty(positions);
        for (int i = 0; i < positions.Count; i++)
        {
            // glTF's y is up, and its v runs down from 1, so 1 - v is metres up.
            Assert.Equal(positions[i].X - Left, uvs[i].X, 0.0005f);
            Assert.Equal(positions[i].Y - Bottom, 1f - uvs[i].Y, 0.0005f);
        }
    }

    [Fact]
    public void Dust_packs_into_the_corners_where_the_bars_cross_not_the_middles_of_the_panes()
    {
        var opacity = GraphRunner.Run(new Glass(), Size).Opacity;

        // Where a bar crosses a rail, four panes meet, each with dust packed into its corner. 3 cm from the crossing
        // along each diagonal is just inside each pane's corner, past the gap under the bars. A pane's middle, half a
        // pane from each, is the cleanest of the glass.
        var corners = new List<float>();
        var middles = new List<float>();
        foreach (float x in Bars)
        {
            foreach (float z in Rails)
            {
                foreach (var (dx, dz) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
                {
                    corners.Add(Around(opacity, x + dx * 0.03f, z + dz * 0.03f));
                }

                middles.Add(Around(opacity, x + 0.6167f / 2f, z + 0.461f / 2f));
            }
        }

        Assert.True(corners.Average() > middles.Average() + 0.15f,
            $"The pane corners at the crossings are {corners.Average():0.000} opaque and the middles {middles.Average():0.000}.");
    }

    // The mean opacity in a 5 by 5 pixel box round the point on the glass x metres across and z up.
    private static float Around(float[] opacity, float x, float z)
    {
        // The cook divides the metres by the graph's width; the image's rows run down the window from the top.
        int column = (int)((x - Left) / Width * Size);
        int row = (int)((1f - (z - Bottom) / Width) * Size);
        float sum = 0f;
        for (int y = row - 2; y <= row + 2; y++)
        {
            for (int c = column - 2; c <= column + 2; c++)
            {
                sum += opacity[((y % Size + Size) % Size) * Size + (c % Size + Size) % Size];
            }
        }

        return sum / 25f;
    }
}
