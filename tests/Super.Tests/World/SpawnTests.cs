using System.Numerics;

namespace Super.Tests;

public class SpawnTests
{
    private record struct Position(float X);

    private record struct Velocity(float X);

    private record struct Health(int Value);

    private record struct Armor(int Value);

    private record struct Name(string Text);

    private readonly World _world = new();

    [Fact]
    public void Spawn_gives_an_entity_all_its_components()
    {
        var entity = _world.Spawn(new Position(1f), new Velocity(2f), new Health(3));

        Assert.Equal(1f, _world.Get<Position>(entity).X);
        Assert.Equal(2f, _world.Get<Velocity>(entity).X);
        Assert.Equal(3, _world.Get<Health>(entity).Value);
        Assert.False(_world.Has<Armor>(entity));
    }

    [Fact]
    public void Spawn_takes_up_to_four_components()
    {
        var entity = _world.Spawn(new Name("crate"), Transform.At(Vector3.One), new Model(default), new Armor(4));

        Assert.Equal("crate", _world.Get<Name>(entity).Text);
        Assert.Equal(Transform.At(Vector3.One), _world.Get<Transform>(entity));
        Assert.True(_world.Has<Model>(entity));
        Assert.Equal(4, _world.Get<Armor>(entity).Value);
    }

    [Fact]
    public void Spawn_takes_five_components()
    {
        var entity = _world.Spawn(new Name("crate"), new Position(1f), new Velocity(2f), new Health(3), new Armor(4));

        Assert.Equal("crate", _world.Get<Name>(entity).Text);
        Assert.Equal(1f, _world.Get<Position>(entity).X);
        Assert.Equal(2f, _world.Get<Velocity>(entity).X);
        Assert.Equal(3, _world.Get<Health>(entity).Value);
        Assert.Equal(4, _world.Get<Armor>(entity).Value);
    }

    [Fact]
    public void Spawns_that_differ_only_in_the_fifth_component_stay_apart()
    {
        var armored = _world.Spawn(new Name("a"), new Position(1f), new Velocity(2f), new Health(3), new Armor(4));
        var placed = _world.Spawn(new Name("b"), new Position(1f), new Velocity(2f), new Health(3), Transform.At(Vector3.One));

        Assert.True(_world.Has<Armor>(armored));
        Assert.False(_world.Has<Transform>(armored));
        Assert.True(_world.Has<Transform>(placed));
        Assert.False(_world.Has<Armor>(placed));
    }

    [Fact]
    public void Spawn_takes_more_than_five_components()
    {
        var entity = _world.Spawn(new Name("crate"), new Position(1f), new Velocity(2f), new Health(3), new Armor(4), Transform.At(Vector3.One), new Static());

        Assert.Equal("crate", _world.Get<Name>(entity).Text);
        Assert.Equal(1f, _world.Get<Position>(entity).X);
        Assert.Equal(2f, _world.Get<Velocity>(entity).X);
        Assert.Equal(3, _world.Get<Health>(entity).Value);
        Assert.Equal(4, _world.Get<Armor>(entity).Value);
        Assert.Equal(Transform.At(Vector3.One), _world.Get<Transform>(entity));
        Assert.True(_world.Has<Static>(entity));
    }

    [Fact]
    public void Spawn_takes_a_list_of_components()
    {
        var parts = new List<object> { new Position(1f), new Health(3) };

        var entity = _world.Spawn([.. parts]);

        Assert.Equal(1f, _world.Get<Position>(entity).X);
        Assert.Equal(3, _world.Get<Health>(entity).Value);
        Assert.False(_world.Has<Velocity>(entity));
    }

    [Fact]
    public void Listed_spawns_of_different_components_stay_apart()
    {
        object[] armored = [new Name("a"), new Position(1f), new Velocity(2f), new Health(3), new Armor(4), new Static()];
        object[] placed = [new Name("b"), new Position(1f), new Velocity(2f), new Health(3), new Armor(4), Transform.Identity];

        var first = _world.Spawn(armored);
        var second = _world.Spawn(placed);
        var third = _world.Spawn(armored);

        Assert.True(_world.Has<Static>(first));
        Assert.False(_world.Has<Transform>(first));
        Assert.True(_world.Has<Transform>(second));
        Assert.False(_world.Has<Static>(second));
        Assert.True(_world.Has<Static>(third));
        Assert.Equal("a", _world.Get<Name>(third).Text);
    }

    [Fact]
    public void A_listed_spawn_brings_required_components_and_keeps_the_given_ones()
    {
        var placed = Transform.At(new Vector3(4f, 0f, 0f));

        var bare = _world.Spawn(new Name("a"), new Position(1f), new Velocity(2f), new Health(3), new Armor(4), new Model(default));
        var given = _world.Spawn(new Name("b"), new Position(1f), new Velocity(2f), new Health(3), new Model(default), placed);

        Assert.Equal(Transform.Identity, _world.Get<Transform>(bare));
        Assert.Equal(placed, _world.Get<Transform>(given));
    }

    [Fact]
    public void A_listed_spawn_keeps_the_last_of_a_type_given_twice()
    {
        var entity = _world.Spawn(new Health(1), new Position(1f), new Velocity(2f), new Armor(4), new Name("a"), new Health(2));

        Assert.Equal(2, _world.Get<Health>(entity).Value);
    }

    [Fact]
    public void A_listed_spawn_inside_a_query_lands_when_the_query_ends()
    {
        _world.Spawn(new Position(1f));
        var spawned = default(Entity);

        foreach (var row in _world.Query<Position>())
        {
            spawned = _world.Spawn(new Name("late"), new Position(5f), new Velocity(2f), new Health(3), new Armor(4), new Static());
            Assert.True(_world.IsAlive(spawned));
        }

        Assert.Equal("late", _world.Get<Name>(spawned).Text);
        Assert.Equal(5f, _world.Get<Position>(spawned).X);
        Assert.True(_world.Has<Static>(spawned));
    }

    [Fact]
    public void A_listed_spawn_runs_the_added_handlers()
    {
        var seen = new List<int>();
        _world.OnAdded<Health>(entity => seen.Add(_world.Get<Health>(entity).Value));

        _world.Spawn(new Name("a"), new Position(1f), new Velocity(2f), new Health(7), new Armor(4), new Static());

        Assert.Equal([7], seen);
    }

    [Fact]
    public void A_listed_spawn_refuses_a_class()
    {
        var error = Assert.Throws<ArgumentException>(() => _world.Spawn(new Position(1f), "crate"));

        Assert.Equal("A component must be a struct, and String is a class.", error.Message);
    }

    [Fact]
    public void A_listed_spawn_refuses_null()
    {
        var error = Assert.Throws<ArgumentException>(() => _world.Spawn(new Position(1f), null!));

        Assert.Equal("Spawn was given null where a component should be.", error.Message);
    }

    [Fact]
    public void A_scene_owns_what_it_spawns_from_a_list()
    {
        var scenes = new Scenes(_world, new Transitions(new GameTime(60)));
        var kept = _world.Spawn(new Name("kept"), new Position(1f), new Velocity(2f), new Health(3), new Armor(4), new Static());
        var owned = default(Entity);
        scenes.Go(scene => owned = scene.Spawn(new Name("owned"), new Position(1f), new Velocity(2f), new Health(3), new Armor(4), new Static()));
        scenes.Apply();
        Assert.Equal("owned", _world.Get<Name>(owned).Text);

        scenes.Go(_ => { });
        scenes.Apply();

        Assert.False(_world.IsAlive(owned));
        Assert.True(_world.IsAlive(kept));
    }

    [Fact]
    public void A_type_given_twice_keeps_the_last()
    {
        var entity = _world.Spawn(new Health(1), new Health(2));

        Assert.Equal(2, _world.Get<Health>(entity).Value);
    }

    [Fact]
    public void A_model_spawned_alone_gets_an_identity_transform()
    {
        var entity = _world.Spawn(new Model(default));

        Assert.Equal(Transform.Identity, _world.Get<Transform>(entity));
    }

    [Fact]
    public void An_animated_entity_added_alone_gets_an_identity_transform()
    {
        var entity = _world.Create();
        _world.Add(entity, new Animated(null!));

        Assert.Equal(Transform.Identity, _world.Get<Transform>(entity));
    }

    [Fact]
    public void A_required_component_never_replaces_one_the_entity_was_given()
    {
        var placed = Transform.At(new Vector3(4f, 0f, 0f));

        var modelFirst = _world.Spawn(new Model(default), placed);
        var transformFirst = _world.Spawn(placed, new Model(default));

        Assert.Equal(placed, _world.Get<Transform>(modelFirst));
        Assert.Equal(placed, _world.Get<Transform>(transformFirst));
    }

    [Fact]
    public void Adding_to_an_entity_that_has_the_needed_component_keeps_it()
    {
        var placed = Transform.At(new Vector3(0f, 2f, 0f));
        var entity = _world.Spawn(placed);

        _world.Add(entity, new Model(default));

        Assert.Equal(placed, _world.Get<Transform>(entity));
    }

    [Fact]
    public void A_game_can_require_its_own_components_and_they_chain()
    {
        _world.Require<Health, Position>(new Position(7f));
        _world.Require<Position, Velocity>(new Velocity(9f));

        var entity = _world.Create();
        _world.Add(entity, new Health(1));

        Assert.Equal(7f, _world.Get<Position>(entity).X);
        Assert.Equal(9f, _world.Get<Velocity>(entity).X);
    }

    [Fact]
    public void A_spawned_entity_gets_chained_requirements_too()
    {
        _world.Require<Health, Position>(new Position(7f));
        _world.Require<Position, Velocity>(new Velocity(9f));

        var entity = _world.Spawn(new Health(1), new Armor(2));

        Assert.Equal(7f, _world.Get<Position>(entity).X);
        Assert.Equal(9f, _world.Get<Velocity>(entity).X);
    }

    [Fact]
    public void A_component_cannot_require_itself()
    {
        var error = Assert.Throws<InvalidOperationException>(() => _world.Require<Health, Health>(new Health(1)));

        Assert.Equal("Health can't require itself.", error.Message);
    }

    [Fact]
    public void A_requirement_that_would_close_a_loop_is_refused()
    {
        _world.Require<Health, Position>(new Position(1f));
        _world.Require<Position, Velocity>(new Velocity(1f));

        var error = Assert.Throws<InvalidOperationException>(() => _world.Require<Velocity, Health>(new Health(1)));

        Assert.Equal("Velocity can't require Health, because Health already requires Velocity.", error.Message);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SpawnAllocationCollection
{
    public const string Name = "Spawn allocation";
}

[Collection(SpawnAllocationCollection.Name)]
[Trait("Speed", "Slow")]
public class SpawnAllocationTests
{
    private record struct Position(float X);

    private record struct Velocity(float X);

    [Fact]
    public void Spawning_allocates_nothing_once_the_world_has_grown()
    {
        var world = new World();
        var spawned = new Entity[1000];
        for (int i = 0; i < spawned.Length; i++)
        {
            spawned[i] = world.Spawn(new Position(i), new Velocity(i), new Model(default));
        }

        foreach (var entity in spawned)
        {
            world.Destroy(entity);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < spawned.Length; i++)
        {
            spawned[i] = world.Spawn(new Position(i), new Velocity(i), new Model(default));
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1000, world.Count);
        Assert.Equal(0, allocated);
    }
}
