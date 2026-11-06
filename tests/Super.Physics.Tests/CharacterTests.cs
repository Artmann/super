using System.Numerics;
using Super.Ecs;

namespace Super.Physics.Tests;

public class CharacterTests
{
    private const float Tick = 1f / 60f;

    private static (World World, Physics Physics) Make()
    {
        var world = new World();
        return (world, new Physics(world));
    }

    private static Entity Place(World world, Vector3 position, Collider collider, Body? body = null)
    {
        var entity = world.Create();
        world.Add(entity, Transform.At(position));
        world.Add(entity, collider);
        if (body is { } given)
        {
            world.Add(entity, given);
        }

        return entity;
    }

    private static Entity Floor(World world) =>
        Place(world, new Vector3(0f, -0.5f, 0f), Collider.Box(new Vector3(40f, 1f, 40f)), Body.Static);

    private static Entity Walker(World world, Vector3 position, Character? character = null)
    {
        var walker = Place(world, position, Collider.Capsule(1.8f, 0.4f));
        world.Add(walker, character ?? new Character());
        return walker;
    }

    /// <summary>Walks for some ticks with gravity, as a game would, and gives the last move.</summary>
    private static CharacterMove Walk(Physics physics, Entity walker, Vector3 velocity, int ticks)
    {
        var fall = 0f;
        CharacterMove move = default;
        for (int i = 0; i < ticks; i++)
        {
            physics.Step(Tick);
            fall = move.Grounded ? 0f : fall - (9.81f * Tick);
            move = physics.Move(walker, velocity with { Y = fall }, Tick);
            fall = move.Velocity.Y;
        }

        return move;
    }

    [Fact]
    public void A_character_falls_and_lands_on_the_floor()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var floor = Floor(world);
        var walker = Walker(world, new Vector3(0f, 3f, 0f));

        var move = Walk(physics, walker, Vector3.Zero, 90);

        Assert.True(move.Grounded);
        Assert.Equal(floor, move.Ground);
        Assert.Equal(Vector3.UnitY, move.GroundNormal);
        Assert.Equal(0f, world.Get<Transform>(walker).Position.Y, 0.05f);
    }

    [Fact]
    public void A_character_walks_at_its_speed()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Floor(world);
        var walker = Walker(world, Vector3.Zero);
        Walk(physics, walker, Vector3.Zero, 10);

        Walk(physics, walker, new Vector3(3f, 0f, 0f), 60);

        Assert.Equal(3f, world.Get<Transform>(walker).Position.X, 0.1f);
    }

    [Fact]
    public void A_wall_stops_a_character_and_it_slides_along()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Floor(world);
        Place(world, new Vector3(2.5f, 1.5f, 0f), Collider.Box(new Vector3(1f, 3f, 20f)), Body.Static);
        var walker = Walker(world, Vector3.Zero);

        Walk(physics, walker, new Vector3(4f, 0f, 2f), 90);

        // The wall's face is at x = 2, and the capsule's radius is 0.4.
        var position = world.Get<Transform>(walker).Position;
        Assert.Equal(1.6f, position.X, 0.05f);
        Assert.True(position.Z > 2.5f, $"It stopped at {position}.");
    }

    [Fact]
    public void A_character_climbs_a_step_but_not_a_ledge()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Floor(world);
        Place(world, new Vector3(2.5f, 0.1f, 0f), Collider.Box(new Vector3(1f, 0.2f, 1f)), Body.Static);
        Place(world, new Vector3(2.5f, 0.3f, 3f), Collider.Box(new Vector3(1f, 0.6f, 1f)), Body.Static);
        var climber = Walker(world, Vector3.Zero);
        var blocked = Walker(world, new Vector3(0f, 0f, 3f));

        Walk(physics, climber, new Vector3(2.5f, 0f, 0f), 60);
        Walk(physics, blocked, new Vector3(2.5f, 0f, 0f), 60);

        Assert.True(world.Get<Transform>(climber).Position.X > 2.2f, $"The climber stopped at {world.Get<Transform>(climber).Position}.");
        Assert.True(world.Get<Transform>(blocked).Position.X < 1.65f, $"The blocked one reached {world.Get<Transform>(blocked).Position}.");
    }

    [Fact]
    public void A_slope_steeper_than_its_limit_is_steep_ground()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var ramp = world.Create();
        world.Add(ramp, new Transform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 60f * MathF.PI / 180f), Vector3.One));
        world.Add(ramp, Collider.Box(new Vector3(20f, 1f, 20f)));
        world.Add(ramp, Body.Static);
        var gentle = world.Create();
        world.Add(gentle, new Transform(new Vector3(0f, 0f, 30f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 20f * MathF.PI / 180f), Vector3.One));
        world.Add(gentle, Collider.Box(new Vector3(20f, 1f, 20f)));
        world.Add(gentle, Body.Static);
        var steep = Walker(world, new Vector3(0f, 4f, 0f));
        var easy = Walker(world, new Vector3(0f, 4f, 30f));

        // Both surfaces are about 3 m below, which takes some 47 ticks to fall.
        var onSteep = Walk(physics, steep, Vector3.Zero, 60);
        var onEasy = Walk(physics, easy, Vector3.Zero, 60);

        Assert.Equal(Footing.SteepGround, onSteep.Footing);
        Assert.Equal(Footing.Ground, onEasy.Footing);
    }

    [Fact]
    public void A_character_pushes_a_crate()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Floor(world);
        var crate = Place(world, new Vector3(2f, 0.5f, 0f), Collider.Box(Vector3.One), Body.Dynamic(mass: 10f));
        var walker = Walker(world, Vector3.Zero);

        Walk(physics, walker, new Vector3(2f, 0f, 0f), 120);

        Assert.True(world.Get<Transform>(crate).Position.X > 2.5f, $"The crate is at {world.Get<Transform>(crate).Position}.");
    }

    [Fact]
    public void Setting_the_transform_teleports_a_character()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Floor(world);
        var walker = Walker(world, Vector3.Zero);
        Walk(physics, walker, Vector3.Zero, 5);

        world.Get<Transform>(walker).Position = new Vector3(8f, 0f, 0f);
        var move = physics.Move(walker, new Vector3(1f, 0f, 0f), Tick);

        Assert.Equal(8f + (1f / 60f), move.Position.X, 0.001f);
    }

    [Fact]
    public void A_ray_finds_a_character()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Floor(world);
        var walker = Walker(world, Vector3.Zero);
        Walk(physics, walker, new Vector3(0f, 0f, -2f), 60);
        physics.Sync();

        var hit = physics.Raycast(new Ray(new Vector3(0f, 1f, 10f), -Vector3.UnitZ));

        Assert.Equal(walker, hit!.Value.Entity);
    }

    [Fact]
    public void Moving_without_a_character_names_the_entity()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var box = Place(world, Vector3.Zero, Collider.Box(Vector3.One));

        var error = Assert.Throws<InvalidOperationException>(() => physics.Move(box, Vector3.UnitX, Tick));

        Assert.Equal($"Entity {box.Id} can't Move: it needs a Character, a Collider and a Transform.", error.Message);
    }
}
