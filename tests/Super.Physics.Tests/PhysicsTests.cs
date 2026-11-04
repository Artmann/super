using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Super.Ecs;
using Super.Testing;

namespace Super.Physics.Tests;

public class PhysicsTests
{
    // Looking down the -Z axis from in front of everything.
    private static readonly Ray Forward = new(new Vector3(0f, 1f, 10f), -Vector3.UnitZ);

    private static (World World, Physics Physics) Make()
    {
        var world = new World();
        return (world, new Physics(world));
    }

    private static Entity Place(World world, Vector3 position, Collider collider)
    {
        var entity = world.Create();
        world.Add(entity, Transform.At(position));
        world.Add(entity, collider);
        return entity;
    }

    [Fact]
    public void A_ray_hits_the_nearest_collider()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var far = Place(world, new Vector3(0f, 1f, -5f), Collider.Ball(0.5f));
        var near = Place(world, new Vector3(0f, 1f, 0f), Collider.Ball(0.5f));
        physics.Sync();

        var hit = physics.Raycast(Forward);

        Assert.NotNull(hit);
        Assert.Equal(near, hit.Value.Entity);
        Assert.Equal(9.5f, hit.Value.Distance, 3);
        Assert.Equal(0.5f, hit.Value.Point.Z, 3);
        Assert.NotEqual(far, hit.Value.Entity);
    }

    [Fact]
    public void A_ray_that_meets_nothing_misses()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Place(world, new Vector3(3f, 1f, 0f), Collider.Ball(0.5f));
        physics.Sync();

        Assert.Null(physics.Raycast(Forward));
        Assert.Null(physics.Raycast(Forward, reach: 5f));
    }

    [Fact]
    public void A_query_sees_only_its_layers()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Place(world, new Vector3(0f, 1f, 0f), Collider.Ball(0.5f, layers: 1));
        var behind = Place(world, new Vector3(0f, 1f, -5f), Collider.Ball(0.5f, layers: 2));
        physics.Sync();

        Assert.Equal(behind, physics.Raycast(Forward, layers: 2)!.Value.Entity);
    }

    [Fact]
    public void Every_hit_comes_nearest_first_once_each()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var c = Place(world, new Vector3(0f, 1f, -6f), Collider.Box(Vector3.One));
        var a = Place(world, new Vector3(0f, 1f, 0f), Collider.Capsule(2f, 0.4f, new Vector3(0f, -1f, 0f)));
        var b = Place(world, new Vector3(0f, 1f, -3f), Collider.Ball(0.5f));
        physics.Sync();

        var hits = new List<RayHit>();
        physics.RaycastAll(Forward, hits);

        Assert.Equal([a, b, c], hits.Select(hit => hit.Entity));
    }

    [Fact]
    public void A_collider_follows_its_entity_and_parent()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var parent = world.Create();
        world.Add(parent, Transform.At(new Vector3(5f, 0f, 0f)));
        var child = Place(world, new Vector3(0f, 1f, 0f), Collider.Ball(0.5f));
        world.Add(child, new Parent(parent));
        physics.Sync();
        Assert.Null(physics.Raycast(Forward));

        world.Get<Transform>(parent).Position = Vector3.Zero;
        physics.Sync();

        Assert.Equal(child, physics.Raycast(Forward)!.Value.Entity);
    }

    [Fact]
    public void A_collider_grows_with_its_scale()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var ball = Place(world, new Vector3(1.5f, 1f, 0f), Collider.Ball(0.5f));
        physics.Sync();
        Assert.Null(physics.Raycast(Forward));

        world.Get<Transform>(ball).Scale = new Vector3(4f);
        physics.Sync();

        Assert.Equal(ball, physics.Raycast(Forward)!.Value.Entity);
    }

    [Fact]
    public void A_destroyed_entity_is_skipped_at_once_and_dropped_at_the_next_sync()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var near = Place(world, new Vector3(0f, 1f, 0f), Collider.Ball(0.5f));
        var far = Place(world, new Vector3(0f, 1f, -5f), Collider.Ball(0.5f));
        physics.Sync();

        world.Destroy(near);
        Assert.Equal(far, physics.Raycast(Forward)!.Value.Entity);

        physics.Sync();
        Assert.Equal(1, physics.Count);
    }

    [Fact]
    public void Taking_the_collider_away_drops_it()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var ball = Place(world, new Vector3(0f, 1f, 0f), Collider.Ball(0.5f));
        physics.Sync();

        world.Remove<Collider>(ball);
        Assert.Null(physics.Raycast(Forward));
        physics.Sync();

        Assert.Equal(0, physics.Count);
    }

    [Fact]
    public void A_bad_collider_names_its_entity()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var entity = Place(world, Vector3.Zero, Collider.Capsule(0.5f, 0.4f));

        var error = Assert.Throws<ArgumentOutOfRangeException>(physics.Sync);

        Assert.Contains($"Entity {entity.Id}", error.Message);
    }

    private sealed class Clicking(World world, Physics physics) : IGame
    {
        public Entity Target { get; private set; }

        public Entity? Hit { get; private set; }

        public void Load() => Target = Place(world, new Vector3(0f, 1f, 0f), Collider.Ball(0.5f));

        public void Update(GameTime time) => Hit = physics.Raycast(Forward)?.Entity;

        public void Draw(View view)
        {
        }
    }

    [Fact]
    public void The_game_loop_syncs_before_update()
    {
        using var run = HeadlessGame.Create<Clicking>(1, services => services.AddSuperPhysics());

        run.Ticks(1);

        Assert.Equal(run.Game.Target, run.Game.Hit);
    }
}
