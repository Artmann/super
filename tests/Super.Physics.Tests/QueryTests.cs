using System.Numerics;
using Super.Ecs;

namespace Super.Physics.Tests;

public class QueryTests
{
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
    public void A_ball_cast_stops_where_the_ball_first_touches()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var wall = Place(world, new Vector3(0f, 1f, -0.5f), Collider.Box(new Vector3(10f, 2f, 1f)));
        physics.Sync();

        var hit = physics.ShapeCast(Collider.Ball(0.5f), new Vector3(0f, 1f, 5f), -Vector3.UnitZ);

        // The wall's face is at z = 0, and the ball's front reaches it when its centre is at 0.5.
        Assert.NotNull(hit);
        Assert.Equal(wall, hit.Value.Entity);
        Assert.Equal(4.5f, hit.Value.Distance, 0.01f);
        Assert.Equal(0f, hit.Value.Point.Z, 0.01f);
        Assert.Equal(1f, hit.Value.Normal.Z, 0.01f);
    }

    [Fact]
    public void A_ball_does_not_fit_through_a_gap_a_ray_passes()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Place(world, new Vector3(-0.6f, 1f, 0f), Collider.Box(new Vector3(1f, 2f, 1f)));
        Place(world, new Vector3(0.6f, 1f, 0f), Collider.Box(new Vector3(1f, 2f, 1f)));
        physics.Sync();

        var from = new Vector3(0f, 1f, 5f);

        Assert.Null(physics.Raycast(new Ray(from, -Vector3.UnitZ)));
        Assert.NotNull(physics.ShapeCast(Collider.Ball(0.3f), from, -Vector3.UnitZ));
    }

    [Fact]
    public void A_cast_down_finds_the_floor_and_its_normal()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var floor = Place(world, new Vector3(0f, -0.5f, 0f), Collider.Box(new Vector3(20f, 1f, 20f)));
        physics.Sync();

        var hit = physics.ShapeCast(Collider.Capsule(1.8f, 0.4f), new Vector3(3f, 10f, 2f), -Vector3.UnitY, reach: 50f);

        // A capsule stands on its offset, so its foot meets the floor after 10 m.
        Assert.Equal(floor, hit!.Value.Entity);
        Assert.Equal(10f, hit.Value.Distance, 0.01f);
        Assert.Equal(Vector3.UnitY, hit.Value.Normal);
    }

    [Fact]
    public void Every_cast_hit_comes_nearest_first_once_each()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var c = Place(world, new Vector3(0f, 1f, -8f), Collider.Ball(0.5f));
        var a = Place(world, new Vector3(0f, 1f, 0f), Collider.Box(Vector3.One));
        var b = Place(world, new Vector3(0.6f, 1f, -4f), Collider.Ball(0.5f));
        physics.Sync();

        var hits = new List<ShapeHit>();
        physics.ShapeCastAll(Collider.Ball(0.5f), new Vector3(0f, 1f, 5f), -Vector3.UnitZ, hits);

        Assert.Equal([a, b, c], hits.Select(hit => hit.Entity));
    }

    [Fact]
    public void A_cast_misses_beyond_its_reach()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Place(world, Vector3.Zero, Collider.Ball(0.5f));
        physics.Sync();

        Assert.Null(physics.ShapeCast(Collider.Ball(0.5f), new Vector3(0f, 0f, 5f), -Vector3.UnitZ, reach: 3.9f));
        Assert.NotNull(physics.ShapeCast(Collider.Ball(0.5f), new Vector3(0f, 0f, 5f), -Vector3.UnitZ, reach: 4.1f));
    }

    [Fact]
    public void A_turned_box_cast_reaches_further()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Place(world, new Vector3(0f, 0f, -0.5f), Collider.Box(new Vector3(10f, 10f, 1f)));
        physics.Sync();

        var flat = physics.ShapeCast(Collider.Box(new Vector3(2f, 0.2f, 0.2f)), new Vector3(0f, 0f, 5f), -Vector3.UnitZ);
        var turned = physics.ShapeCast(
            Collider.Box(new Vector3(2f, 0.2f, 0.2f)),
            new Vector3(0f, 0f, 5f),
            -Vector3.UnitZ,
            rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f));

        // Lengthways along the cast, the box's front is 1 m ahead of its centre rather than 0.1 m.
        Assert.Equal(4.9f, flat!.Value.Distance, 0.01f);
        Assert.Equal(4f, turned!.Value.Distance, 0.01f);
    }

    [Fact]
    public void An_overlap_finds_what_touches_in_entity_order()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var near = Place(world, new Vector3(1f, 0f, 0f), Collider.Ball(0.5f));
        Place(world, new Vector3(5f, 0f, 0f), Collider.Ball(0.5f));
        var under = Place(world, new Vector3(0f, -1f, 0f), Collider.Box(new Vector3(4f, 1f, 4f)));
        Place(world, new Vector3(0f, 1f, 0f), Collider.Ball(0.5f, layers: 2));
        physics.Sync();

        var hits = new List<Entity>();
        physics.Overlap(Collider.Ball(1.2f), Vector3.Zero, hits, layers: 1);

        Assert.Equal([near, under], hits);
    }

    [Fact]
    public void An_overlap_places_the_shape_by_its_offset()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var high = Place(world, new Vector3(0f, 3f, 0f), Collider.Ball(0.5f));
        physics.Sync();

        var hits = new List<Entity>();
        physics.Overlap(Collider.Ball(0.6f), Vector3.Zero, hits);
        Assert.Empty(hits);

        physics.Overlap(Collider.Ball(0.6f, offset: new Vector3(0f, 3f, 0f)), Vector3.Zero, hits);
        Assert.Equal([high], hits);
    }

    [Fact]
    public void Queries_pass_through_triggers()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var area = Place(world, Vector3.Zero, Collider.Box(new Vector3(4f)));
        world.Add(area, new Trigger());
        var behind = Place(world, new Vector3(0f, 0f, -5f), Collider.Ball(0.5f));
        physics.Sync();

        var hits = new List<Entity>();
        physics.Overlap(Collider.Ball(0.5f), Vector3.Zero, hits);

        Assert.Equal(behind, physics.Raycast(new Ray(new Vector3(0f, 0f, 5f), -Vector3.UnitZ))!.Value.Entity);
        Assert.Equal(behind, physics.ShapeCast(Collider.Ball(0.2f), new Vector3(0f, 0f, 5f), -Vector3.UnitZ)!.Value.Entity);
        Assert.Empty(hits);
    }

    [Fact]
    public void A_bad_query_shape_says_so()
    {
        var (_, physics) = Make();
        using var __ = physics;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => physics.ShapeCast(Collider.Ball(0f), Vector3.Zero, Vector3.UnitX));

        Assert.StartsWith("The ShapeCast shape is a ball of radius 0; it must be above 0.", error.Message);
    }

    [Fact]
    public void A_cast_with_no_direction_says_so()
    {
        var (_, physics) = Make();
        using var __ = physics;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => physics.ShapeCast(Collider.Ball(1f), Vector3.Zero, Vector3.Zero));

        Assert.StartsWith("A shape cast needs a direction that isn't zero.", error.Message);
    }
}
