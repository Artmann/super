namespace Super.Tests;

public class QueuedChangesTests
{
    private record struct Position(float X);

    private record struct Health(int Value);

    private readonly World _world = new();

    private Entity Create(float x) => _world.Spawn(new Position(x));

    [Fact]
    public void Destroying_inside_a_query_takes_effect_when_it_ends()
    {
        var first = Create(1f);
        var second = Create(2f);
        int visited = 0;

        foreach (var row in _world.Query<Position>())
        {
            visited++;
            _world.Destroy(first);
            Assert.True(_world.IsAlive(first));
        }

        Assert.Equal(2, visited);
        Assert.False(_world.IsAlive(first));
        Assert.True(_world.IsAlive(second));
    }

    [Fact]
    public void An_entity_spawned_inside_a_query_is_alive_at_once_and_complete_afterwards()
    {
        Create(1f);
        Entity spawned = default;

        foreach (var row in _world.Query<Position>())
        {
            spawned = _world.Spawn(new Position(5f), new Health(3));
            Assert.True(_world.IsAlive(spawned));
            Assert.False(_world.Has<Health>(spawned));
        }

        Assert.Equal(5f, _world.Get<Position>(spawned).X);
        Assert.Equal(3, _world.Get<Health>(spawned).Value);
    }

    [Fact]
    public void A_query_does_not_visit_an_entity_spawned_during_it()
    {
        Create(1f);
        int visited = 0;

        foreach (var row in _world.Query<Position>())
        {
            visited++;
            _world.Spawn(new Position(5f));
        }

        Assert.Equal(1, visited);
        Assert.Equal(2, _world.Count);
    }

    [Fact]
    public void A_model_spawned_with_a_transform_inside_a_query_keeps_that_transform()
    {
        Create(1f);
        var placed = Transform.At(new System.Numerics.Vector3(4f, 0f, 0f));
        Entity modelFirst = default;
        Entity transformFirst = default;

        foreach (var row in _world.Query<Position>())
        {
            modelFirst = _world.Spawn(new Model(default), placed);
            transformFirst = _world.Spawn(placed, new Model(default));
        }

        Assert.Equal(placed, _world.Get<Transform>(modelFirst));
        Assert.Equal(placed, _world.Get<Transform>(transformFirst));
    }

    [Fact]
    public void A_model_spawned_alone_inside_a_query_gets_its_transform_afterwards()
    {
        Create(1f);
        Entity spawned = default;

        foreach (var row in _world.Query<Position>())
        {
            spawned = _world.Spawn(new Model(default));
        }

        Assert.Equal(Transform.Identity, _world.Get<Transform>(spawned));
    }

    [Fact]
    public void Removing_inside_a_query_takes_effect_when_it_ends()
    {
        var entity = _world.Spawn(new Position(1f), new Health(3));

        foreach (var row in _world.Query<Position>())
        {
            _world.Remove<Health>(entity);
            Assert.True(_world.Has<Health>(entity));
        }

        Assert.False(_world.Has<Health>(entity));
    }

    [Fact]
    public void Replacing_a_component_inside_a_query_happens_at_once()
    {
        var entity = Create(1f);

        foreach (var row in _world.Query<Position>())
        {
            _world.Add(row.Entity, new Position(9f));
            Assert.Equal(9f, row.First.X);
        }

        Assert.Equal(9f, _world.Get<Position>(entity).X);
    }

    [Fact]
    public void Reading_a_queued_component_says_why_it_is_not_there()
    {
        var entity = Create(1f);

        foreach (var row in _world.Query<Position>())
        {
            _world.Add(entity, new Health(3));
            var error = Assert.Throws<InvalidOperationException>(() => _world.Get<Health>(entity));
            Assert.Equal($"Entity {entity.Id}'s Health was added during a query and arrives when the query ends.", error.Message);
        }
    }

    [Fact]
    public void Reading_a_missing_component_inside_a_query_still_says_it_is_missing()
    {
        var entity = Create(1f);

        foreach (var row in _world.Query<Position>())
        {
            var error = Assert.Throws<InvalidOperationException>(() => _world.Get<Health>(entity));
            Assert.Equal($"Entity {entity.Id} has no Health.", error.Message);
        }
    }

    [Fact]
    public void Changes_wait_for_the_outermost_query()
    {
        var entity = Create(1f);

        foreach (var outer in _world.Query<Position>())
        {
            foreach (var inner in _world.Query<Position>())
            {
                _world.Add(entity, new Health(3));
            }

            Assert.False(_world.Has<Health>(entity));
        }

        Assert.True(_world.Has<Health>(entity));
    }

    [Fact]
    public void Changes_are_made_in_the_order_they_were_asked_for()
    {
        var entity = Create(1f);

        foreach (var row in _world.Query<Position>())
        {
            _world.Add(entity, new Health(1));
            _world.Add(entity, new Health(2));
        }

        Assert.Equal(2, _world.Get<Health>(entity).Value);
    }

    [Fact]
    public void A_change_queued_for_an_entity_destroyed_first_is_dropped()
    {
        var entity = Create(1f);

        foreach (var row in _world.Query<Position>())
        {
            _world.Destroy(entity);
            _world.Add(entity, new Health(3));
            _world.Destroy(entity);
        }

        Assert.False(_world.IsAlive(entity));
        Assert.Equal(0, _world.Count);
    }

    [Fact]
    public void An_entity_spawned_and_destroyed_in_one_query_is_gone_afterwards()
    {
        Create(1f);
        Entity spawned = default;

        foreach (var row in _world.Query<Position>())
        {
            spawned = _world.Spawn(new Position(5f));
            _world.Destroy(spawned);
        }

        Assert.False(_world.IsAlive(spawned));
        Assert.Equal(1, _world.Count);
    }

    [Fact]
    public void A_query_that_throws_still_applies_its_changes_and_ends()
    {
        var entity = Create(1f);

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var row in _world.Query<Position>())
            {
                _world.Add(entity, new Health(3));
                throw new InvalidOperationException("stop");
            }
        });

        Assert.Equal("stop", error.Message);
        Assert.True(_world.Has<Health>(entity));
        Assert.False(_world.IsQuerying);
        _world.Destroy(entity);
        Assert.False(_world.IsAlive(entity));
    }

    [Fact]
    public void A_handle_to_a_reused_id_is_not_touched_by_a_change_queued_for_the_old_one()
    {
        var old = Create(1f);
        Create(2f);

        foreach (var row in _world.Query<Position>())
        {
            _world.Destroy(old);
            _world.Add(old, new Health(3));
        }

        var reused = _world.Spawn(new Position(9f));

        Assert.Equal(old.Id, reused.Id);
        Assert.False(_world.Has<Health>(reused));
    }

    [Fact]
    public void Destroying_a_dead_entity_inside_a_query_throws_at_once()
    {
        var entity = Create(1f);
        _world.Destroy(entity);
        Create(2f);

        foreach (var row in _world.Query<Position>())
        {
            var error = Assert.Throws<InvalidOperationException>(() => _world.Destroy(entity));
            Assert.Equal($"Entity {entity.Id} is not alive.", error.Message);
        }
    }

    [Fact]
    public void Adding_then_removing_a_new_type_inside_a_query_leaves_it_off()
    {
        var entity = Create(1f);

        foreach (var row in _world.Query<Position>())
        {
            _world.Add(row.Entity, new Health(3));
            _world.Remove<Health>(row.Entity);
        }

        Assert.False(_world.Has<Health>(entity));
    }

    [Fact]
    public void Removing_then_adding_a_type_inside_a_query_leaves_the_new_value_on()
    {
        var entity = _world.Spawn(new Position(1f), new Health(3));

        foreach (var row in _world.Query<Position>())
        {
            _world.Remove<Health>(row.Entity);
            _world.Add(row.Entity, new Health(7));
        }

        Assert.Equal(7, _world.Get<Health>(entity).Value);
    }

    [Fact]
    public void A_handler_that_throws_while_changes_are_made_does_not_stop_the_rest()
    {
        var first = _world.Spawn(new Position(1f), new Health(1));
        var second = _world.Spawn(new Position(2f), new Health(2));
        _world.OnRemoved<Health>(entity =>
        {
            if (entity == first)
            {
                throw new InvalidOperationException("Boom.");
            }
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var row in _world.Query<Position>())
            {
                _world.Destroy(row.Entity);
            }
        });

        Assert.Equal("Boom.", error.Message);
        Assert.False(_world.IsAlive(second));
    }
}

[Collection(SpawnAllocationCollection.Name)]
[Trait("Speed", "Slow")]
public class QueuedChangesAllocationTests
{
    private record struct Position(float X);

    private record struct Health(int Value);

    private record struct Armor(int Value);

    private static void QueueChanges(World world)
    {
        foreach (var row in world.Query<Position>())
        {
            world.Add(row.Entity, new Health(1));
            world.Remove<Armor>(row.Entity);
            world.Destroy(row.Entity);
        }
    }

    private static void Fill(World world)
    {
        for (int i = 0; i < 1000; i++)
        {
            world.Spawn(new Position(i), new Armor(i));
        }
    }

    [Fact]
    public void Queued_changes_allocate_nothing_once_the_world_has_grown()
    {
        var world = new World();
        Fill(world);
        QueueChanges(world);
        Fill(world);

        long before = GC.GetAllocatedBytesForCurrentThread();
        QueueChanges(world);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, world.Count);
        Assert.Equal(0, allocated);
    }
}
