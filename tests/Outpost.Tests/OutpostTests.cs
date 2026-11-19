using System.Numerics;
using OutpostExample;
using Super;
using Super.Testing;

namespace Outpost.Tests;

public class OutpostTests
{
    private static HeadlessRun<OutpostGame> Start() => HeadlessGame.Create<OutpostGame>(1, services => services.AddOutpost());

    /// <summary>Holds the waves back, so a test has the road to itself.</summary>
    private static Enemies QuietRoad(HeadlessRun<OutpostGame> run)
    {
        run.Game.Waves.FirstDelay = float.MaxValue;
        run.Game.Waves.Reset();
        return run.Game.Enemies;
    }

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
        var enemies = QuietRoad(run);

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
        var enemies = QuietRoad(run);

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
        var enemies = QuietRoad(run);

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
    public void Clicking_grass_builds_a_tower()
    {
        using var run = Start();
        run.Ticks(1);

        // The camera looks at the middle tile, so the middle of the screen is on it.
        run.Input.Click(MouseButton.Left, at: run.ScreenSize / 2);
        run.Ticks(1);

        var tower = Assert.Single(run.Game.Towers.All);
        Assert.Equal((7, 4), (tower.X, tower.Z));
        Assert.Equal(50, run.Game.Player.Gold);
    }

    [Fact]
    public void Building_needs_grass_and_gold()
    {
        using var run = Start();
        var towers = run.Game.Towers;

        Assert.False(towers.TryBuild(4, 3));
        Assert.True(towers.TryBuild(7, 4));
        Assert.False(towers.TryBuild(7, 4));
        Assert.False(towers.TryBuild(-1, 0));
        Assert.True(towers.TryBuild(3, 2));
        Assert.False(towers.TryBuild(8, 4));

        Assert.Equal(0, run.Game.Player.Gold);
        Assert.Equal(2, towers.All.Count);
    }

    [Fact]
    public void A_tower_by_the_road_kills_enemies()
    {
        using var run = Start();
        var enemies = QuietRoad(run);

        run.Game.Towers.TryBuild(3, 2);
        for (int i = 0; i < 3; i++)
        {
            enemies.Spawn(EnemyKind.Walker);
            run.Ticks(30);
        }

        run.Ticks(30 * 30);

        Assert.Equal(3, enemies.Killed);
        Assert.Equal(10, run.Game.Player.Lives);
    }

    [Fact]
    public void Hitting_a_dead_enemy_again_pays_nothing()
    {
        using var run = Start();
        var enemies = QuietRoad(run);

        var walker = enemies.Spawn(EnemyKind.Walker);
        enemies.Hit(walker, 3);
        enemies.Hit(walker, 1);

        Assert.Equal(110, run.Game.Player.Gold);
        Assert.Equal(1, enemies.Killed);
    }

    [Fact]
    public void A_shot_whose_target_died_vanishes()
    {
        using var run = Start();
        var enemies = QuietRoad(run);

        var walker = enemies.Spawn(EnemyKind.Walker);
        enemies.Hit(walker, 1);
        enemies.Hit(walker, 1);
        // The near shot lands in a few ticks. The far one would take most of a second.
        run.Game.Shots.Fire(walker.Position + new Vector3(1, 0, 0), walker);
        run.Game.Shots.Fire(walker.Position + new Vector3(10, 0, 0), walker);
        run.Ticks(5);

        Assert.Equal(1, enemies.Killed);
        Assert.Empty(run.Game.Shots.All);
    }

    [Fact]
    public void A_tower_ignores_enemies_out_of_range()
    {
        using var run = Start();
        var enemies = QuietRoad(run);

        // The middle tile is about 15 metres from where the road starts.
        run.Game.Towers.TryBuild(7, 4);
        enemies.Spawn(EnemyKind.Walker);
        run.Ticks(1);

        Assert.Empty(run.Game.Shots.All);
    }

    [Fact]
    public void Every_tile_is_drawn()
    {
        using var run = Start();

        run.Ticks(1);

        Assert.Equal(15 * 9, run.Drawn.Count(draw => draw.Key is >= 10_000 and < 20_000));
    }

    [Fact]
    public void Wave_makeup_follows_the_table()
    {
        Assert.Equal(Enumerable.Repeat(EnemyKind.Walker, 5), Waves.Makeup(1));

        var fifth = Waves.Makeup(5);
        Assert.Equal(9, fifth.Count(kind => kind == EnemyKind.Walker));
        Assert.Equal(3, fifth.Count(kind => kind == EnemyKind.Runner));
        Assert.Equal(1, fifth.Count(kind => kind == EnemyKind.Brute));

        var tenth = Waves.Makeup(10);
        Assert.Equal(14, tenth.Count(kind => kind == EnemyKind.Walker));
        Assert.Equal(8, tenth.Count(kind => kind == EnemyKind.Runner));
        Assert.Equal(3, tenth.Count(kind => kind == EnemyKind.Brute));
    }

    [Fact]
    public void A_wave_with_no_towers_costs_lives()
    {
        using var run = Start();

        // 3 seconds before the first walker, and about 23 seconds for it to walk the road.
        run.Ticks(30 * 40);

        Assert.True(run.Game.Player.Lives < 10, $"Lives are still {run.Game.Player.Lives}.");
    }

    [Fact]
    public void The_hud_shows_gold_lives_and_wave()
    {
        using var run = Start();

        run.Ticks(1);

        var shown = run.ShownText.Select(text => text.Text).ToList();
        Assert.Contains("Gold 100", shown);
        Assert.Contains("Lives 10", shown);
        Assert.Contains("Wave 1/10", shown);
    }

    [Fact]
    public void A_click_on_the_hud_builds_nothing()
    {
        using var run = Start();
        // At the usual size the panel sits over the sky. Blown up, as a player might set it, it covers grass.
        run.UiScale = 5f;
        run.Ticks(1);

        var hud = run.FrameOf("hud");
        var middle = new Vector2(hud.X + hud.Width / 2, hud.Y + hud.Height / 2) * run.UiScale;
        var ground = run.Game.Camera.Ray(middle, run.ScreenSize).Ground();
        Assert.True(ground is { } spot && new Field().TileAt(spot) is { } tile && !new Field().IsRoad(tile.X, tile.Z), "The panel should cover grass.");

        run.Input.Click(MouseButton.Left, at: middle);
        run.Ticks(1);

        Assert.Empty(run.Game.Towers.All);
        Assert.Equal(100, run.Game.Player.Gold);
    }
}
