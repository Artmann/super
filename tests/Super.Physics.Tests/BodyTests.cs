using System.Numerics;
using Super.Ecs;
using Super.Testing;

namespace Super.Physics.Tests;

public class BodyTests
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

    private static void Run(Physics physics, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            physics.Step(Tick);
        }
    }

    [Fact]
    public void A_dynamic_body_falls_as_gravity_says()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var ball = Place(world, new Vector3(0f, 100f, 0f), Collider.Ball(0.5f), Body.Dynamic(drag: 0f));

        Run(physics, 60);

        // Half of g times t squared: 4.905 m in one second, give or take the steps.
        Assert.Equal(100f - 4.905f, world.Get<Transform>(ball).Position.Y, 0.15f);
        Assert.Equal(-9.81f, physics.Velocity(ball).Y, 0.05f);
    }

    [Fact]
    public void A_box_comes_to_rest_on_the_floor()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Floor(world);
        var box = Place(world, new Vector3(0f, 3f, 0f), Collider.Box(Vector3.One), Body.Dynamic());

        Run(physics, 180);

        // Jolt lets resting bodies sink up to 2 cm, so contacts stay steady.
        var transform = world.Get<Transform>(box);
        Assert.Equal(0.5f, transform.Position.Y, 0.03f);
        Assert.Equal(0f, physics.Velocity(box).Length(), 0.05f);
    }

    [Fact]
    public void A_bouncy_ball_bounces_and_a_dull_one_does_not()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Floor(world);
        var bouncy = Place(world, new Vector3(-5f, 2f, 0f), Collider.Ball(0.5f), Body.Dynamic(bounciness: 0.9f, drag: 0f));
        var dull = Place(world, new Vector3(5f, 2f, 0f), Collider.Ball(0.5f), Body.Dynamic(bounciness: 0f, drag: 0f));

        // It reaches the floor after about 0.48 s; give it a little more to bounce.
        Run(physics, 45);

        Assert.True(physics.Velocity(bouncy).Y > 3f, $"The bouncy ball rises at {physics.Velocity(bouncy).Y}.");
        Assert.Equal(0f, physics.Velocity(dull).Y, 0.3f);
    }

    [Fact]
    public void A_push_changes_the_velocity_by_the_impulse_over_the_mass()
    {
        var (world, physics) = Make();
        using var _ = physics;
        physics.Gravity = Vector3.Zero;
        var crate = Place(world, Vector3.Zero, Collider.Box(Vector3.One), Body.Dynamic(mass: 4f, drag: 0f));

        physics.Push(crate, new Vector3(8f, 0f, 0f));
        Run(physics, 60);

        Assert.Equal(new Vector3(2f, 0f, 0f), physics.Velocity(crate));
        Assert.Equal(2f, world.Get<Transform>(crate).Position.X, 0.01f);
    }

    [Fact]
    public void Setting_a_dynamic_transform_teleports_it_and_keeps_its_speed()
    {
        var (world, physics) = Make();
        using var _ = physics;
        physics.Gravity = Vector3.Zero;
        var ball = Place(world, Vector3.Zero, Collider.Ball(0.5f), Body.Dynamic(drag: 0f));
        physics.SetVelocity(ball, new Vector3(0f, 0f, 6f));
        Run(physics, 1);

        world.Get<Transform>(ball).Position = new Vector3(10f, 0f, 0f);
        Run(physics, 1);

        // It jumped back to z = 0 and moved on one step at 6 m/s from there.
        var position = world.Get<Transform>(ball).Position;
        Assert.Equal(10f, position.X, 0.001f);
        Assert.Equal(0.1f, position.Z, 0.001f);
        Assert.Equal(new Vector3(0f, 0f, 6f), physics.Velocity(ball));
    }

    [Fact]
    public void A_moving_kinematic_collider_pushes_a_dynamic_body()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Floor(world);
        var pusher = Place(world, new Vector3(-2f, 1f, 0f), Collider.Box(new Vector3(1f, 2f, 3f)));
        var crate = Place(world, new Vector3(0f, 0.5f, 0f), Collider.Box(Vector3.One), Body.Dynamic());
        Run(physics, 10);

        for (int i = 0; i < 60; i++)
        {
            world.Get<Transform>(pusher).Position += new Vector3(2f * Tick, 0f, 0f);
            physics.Step(Tick);
        }

        // The pusher's face went from x = -1.5 to 0.5, so the crate's centre is past 1.
        Assert.True(world.Get<Transform>(crate).Position.X > 0.9f, $"The crate is at {world.Get<Transform>(crate).Position}.");
    }

    [Fact]
    public void Bodies_on_layers_they_do_not_collide_with_pass_through()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Place(world, new Vector3(0f, -0.5f, 0f), Collider.Box(new Vector3(40f, 1f, 40f), layers: 2), Body.Static);
        var ghost = Place(world, new Vector3(0f, 1f, 0f), Collider.Ball(0.5f), Body.Dynamic(collidesWith: 1));

        Run(physics, 60);

        Assert.True(world.Get<Transform>(ghost).Position.Y < -1f, $"The ghost stopped at {world.Get<Transform>(ghost).Position}.");
    }

    [Fact]
    public void A_new_body_is_kinematic_and_collides_with_everything()
    {
        Assert.Equal(Body.Kinematic, new Body());
        Assert.Equal(BodyKind.Kinematic, new Body().Kind);
        Assert.Equal(Physics.AllLayers, new Body().CollidesWith);
    }

    [Fact]
    public void A_locked_body_does_not_turn()
    {
        var (world, physics) = Make();
        using var _ = physics;
        physics.Gravity = Vector3.Zero;
        var locked = Place(world, Vector3.Zero, Collider.Box(Vector3.One), Body.Dynamic(lockRotation: true));
        var free = Place(world, new Vector3(5f, 0f, 0f), Collider.Box(Vector3.One), Body.Dynamic());

        physics.Push(locked, Vector3.UnitZ, new Vector3(0.5f, 0.5f, 0f));
        physics.Push(free, Vector3.UnitZ, new Vector3(5.5f, 0.5f, 0f));
        Run(physics, 30);

        Assert.Equal(Quaternion.Identity, world.Get<Transform>(locked).Rotation);
        Assert.NotEqual(Quaternion.Identity, world.Get<Transform>(free).Rotation);
    }

    [Fact]
    public void A_dynamic_body_with_a_parent_names_its_entity()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var parent = world.Create();
        world.Add(parent, Transform.Identity);
        var child = Place(world, Vector3.Zero, Collider.Ball(0.5f), Body.Dynamic());
        world.Add(child, new Parent(parent));

        var error = Assert.Throws<InvalidOperationException>(() => physics.Step(Tick));

        Assert.Equal($"Entity {child.Id} has a dynamic Body and a Parent; physics moves a dynamic body in the world, so it can't follow a parent.", error.Message);
    }

    [Fact]
    public void Pushing_a_static_body_explains_why_not()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var wall = Place(world, Vector3.Zero, Collider.Box(Vector3.One), Body.Static);

        var error = Assert.Throws<InvalidOperationException>(() => physics.Push(wall, Vector3.UnitX));

        Assert.Equal($"Entity {wall.Id} has no dynamic Body; only dynamic bodies can be pushed or given a velocity.", error.Message);
    }

    [Fact]
    public void A_massless_body_names_its_entity()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var ball = Place(world, Vector3.Zero, Collider.Ball(0.5f), Body.Dynamic(mass: 0f));

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => physics.Step(Tick));

        Assert.StartsWith($"Entity {ball.Id}'s Body has mass 0; it must be above 0.", error.Message);
    }

    [Fact]
    public void Destroying_a_falling_body_removes_it()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var ball = Place(world, new Vector3(0f, 5f, 0f), Collider.Ball(0.5f), Body.Dynamic());
        Run(physics, 5);

        world.Destroy(ball);
        Run(physics, 1);

        Assert.Equal(0, physics.Count);
    }

    [Fact]
    public void A_pile_falls_the_same_way_every_time()
    {
        for (int seed = 1; seed <= 12; seed++)
        {
            Assert.Equal(Pile(seed), Pile(seed));
        }
    }

    [Fact]
    public void Different_seeds_make_different_piles()
    {
        Assert.NotEqual(Pile(1), Pile(2));
    }

    /// <summary>Drops 60 boxes and balls from places a seed picks, and gives every final position and rotation, in order.</summary>
    private static string Pile(int seed)
    {
        var (world, physics) = Make();
        using var _ = physics;
        var random = new System.Random(seed);
        Floor(world);
        var falling = new List<Entity>();
        for (int i = 0; i < 60; i++)
        {
            var at = new Vector3((random.NextSingle() * 4f) - 2f, 1f + (i * 0.6f), (random.NextSingle() * 4f) - 2f);
            var collider = i % 2 == 0 ? Collider.Box(new Vector3(0.6f)) : Collider.Ball(0.35f);
            falling.Add(Place(world, at, collider, Body.Dynamic(bounciness: 0.2f)));
        }

        Run(physics, 240);

        return string.Join(";", falling.Select(entity =>
        {
            var transform = world.Get<Transform>(entity);
            return $"{transform.Position.X:R},{transform.Position.Y:R},{transform.Position.Z:R},{transform.Rotation.X:R},{transform.Rotation.Y:R},{transform.Rotation.Z:R},{transform.Rotation.W:R}";
        }));
    }

    private sealed class Dropping(World world) : IGame
    {
        public Entity Ball { get; private set; }

        public float Height => world.Get<Transform>(Ball).Position.Y;

        /// <summary>The ball's height as drawn, blended from last tick (0) to this one (1).</summary>
        public float Drawn(float blend) => ViewExtensions.WorldMatrix(world, Ball, blend, depth: 0).Translation.Y;

        public void Load()
        {
            Ball = Place(world, new Vector3(0f, 50f, 0f), Collider.Ball(0.5f), Body.Dynamic(drag: 0f));
            world.Add(Ball, new Smoothed());
        }

        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }
    }

    [Fact]
    public void The_game_loop_steps_bodies_before_update()
    {
        using var run = HeadlessGame.Create<Dropping>(1, services => services.AddSuperPhysics());

        // 30 ticks a second, so two seconds: half of g times t squared is 19.62 m.
        run.Ticks(60);

        Assert.Equal(50f - 19.62f, run.Game.Height, 0.5f);
    }

    [Fact]
    public void Smoothing_blends_from_where_a_body_was_before_the_step()
    {
        using var run = HeadlessGame.Create<Dropping>(1, services => services.AddSuperPhysics());

        run.Ticks(30);

        // A second in, it falls about 0.33 m a tick, so last tick's place is clearly higher.
        Assert.True(run.Game.Drawn(0f) - run.Game.Drawn(1f) > 0.25f, $"Last tick {run.Game.Drawn(0f)}, this tick {run.Game.Drawn(1f)}.");
        Assert.Equal(run.Game.Height, run.Game.Drawn(1f));
    }
}
