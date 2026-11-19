using System.Numerics;
using Super.Testing;

namespace Super.Tests;

public class WorldServiceTests
{
    private sealed class Mover(World world) : IGame
    {
        public Entity Entity { get; private set; }

        public void Load()
        {
            Entity = world.Create();
            world.Add(Entity, Transform.At(new Vector3(1f, 0f, 0f)));
            world.Add(Entity, new Smoothed());
        }

        public void Update(GameTime time) => world.Get<Transform>(Entity).Position += Vector3.UnitX;

        public void Draw(View view)
        {
        }
    }

    [Fact]
    public void A_game_gets_the_world_without_registering_it()
    {
        using var run = HeadlessGame.Create<Mover>(seed: 1);

        run.Ticks(1);

        Assert.Equal(2f, run.World.Get<Transform>(run.Game.Entity).Position.X);
    }

    [Fact]
    public void The_world_remembers_last_tick_without_being_registered()
    {
        using var run = HeadlessGame.Create<Mover>(seed: 1);

        run.Ticks(2);

        Assert.Equal(2f, run.World.Get<Smoothed>(run.Game.Entity).Previous.Position.X);
    }
}
