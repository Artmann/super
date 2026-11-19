using Super.Testing;

namespace Super.Tests;

public class SceneTests
{
    private record struct Piece(int Value);

    private sealed class Empty : IGame
    {
        public void Update(GameTime time)
        {
        }
    }

    /// <summary>Asks for a scene on tick 3, and notes whether it was built by the end of that Update.</summary>
    private sealed class Door(Scenes scenes) : IGame
    {
        public bool BuiltDuringUpdate { get; private set; }

        public int Builds { get; private set; }

        public void Update(GameTime time)
        {
            if (time.Tick == 3)
            {
                scenes.Go(scene =>
                {
                    Builds++;
                    scene.Spawn(new Piece(1));
                });
                BuiltDuringUpdate = Builds > 0;
            }
        }
    }

    private static int Count(World world, int value)
    {
        int count = 0;
        foreach (var row in world.Query<Piece>())
        {
            if (row.First.Value == value)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void Go_builds_the_scene_at_the_end_of_the_tick()
    {
        using var run = HeadlessGame.Create<Door>(1);

        run.Ticks(4);

        Assert.False(run.Game.BuiltDuringUpdate);
        Assert.Equal(1, run.Game.Builds);
        Assert.Equal(1, Count(run.World, 1));
    }

    [Fact]
    public void Go_destroys_the_old_scene_before_building_the_new_one()
    {
        using var run = HeadlessGame.Create<Empty>(1);
        int seenOld = -1;
        run.Scenes.Go(scene => scene.Spawn(new Piece(1)));
        run.Ticks(1);

        run.Scenes.Go(scene =>
        {
            seenOld = Count(scene.World, 1);
            scene.Spawn(new Piece(2));
        });
        run.Ticks(1);

        Assert.Equal(0, seenOld);
        Assert.Equal(0, Count(run.World, 1));
        Assert.Equal(1, Count(run.World, 2));
    }

    [Fact]
    public void Entities_spawned_on_the_world_outlive_every_scene()
    {
        using var run = HeadlessGame.Create<Empty>(1);
        var party = run.World.Spawn(new Piece(9));
        run.Scenes.Go(scene => scene.Spawn(new Piece(1)));
        run.Ticks(1);

        run.Scenes.Go(scene => scene.Spawn(new Piece(2)));
        run.Ticks(1);

        Assert.True(run.World.IsAlive(party));
    }

    [Fact]
    public void A_world_child_of_a_scene_entity_goes_with_the_scene()
    {
        using var run = HeadlessGame.Create<Empty>(1);
        Entity door = default;
        run.Scenes.Go(scene => door = scene.Spawn(new Piece(1)));
        run.Ticks(1);
        var handle = run.World.Spawn(new Piece(3), new Parent(door));

        run.Scenes.Go(_ => { });
        run.Ticks(1);

        Assert.False(run.World.IsAlive(handle));
    }

    [Fact]
    public void The_last_Go_in_a_tick_wins()
    {
        using var run = HeadlessGame.Create<Empty>(1);

        run.Scenes.Go(scene => scene.Spawn(new Piece(1)));
        run.Scenes.Go(scene => scene.Spawn(new Piece(2)));
        run.Ticks(1);

        Assert.Equal(0, Count(run.World, 1));
        Assert.Equal(1, Count(run.World, 2));
    }

    [Fact]
    public void Rebuild_runs_the_current_build_again()
    {
        using var run = HeadlessGame.Create<Empty>(1);
        int builds = 0;
        run.Scenes.Go(scene =>
        {
            builds++;
            scene.Spawn(new Piece(1));
        });
        run.Ticks(1);

        run.Scenes.Rebuild();
        run.Ticks(1);

        Assert.Equal(2, builds);
        Assert.Equal(1, Count(run.World, 1));
    }

    [Fact]
    public void Rebuild_before_any_scene_throws()
    {
        using var run = HeadlessGame.Create<Empty>(1);

        var error = Assert.Throws<InvalidOperationException>(() => run.Scenes.Rebuild());

        Assert.Equal("There's no scene to rebuild yet. Call Go first.", error.Message);
    }

    /// <summary>Asks for a scene whose build reads a key.</summary>
    private sealed class Peeker(Scenes scenes, Input input) : IGame
    {
        public void Update(GameTime time)
        {
            if (time.Tick == 0)
            {
                scenes.Go(_ => input.Down(Key.Space));
            }
        }
    }

    [Fact]
    public void A_build_cannot_read_input()
    {
        using var run = HeadlessGame.Create<Peeker>(1);

        Assert.Throws<InvalidOperationException>(() => run.Ticks(1));
    }

    [Fact]
    public void Current_is_the_scene_last_built()
    {
        using var run = HeadlessGame.Create<Empty>(1);
        Scene? built = null;
        Assert.Null(run.Scenes.Current);

        run.Scenes.Go(scene => built = scene);
        run.Ticks(1);

        Assert.NotNull(built);
        Assert.Same(built, run.Scenes.Current);
    }
}
