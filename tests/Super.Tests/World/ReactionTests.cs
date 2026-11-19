namespace Super.Tests;

public class ReactionTests
{
    private record struct Position(float X);

    private record struct Health(int Value);

    private record struct Armor(int Value);

    private record struct Speed(float Value);

    private readonly World _world = new();

    [Fact]
    public void OnAdded_runs_once_for_a_spawn_and_can_read_the_component()
    {
        var seen = new List<(Entity Entity, int Health)>();
        _world.OnAdded<Health>(entity => seen.Add((entity, _world.Get<Health>(entity).Value)));

        var spawned = _world.Spawn(new Health(3));

        Assert.Equal([(spawned, 3)], seen);
    }

    [Fact]
    public void OnAdded_sees_every_component_the_spawn_gave()
    {
        float? position = null;
        _world.OnAdded<Health>(entity => position = _world.Get<Position>(entity).X);

        _world.Spawn(new Health(3), new Position(1f));

        Assert.Equal(1f, position);
    }

    [Fact]
    public void OnAdded_runs_for_a_required_component()
    {
        int added = 0;
        _world.OnAdded<Transform>(_ => added++);

        _world.Spawn(new Model(default));

        Assert.Equal(1, added);
    }

    [Fact]
    public void OnAdded_sees_every_required_component_even_ones_required_later_in_the_list()
    {
        _world.Require<Health, Armor>(new Armor(1));
        _world.Require<Health, Speed>(new Speed(2f));
        bool? hadSpeed = null;
        _world.OnAdded<Armor>(entity => hadSpeed = _world.Has<Speed>(entity));

        var entity = _world.Create();
        _world.Add(entity, new Health(3));

        Assert.True(hadSpeed);
    }

    [Fact]
    public void Replacing_a_component_does_not_run_OnAdded_again()
    {
        int added = 0;
        _world.OnAdded<Health>(_ => added++);
        var entity = _world.Spawn(new Health(3));

        _world.Add(entity, new Health(4));

        Assert.Equal(1, added);
    }

    [Fact]
    public void OnRemoved_runs_on_Remove_and_can_still_read_the_value()
    {
        var seen = new List<int>();
        _world.OnRemoved<Health>(entity => seen.Add(_world.Get<Health>(entity).Value));
        var healthy = _world.Spawn(new Health(3));
        var plain = _world.Spawn(new Position(1f));

        _world.Remove<Health>(healthy);
        _world.Remove<Health>(plain);

        Assert.Equal([3], seen);
        Assert.False(_world.Has<Health>(healthy));
    }

    [Fact]
    public void OnRemoved_runs_for_each_component_on_Destroy_while_the_entity_is_alive()
    {
        var seen = new List<string>();
        _world.OnRemoved<Health>(entity => seen.Add($"health {_world.IsAlive(entity)}"));
        _world.OnRemoved<Position>(entity => seen.Add($"position {_world.IsAlive(entity)}"));
        var entity = _world.Spawn(new Health(3), new Position(1f));

        _world.Destroy(entity);

        Assert.Equal(["health True", "position True"], seen.Order());
        Assert.False(_world.IsAlive(entity));
    }

    [Fact]
    public void A_spawn_inside_a_query_runs_its_handler_when_the_query_ends()
    {
        _world.Spawn(new Position(0f));
        int added = 0;
        _world.OnAdded<Health>(_ => added++);
        int duringLoop = -1;

        foreach (var row in _world.Query<Position>())
        {
            _world.Spawn(new Health(3));
            duringLoop = added;
        }

        Assert.Equal(0, duringLoop);
        Assert.Equal(1, added);
    }

    [Fact]
    public void A_spawn_inside_a_query_has_all_its_components_when_its_handler_runs()
    {
        _world.Spawn(new Position(0f));
        float? position = null;
        _world.OnAdded<Health>(entity => position = _world.Get<Position>(entity).X);

        foreach (var row in _world.Query<Position>())
        {
            _world.Spawn(new Health(3), new Position(7f));
        }

        Assert.Equal(7f, position);
    }

    [Fact]
    public void An_entity_spawned_and_destroyed_in_one_query_runs_no_handlers()
    {
        _world.Spawn(new Position(0f));
        int added = 0;
        int removed = 0;
        _world.OnAdded<Health>(_ => added++);
        _world.OnRemoved<Health>(_ => removed++);

        foreach (var row in _world.Query<Position>())
        {
            var brief = _world.Spawn(new Health(3));
            _world.Destroy(brief);
        }

        Assert.Equal(0, added);
        Assert.Equal(0, removed);
        Assert.Equal(1, _world.Count);
    }

    [Fact]
    public void A_handler_can_destroy_other_entities()
    {
        var torch = _world.Spawn(new Position(1f));
        var holder = _world.Spawn(new Health(3));
        int removed = 0;
        _world.OnRemoved<Health>(_ => _world.Destroy(torch));
        _world.OnRemoved<Position>(_ => removed++);

        foreach (var row in _world.Query<Health>())
        {
            _world.Destroy(holder);
        }

        Assert.False(_world.IsAlive(torch));
        Assert.False(_world.IsAlive(holder));
        Assert.Equal(1, removed);
        Assert.Equal(0, _world.Count);
    }

    [Fact]
    public void A_handler_that_adds_to_its_dying_entity_does_not_keep_it_alive()
    {
        var entity = _world.Spawn(new Health(3));
        int armored = 0;
        _world.OnRemoved<Health>(dying => _world.Add(dying, new Armor(1)));
        _world.OnAdded<Armor>(_ => armored++);

        _world.Destroy(entity);

        Assert.False(_world.IsAlive(entity));
        Assert.Equal(0, armored);
        Assert.Equal(0, _world.Count);
    }

    [Fact]
    public void A_handler_can_spawn_the_type_it_reacts_to()
    {
        int added = 0;
        _world.OnAdded<Health>(_ =>
        {
            added++;
            if (added == 1)
            {
                _world.Spawn(new Health(1));
            }
        });

        _world.Spawn(new Health(3));

        Assert.Equal(2, added);
        Assert.Equal(2, _world.Count);
    }

    [Fact]
    public void Handlers_for_one_type_run_in_the_order_they_were_registered()
    {
        var order = new List<string>();
        _world.OnAdded<Health>(_ => order.Add("first"));
        _world.OnAdded<Health>(_ => order.Add("second"));

        _world.Spawn(new Health(3));

        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public void A_handler_that_throws_leaves_the_world_usable()
    {
        _world.OnAdded<Health>(_ => throw new InvalidOperationException("Boom."));

        var error = Assert.Throws<InvalidOperationException>(() => _world.Spawn(new Health(3)));

        Assert.Equal("Boom.", error.Message);
        var later = _world.Spawn(new Position(1f));
        _world.Destroy(later);
        Assert.Equal(1, _world.Count);
    }

    [Fact]
    public void Adding_then_removing_in_one_query_runs_both_handlers()
    {
        int added = 0;
        int removed = 0;
        _world.OnAdded<Armor>(_ => added++);
        _world.OnRemoved<Armor>(_ => removed++);
        var entity = _world.Spawn(new Health(3));

        foreach (var row in _world.Query<Health>())
        {
            _world.Add(row.Entity, new Armor(1));
            _world.Remove<Armor>(row.Entity);
        }

        Assert.Equal(1, added);
        Assert.Equal(1, removed);
        Assert.False(_world.Has<Armor>(entity));
    }
}
