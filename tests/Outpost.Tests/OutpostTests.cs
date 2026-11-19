using System.Numerics;
using OutpostExample;
using Super;
using Super.Testing;

namespace Outpost.Tests;

public class OutpostTests
{
    private static HeadlessRun<OutpostGame> Start() => HeadlessGame.Create<OutpostGame>(1, services => services.AddOutpost());

    [Fact]
    public void The_middle_tile_sits_at_the_origin()
    {
        var field = new Field();

        Assert.Equal(Vector3.Zero, field.Center(7, 4));
        Assert.Equal(new Vector3(-14, 0, -8), field.Center(0, 0));
    }

    [Fact]
    public void A_point_finds_its_tile()
    {
        var field = new Field();

        Assert.Equal<(int, int)?>((7, 4), field.TileAt(new Vector3(0.9f, 0, -0.9f)));
        Assert.Equal<(int, int)?>((0, 8), field.TileAt(new Vector3(-14.9f, 0, 8.9f)));
        Assert.Null(field.TileAt(new Vector3(-15.1f, 0, 0)));
        Assert.Null(field.TileAt(new Vector3(0, 0, 9.1f)));
    }

    [Fact]
    public void Every_tile_is_drawn()
    {
        using var run = Start();

        run.Ticks(1);

        Assert.Equal(15 * 9, run.Drawn.Count(draw => draw.Key is >= 10_000 and < 20_000));
    }
}
