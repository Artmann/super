using System.Numerics;
using OutpostExample;
using Super;
using Super.Testing;
using Shot = OutpostExample.Shot;

namespace Outpost.Tests;

public class OutpostTests
{
    /// <summary>A game on its title screen.</summary>
    private static HeadlessRun<OutpostGame> Open(int seed = 1) => HeadlessGame.Create<OutpostGame>(seed, services => services.AddOutpost());

    /// <summary>A game past the title screen, playing.</summary>
    private static HeadlessRun<OutpostGame> Start(int seed = 1)
    {
        var run = Open(seed);
        run.States<Screen>().Set(Screen.Playing);
        run.Ticks(1);
        return run;
    }

    /// <summary>Holds the waves back, so a test has the road to itself.</summary>
    private static Enemies QuietRoad(HeadlessRun<OutpostGame> run)
    {
        run.Game.Waves.FirstDelay = float.MaxValue;
        run.Game.Waves.Reset();
        return run.Game.Enemies;
    }

    private static List<T> All<T>(World world) where T : struct
    {
        var found = new List<T>();
        foreach (var row in world.Query<T>())
        {
            found.Add(row.First);
        }

        return found;
    }

    private static int Count<T>(World world) where T : struct => All<T>(world).Count;

    /// <summary>Takes every enemy off the road, as if the towers had got them all.</summary>
    private static void Clear(World world)
    {
        var enemies = new List<Entity>();
        foreach (var row in world.Query<Enemy>())
        {
            enemies.Add(row.Entity);
        }

        foreach (var enemy in enemies)
        {
            world.Destroy(enemy);
        }
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
    public void Enemies_stay_on_the_road_round_the_corners()
    {
        var field = new Field();

        // An enemy walks up to 0.3 metres to the side, and its body is about 0.2 wide on top of that.
        for (float distance = 0f; distance <= field.Road.Length; distance += 0.1f)
        {
            var pose = field.Road.FrameAtDistance(distance);
            foreach (float side in new[] { -0.5f, 0f, 0.5f })
            {
                var point = pose.Position + pose.Right * side;
                var tile = field.TileAt(point);
                Assert.True(tile is { } t && field.IsRoad(t.X, t.Z), $"At {distance:0.0} m, {side} m to the side, {point} is off the road.");
            }
        }
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
        Assert.Equal(0, enemies.Count);
    }

    [Fact]
    public void A_runner_gets_there_first()
    {
        using var run = Start();
        var enemies = QuietRoad(run);

        var walker = enemies.Spawn(EnemyKind.Walker);
        var runner = enemies.Spawn(EnemyKind.Runner);
        run.Ticks(30 * 13);

        Assert.False(run.World.IsAlive(runner));
        Assert.True(run.World.IsAlive(walker));
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
        Assert.Equal(1, run.Game.Player.Killed);
        Assert.Equal(0, enemies.Count);
    }

    [Fact]
    public void Clicking_grass_builds_a_tower()
    {
        using var run = Start();
        run.Ticks(1);

        // The camera looks at the middle tile, so the middle of the screen is on it.
        run.Input.Click(MouseButton.Left, at: run.ScreenSize / 2);
        run.Ticks(1);

        var tower = Assert.Single(All<Tower>(run.World));
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
        Assert.Equal(2, Count<Tower>(run.World));
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

        Assert.Equal(3, run.Game.Player.Killed);
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
        Assert.Equal(1, run.Game.Player.Killed);
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
        var at = run.World.Get<Transform>(walker).Position;
        run.Game.Shots.Fire(at + new Vector3(1, 0, 0), walker);
        run.Game.Shots.Fire(at + new Vector3(10, 0, 0), walker);
        run.Ticks(5);

        Assert.Equal(1, run.Game.Player.Killed);
        Assert.Equal(0, Count<Shot>(run.World));
    }

    [Fact]
    public void Two_shots_landing_together_pay_one_reward()
    {
        using var run = Start();
        var enemies = QuietRoad(run);

        var runner = enemies.Spawn(EnemyKind.Runner);
        enemies.Hit(runner, 1);
        // Both land on the next tick, while the runner is still in the world until the shots are done.
        var at = run.World.Get<Transform>(runner).Position;
        run.Game.Shots.Fire(at, runner);
        run.Game.Shots.Fire(at, runner);
        run.Ticks(1);

        Assert.Equal(1, run.Game.Player.Killed);
        Assert.Equal(106, run.Game.Player.Gold);
        Assert.False(run.World.IsAlive(runner));
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

        Assert.Equal(0, Count<Shot>(run.World));
    }

    [Fact]
    public void Every_tile_is_drawn()
    {
        using var run = Start();

        run.Ticks(1);

        Assert.Equal(15 * 9, run.Drawn.Count(draw => draw.Model == run.Game.Look.Tile));
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

        Assert.Equal(0, Count<Tower>(run.World));
        Assert.Equal(100, run.Game.Player.Gold);
    }

    [Fact]
    public void The_game_opens_on_the_title()
    {
        using var run = Open();

        run.Ticks(1);
        Assert.Equal(Screen.Title, run.States<Screen>().Current);
        Assert.Contains(run.ShownText, text => text.Text == "Outpost");

        run.Ticks(30 * 10);
        Assert.Equal(0, run.Game.Enemies.Count);
    }

    [Fact]
    public void Starting_with_a_click_builds_nothing()
    {
        using var run = Open();
        run.Ticks(1);

        // The middle of the screen is grass, so a click there while playing would build.
        run.Input.Click(MouseButton.Left, at: run.ScreenSize / 2);
        run.Ticks(1);

        Assert.Equal(Screen.Playing, run.States<Screen>().Current);
        Assert.Equal(0, Count<Tower>(run.World));
    }

    [Fact]
    public void Losing_every_life_ends_the_game()
    {
        using var run = Start();

        run.Game.Player.Lives = 1;
        run.Ticks(30 * 40);

        Assert.Equal(Screen.Lost, run.States<Screen>().Current);
        Assert.Contains(run.ShownText, text => text.Text == "The outpost fell");
    }

    [Fact]
    public void Restarting_mid_wave_starts_clean()
    {
        using var run = Start();
        var game = run.Game;
        Assert.True(game.Towers.TryBuild(3, 2));
        run.Ticks(30 * 20);
        Assert.NotEqual(0, game.Enemies.Count);
        Assert.NotEqual(0, Count<Shot>(run.World));

        game.Player.Lives = 0;
        run.Ticks(1);
        Assert.Equal(Screen.Lost, run.States<Screen>().Current);

        run.Input.Press(Control.Restart);
        run.Ticks(1);

        Assert.Equal(Screen.Playing, run.States<Screen>().Current);
        Assert.Equal(100, game.Player.Gold);
        Assert.Equal(10, game.Player.Lives);
        Assert.Equal(0, Count<Tower>(run.World));
        Assert.Equal(0, game.Enemies.Count);
        Assert.Equal(0, Count<Shot>(run.World));
        Assert.Equal(1, game.Waves.Number);
    }

    [Fact]
    public void Winning_needs_every_enemy_gone()
    {
        using var run = Start();
        var waves = run.Game.Waves;
        waves.Gap = 1f / 30f;
        waves.Pause = 1f / 30f;

        for (int tick = 0; tick < 30 * 60 && !waves.AllSpawned; tick++)
        {
            run.Ticks(1);
        }

        Assert.True(waves.AllSpawned);
        Assert.NotEqual(0, run.Game.Enemies.Count);
        run.Ticks(1);
        Assert.Equal(Screen.Playing, run.States<Screen>().Current);

        Clear(run.World);
        run.Ticks(1);

        Assert.Equal(Screen.Won, run.States<Screen>().Current);
        Assert.Contains(run.ShownText, text => text.Text == "You held the outpost!");
    }

    [Fact]
    public void Winning_needs_the_last_wave_fully_spawned()
    {
        using var run = Start();
        var waves = run.Game.Waves;
        waves.Gap = 1f / 30f;
        waves.Pause = 1f / 30f;

        for (int tick = 0; tick < 30 * 60 && waves.Number < Waves.Count; tick++)
        {
            run.Ticks(1);
        }

        // The last wave has begun, but most of it is still waiting to come out.
        Assert.Equal(Waves.Count, waves.Number);
        Assert.False(waves.AllSpawned);

        Clear(run.World);
        run.Ticks(1);

        Assert.Equal(Screen.Playing, run.States<Screen>().Current);
    }

    [Theory, Seeds(10)]
    public void Gold_never_goes_negative(int seed)
    {
        using var run = Start(seed);
        run.Invariant("gold never goes below zero", game => game.Player.Gold >= 0);

        // Click somewhere new on the field every second, far more often than the player can pay.
        for (int second = 0; second < 120; second++)
        {
            var spot = new Vector2(250 + second % 9 * 100, 250 + second / 9 % 4 * 100);
            run.Input.Click(MouseButton.Left, at: spot);
            run.Ticks(30);
        }

        Assert.NotEqual(0, Count<Tower>(run.World));
    }
}
