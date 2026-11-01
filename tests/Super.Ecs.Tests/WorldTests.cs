using Microsoft.Extensions.DependencyInjection;
using Super.Testing;

namespace Super.Ecs.Tests;

public class WorldTests
{
    private record struct Position(float X);

    private record struct Velocity(float X);

    private record struct Health(int Value);

    private record struct Tag;

    private record struct Label(string Name);

    private readonly World _world = new();

    private Entity Create(float position, float? velocity = null)
    {
        var entity = _world.Create();
        _world.Add(entity, new Position(position));
        if (velocity is { } value)
        {
            _world.Add(entity, new Velocity(value));
        }

        return entity;
    }

    [Fact]
    public void A_new_entity_is_alive_and_has_nothing()
    {
        var entity = _world.Create();

        Assert.True(_world.IsAlive(entity));
        Assert.False(_world.Has<Position>(entity));
        Assert.Equal(1, _world.Count);
    }

    [Fact]
    public void Components_can_be_added_read_changed_and_removed()
    {
        var entity = Create(1f);
        Assert.Equal(1f, _world.Get<Position>(entity).X);

        _world.Get<Position>(entity).X = 2f;
        Assert.Equal(2f, _world.Get<Position>(entity).X);

        _world.Add(entity, new Position(3f));
        Assert.Equal(3f, _world.Get<Position>(entity).X);

        _world.Remove<Position>(entity);
        Assert.False(_world.Has<Position>(entity));
        Assert.Throws<InvalidOperationException>(() => _world.Get<Position>(entity));

        _world.Remove<Position>(entity);
    }

    [Fact]
    public void Adding_a_component_keeps_the_others()
    {
        var entity = Create(1f, 2f);
        _world.Add(entity, new Health(3));
        _world.Add(entity, new Label("fish"));

        Assert.Equal(1f, _world.Get<Position>(entity).X);
        Assert.Equal(2f, _world.Get<Velocity>(entity).X);
        Assert.Equal(3, _world.Get<Health>(entity).Value);
        Assert.Equal("fish", _world.Get<Label>(entity).Name);
    }

    [Fact]
    public void Removing_one_entity_leaves_its_neighbours_intact()
    {
        var first = Create(1f);
        var second = Create(2f);
        var third = Create(3f);

        _world.Destroy(first);

        Assert.Equal(2f, _world.Get<Position>(second).X);
        Assert.Equal(3f, _world.Get<Position>(third).X);
        Assert.Equal(2, _world.Count);
    }

    [Fact]
    public void A_destroyed_entity_stays_dead_even_when_its_id_is_reused()
    {
        var old = Create(1f);
        _world.Destroy(old);
        var reused = Create(2f);

        Assert.Equal(old.Id, reused.Id);
        Assert.False(_world.IsAlive(old));
        Assert.True(_world.IsAlive(reused));
        Assert.Throws<InvalidOperationException>(() => _world.Get<Position>(old));
        Assert.Throws<InvalidOperationException>(() => _world.Destroy(old));
    }

    [Fact]
    public void Query_finds_only_entities_with_every_component()
    {
        Create(1f);
        var moving = Create(2f, 5f);
        var healthy = Create(3f, 6f);
        _world.Add(healthy, new Health(1));

        var found = new List<Entity>();
        foreach (var row in _world.Query<Position, Velocity>())
        {
            found.Add(row.Entity);
        }

        Assert.Equal(2, found.Count);
        Assert.Contains(moving, found);
        Assert.Contains(healthy, found);
    }

    [Fact]
    public void Query_rows_change_the_real_components()
    {
        var entity = Create(1f, 2f);

        foreach (var row in _world.Query<Position, Velocity>())
        {
            row.First.X += row.Second.X;
        }

        Assert.Equal(3f, _world.Get<Position>(entity).X);
    }

    [Fact]
    public void Queries_handle_every_size_and_an_empty_world()
    {
        foreach (var row in _world.Query<Position>())
        {
            Assert.Fail($"Nothing should match {row.Entity}.");
        }

        var entity = Create(1f, 2f);
        _world.Add(entity, new Health(3));
        _world.Add(entity, new Label("fish"));

        int rows = 0;
        foreach (var row in _world.Query<Position>())
        {
            Assert.Equal(1f, row.First.X);
            rows++;
        }

        foreach (var row in _world.Query<Position, Velocity, Health>())
        {
            Assert.Equal(3, row.Third.Value);
            rows++;
        }

        foreach (var row in _world.Query<Position, Velocity, Health, Label>())
        {
            Assert.Equal("fish", row.Fourth.Name);
            Assert.Equal(entity, row.Entity);
            rows++;
        }

        Assert.Equal(3, rows);
    }

    [Fact]
    public void Query_visits_each_entity_once_across_many_archetypes()
    {
        for (int i = 0; i < 1000; i++)
        {
            var entity = Create(i, i % 2 == 0 ? 1f : null);
            if (i % 3 == 0)
            {
                _world.Add(entity, new Tag());
            }
        }

        var seen = new HashSet<float>();
        foreach (var row in _world.Query<Position>())
        {
            Assert.True(seen.Add(row.First.X));
        }

        Assert.Equal(1000, seen.Count);
    }

    [Fact]
    public void Changing_the_world_during_a_query_is_refused()
    {
        var entity = Create(1f);

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var row in _world.Query<Position>())
            {
                _world.Destroy(row.Entity);
            }
        });

        // Leaving a query, even by an exception, makes changes possible again.
        _world.Destroy(entity);
        Assert.Equal(0, _world.Count);
    }

    [Fact]
    public void Replacing_a_component_during_a_query_is_allowed()
    {
        var entity = Create(1f);

        foreach (var row in _world.Query<Position>())
        {
            _world.Add(row.Entity, new Position(9f));
        }

        Assert.Equal(9f, _world.Get<Position>(entity).X);
    }

    [Fact]
    public void The_same_steps_give_the_same_order()
    {
        static List<float> Order()
        {
            var world = new World();
            var entities = new List<Entity>();
            for (int i = 0; i < 100; i++)
            {
                var entity = world.Create();
                world.Add(entity, new Position(i));
                entities.Add(entity);
            }

            for (int i = 0; i < 100; i += 7)
            {
                world.Destroy(entities[i]);
            }

            var order = new List<float>();
            foreach (var row in world.Query<Position>())
            {
                order.Add(row.First.X);
            }

            return order;
        }

        Assert.Equal(Order(), Order());
    }

    [Fact]
    public void Rows_come_in_a_fixed_order_across_archetypes()
    {
        var world = new World();
        var entities = new List<Entity>();
        for (int i = 0; i < 6; i++)
        {
            var entity = world.Create();
            world.Add(entity, new Position(i));
            entities.Add(entity);
        }

        // Position only starts as 0 to 5. Moving 1 and 3 out swaps 5 and then 4 into their rows,
        // and dropping 0 swaps 4 into the first row, so that archetype ends up as 4, 5, 2.
        world.Add(entities[1], new Velocity(0f));
        world.Add(entities[3], new Velocity(0f));
        world.Destroy(entities[0]);

        var order = new List<float>();
        foreach (var row in world.Query<Position>())
        {
            order.Add(row.First.X);
        }

        Assert.Equal(new float[] { 4, 5, 2, 1, 3 }, order);
    }

    [Fact]
    public void Worlds_are_separate()
    {
        var other = new World();
        Create(1f);

        foreach (var row in other.Query<Position>())
        {
            Assert.Fail($"Nothing should match {row.Entity}.");
        }
    }

    [Fact]
    public void Queries_allocate_nothing()
    {
        for (int i = 0; i < 1000; i++)
        {
            Create(i, 1f);
        }

        static float Sum(World world)
        {
            float sum = 0f;
            foreach (var row in world.Query<Position, Velocity>())
            {
                row.First.X += row.Second.X;
                sum += row.First.X;
            }

            return sum;
        }

        Sum(_world);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            Sum(_world);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private class Movement(World world) : IGame
    {
        public World World { get; } = world;

        public void Update(GameTime time)
        {
            foreach (var row in World.Query<Position, Velocity>())
            {
                row.First.X += row.Second.X * time.TickSeconds;
            }
        }

        public void Draw(View view)
        {
        }
    }

    [Fact]
    public void AddSuperEcs_gives_a_game_its_own_world()
    {
        using var run = HeadlessGame.Create<Movement>(1, services => services.AddSuperEcs());
        var entity = run.Game.World.Create();
        run.Game.World.Add(entity, new Position(0f));
        run.Game.World.Add(entity, new Velocity(30f));

        run.Ticks(3);

        Assert.Equal(3f, run.Game.World.Get<Position>(entity).X, 0.001f);
    }

    [Fact]
    public void Two_games_do_not_share_a_world()
    {
        using var first = HeadlessGame.Create<Movement>(1, services => services.AddSuperEcs());
        using var second = HeadlessGame.Create<Movement>(1, services => services.AddSuperEcs());
        var entity = first.Game.World.Create();
        first.Game.World.Add(entity, new Position(0f));
        first.Game.World.Add(entity, new Velocity(30f));

        second.Ticks(3);

        Assert.NotSame(first.Game.World, second.Game.World);
        Assert.Equal(0, second.Game.World.Count);
        Assert.False(second.Game.World.IsAlive(entity));
        foreach (var row in second.Game.World.Query<Position>())
        {
            Assert.Fail($"The other game's {row.Entity} showed up.");
        }

        Assert.Equal(1, first.Game.World.Count);
    }
}
