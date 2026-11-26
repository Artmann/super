using System.Numerics;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// Grid overlays drawn over a plain sky looking down -z, with a grey wall covering the right half of the picture five
// metres away. The grid stands up on the wall: turned a quarter about X, its up faces the camera, its X runs along the
// world's and its Z down the world's -Y. The picture is 60 degrees high and square, so five metres away its middle to
// its edge is 5 tan 30 = 2.887 m. Places are parts of the picture, since the window may hold more pixels than asked for.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class GridOverlayGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 128;

    private int _wide = Size;

    [Fact]
    public void A_filled_cell_colours_the_wall_it_lies_on_and_nothing_else()
    {
        int errors = gpu.ValidationErrors;
        using var wall = TestWall.RightHalf(gpu);

        // Cell (0, -1) at 2 m runs from 0 to 2 along X and 0 to 2 up the wall: across 0.5 to 0.846, down 0.154 to 0.5.
        var plain = Render(wall.Draws, []);
        var drawn = Render(wall.Draws, [Fill(new GridCellWindow((0, -1), 1, 1, 0))]);

        var inCell = At(drawn, 0.67f, 0.33f);
        var belowCell = At(drawn, 0.67f, 0.75f);
        var bareBelow = At(plain, 0.67f, 0.75f);
        output.WriteLine($"In the cell {TestPixels.Show(inCell)}, below it {TestPixels.Show(belowCell)}, the wall alone {TestPixels.Show(bareBelow)}.");

        TestPixels.AssertNear(new Vector3(255f, 0f, 0f), inCell, 2f, "the filled cell on the wall");
        TestPixels.AssertNear(bareBelow, belowCell, 2f, "the wall below the cell");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_cell_over_the_sky_draws_nothing()
    {
        int errors = gpu.ValidationErrors;
        using var wall = TestWall.RightHalf(gpu);

        // Cell (-1, -1) runs from -2 to 0 along X, left of the wall, where there's only sky.
        var plain = Render(wall.Draws, []);
        var drawn = Render(wall.Draws, [Fill(new GridCellWindow((-1, -1), 1, 1, 0))]);

        var sky = At(drawn, 0.33f, 0.33f);
        var bare = At(plain, 0.33f, 0.33f);
        output.WriteLine($"Over the sky {TestPixels.Show(sky)}, the sky alone {TestPixels.Show(bare)}.");
        TestPixels.AssertNear(bare, sky, 2f, "the sky behind the cell");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private static GridOverlayDraw Fill(GridCellWindow cells) => new()
    {
        Grid = Matrix4x4.CreateRotationX(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, 0f, -4.95f),
        Shape = GridShape.Square,
        CellSize = new Vector2(2f),
        Cells = cells,
        Depth = 0.5f,
        FillColor = new Vector4(1f, 0f, 0f, 1f),
    };

    private Vector3 At(byte[] pixels, float across, float down) => TestPixels.Around(pixels, _wide, across, down);

    // A couple of frames first, then the one read back, with every effect off so nothing moves the overlay's colour.
    private byte[] Render(DrawItem[] draws, GridOverlayDraw[] overlays)
    {
        var view = TestSky.Bare(Size);
        view.Extras.GridOverlays = overlays;
        view.Extras.GridCells = overlays.Length == 0 ? [] : [1u];
        return TestSky.Capture(gpu, view, draws, 2, out _wide);
    }
}
