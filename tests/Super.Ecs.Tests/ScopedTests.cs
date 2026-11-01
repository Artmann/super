using System.Numerics;
using Super.Testing;

namespace Super.Ecs.Tests;

public class ScopedTests
{
    public enum Screen { Menu, House, Outside }

    public enum Pause { Running, Paused }

    private class Rooms(World world, States<Screen> states) : IGame
    {
        public World World => world;

        public Entity Player { get; private set; }

        public Entity Door { get; private set; }

        public int SeenOnExit { get; private set; }

        public Vector3 DoorPosition { get; set; } = new(5f, 0f, 0f);

        public void Load()
        {
            Player = world.Create();
            world.Add(Player, Transform.At(new Vector3(1f, 0f, 0f)));
            world.Add(Player, new Smoothed());

            states.OnEnter(Screen.House, () =>
            {
                Door = world.Create();
                world.Add(Door, new ScopedTo<Screen>(Screen.House));
                world.Add(Player, Transform.At(DoorPosition));
            });
            states.OnExit(Screen.House, () => SeenOnExit = world.IsAlive(Door) ? 1 : 0);
        }

        public void Update(GameTime time)
        {
        }

        public void Draw(View view) => view.Models(world);
    }

    private static HeadlessRun<Rooms> Run() => HeadlessGame.Create<Rooms>(1, services => services.AddSuperEcs());

    [Fact]
    public void Entities_scoped_to_a_state_go_when_it_ends()
    {
        using var run = Run();
        run.States<Screen>().Set(Screen.House);
        run.Ticks(1);
        Assert.True(run.Game.World.IsAlive(run.Game.Door));

        run.States<Screen>().Set(Screen.Outside);
        run.Ticks(1);
        Assert.False(run.Game.World.IsAlive(run.Game.Door));
    }

    [Fact]
    public void Exit_callbacks_still_see_the_scoped_entities()
    {
        using var run = Run();
        run.States<Screen>().Set(Screen.House);
        run.Ticks(1);
        run.States<Screen>().Set(Screen.Outside);
        run.Ticks(1);
        Assert.Equal(1, run.Game.SeenOnExit);
    }

    [Fact]
    public void Untagged_entities_carry_through()
    {
        using var run = Run();
        run.States<Screen>().Set(Screen.House);
        run.Ticks(1);
        run.States<Screen>().Set(Screen.Outside);
        run.Ticks(1);
        Assert.True(run.Game.World.IsAlive(run.Game.Player));
    }

    [Fact]
    public void Leaving_a_different_state_keeps_the_entity()
    {
        using var run = Run();
        var house = run.Game.World.Create();
        run.Game.World.Add(house, new ScopedTo<Screen>(Screen.House));
        run.States<Screen>().Set(Screen.Outside);
        run.Ticks(1);
        Assert.True(run.Game.World.IsAlive(house));
    }

    [Fact]
    public void Another_enum_with_the_same_number_leaves_it_alone()
    {
        using var run = Run();
        var paused = run.Game.World.Create();
        run.Game.World.Add(paused, new ScopedTo<Pause>(Pause.Running));
        run.States<Screen>().Set(Screen.House);
        run.Ticks(1);
        Assert.True(run.Game.World.IsAlive(paused));
    }

    [Fact]
    public void Many_scoped_entities_all_go()
    {
        using var run = Run();
        var world = run.Game.World;
        var made = new List<Entity>();
        for (int i = 0; i < 1000; i++)
        {
            var entity = world.Create();
            world.Add(entity, new ScopedTo<Screen>(Screen.Menu));
            if (i % 2 == 0)
            {
                world.Add(entity, Transform.At(Vector3.Zero));
            }

            made.Add(entity);
        }

        run.States<Screen>().Set(Screen.Outside);
        run.Ticks(1);
        Assert.All(made, entity => Assert.False(world.IsAlive(entity)));
    }

    [Fact]
    public void A_player_moved_on_entering_appears_there_without_gliding()
    {
        using var run = Run();
        run.Ticks(2);
        run.States<Screen>().Set(Screen.House);
        run.Ticks(1);

        var player = run.Game.World.Get<Smoothed>(run.Game.Player);
        Assert.Equal(run.Game.DoorPosition, player.Previous.Position);
    }
}
