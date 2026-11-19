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
    public void The_road_runs_from_the_left_edge_to_the_base()
    {
        var field = new Field();

        Assert.True(field.IsRoad(0, 1));
        Assert.True(field.IsRoad(4, 3));
        Assert.True(field.IsRoad(7, 6));
        Assert.True(field.IsRoad(10, 4));
        Assert.True(field.IsRoad(14, 2));
        Assert.False(field.IsRoad(7, 4));
        Assert.False(field.IsRoad(3, 2));

        var end = field.Road.PointAtDistance(field.Road.Length);
        Assert.Equal(field.Center(14, 2).X, end.X, 0.01f);
        Assert.Equal(field.Center(14, 2).Y, end.Y, 0.01f);
        Assert.Equal(field.Center(14, 2).Z, end.Z, 0.01f);
    }

    [Fact]
    public void The_road_is_46_metres_long()
    {
        var field = new Field();

        // 8 + 10 + 12 + 8 + 8 metres between the corners, a little less where the curve cuts them.
        Assert.InRange(field.Road.Length, 44f, 48f);
    }

    [Fact]
    public void A_walker_reaching_the_base_costs_a_life()
    {
        using var run = Start();
        var enemies = run.Game.Enemies;
        enemies.Clear();

        enemies.Spawn(EnemyKind.Walker);
        // The road is about 46 metres and a walker does 2 metres a second, so 25 seconds is plenty.
        run.Ticks(30 * 25);

        Assert.Equal(9, run.Game.Player.Lives);
        Assert.Empty(enemies.All);
    }

    [Fact]
    public void A_runner_gets_there_first()
    {
        using var run = Start();
        var enemies = run.Game.Enemies;
        enemies.Clear();

        var walker = enemies.Spawn(EnemyKind.Walker);
        var runner = enemies.Spawn(EnemyKind.Runner);
        run.Ticks(30 * 13);

        Assert.DoesNotContain(runner, enemies.All);
        Assert.Contains(walker, enemies.All);
    }

    [Fact]
    public void Hitting_to_zero_pays_the_reward()
    {
        using var run = Start();
        var enemies = run.Game.Enemies;
        enemies.Clear();

        var walker = enemies.Spawn(EnemyKind.Walker);
        enemies.Hit(walker, 1);
        enemies.Hit(walker, 1);
        Assert.Equal(100, run.Game.Player.Gold);
        enemies.Hit(walker, 1);

        Assert.Equal(110, run.Game.Player.Gold);
        Assert.Equal(1, enemies.Killed);
        Assert.Empty(enemies.All);
    }

    [Fact]
    public void Every_tile_is_drawn()
    {
        using var run = Start();

        run.Ticks(1);

        Assert.Equal(15 * 9, run.Drawn.Count(draw => draw.Key is >= 10_000 and < 20_000));
    }
}
