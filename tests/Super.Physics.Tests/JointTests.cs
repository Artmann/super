using System.Numerics;
using Super.Ecs;

namespace Super.Physics.Tests;

public class JointTests
{
    private const float Tick = 1f / 60f;

    private static Entity Ball(World world, Vector3 position)
    {
        var ball = world.Create();
        world.Add(ball, Transform.At(position));
        world.Add(ball, Collider.Ball(0.25f));
        world.Add(ball, Body.Dynamic(drag: 0f));
        return ball;
    }

    private static void Run(Physics physics, float seconds)
    {
        for (int i = 0; i < (int)MathF.Round(seconds / Tick); i++)
        {
            physics.Step(Tick);
        }
    }

    private static Vector3 At(World world, Entity entity) => world.Get<Transform>(entity).Position;

    [Fact]
    public void A_ball_on_a_rope_to_the_world_swings_down_but_never_goes_farther_than_the_rope()
    {
        var world = new World();
        using var physics = new Physics(world);
        var ball = Ball(world, new Vector3(2f, 10f, 0f));
        world.Add(ball, Joint.Rope(Vector3.Zero, null, new Vector3(0f, 10f, 0f), 2f));

        float farthest = 0f;
        float lowest = float.MaxValue;
        for (int i = 0; i < 180; i++)
        {
            physics.Step(Tick);
            farthest = MathF.Max(farthest, Vector3.Distance(new Vector3(0f, 10f, 0f), At(world, ball)));
            lowest = MathF.Min(lowest, At(world, ball).Y);
        }

        Assert.True(farthest < 2.05f, $"the rope stretched to {farthest} m");
        Assert.True(lowest < 8.2f, $"it only swung down to {lowest}");
    }

    [Fact]
    public void A_body_fixed_to_the_world_stays_put_when_pushed()
    {
        var world = new World();
        using var physics = new Physics(world);
        var box = world.Create();
        world.Add(box, Transform.At(new Vector3(0f, 5f, 0f)));
        world.Add(box, Collider.Box(Vector3.One));
        world.Add(box, Body.Dynamic());
        world.Add(box, Joint.Fixed());
        physics.Sync();
        physics.Push(box, new Vector3(5f, 0f, 0f), new Vector3(0f, 5.5f, 0f));

        Run(physics, 1f);

        Assert.True(Vector3.Distance(new Vector3(0f, 5f, 0f), At(world, box)) < 0.02f, $"it moved to {At(world, box)}");
        Assert.True(Quaternion.Dot(Quaternion.Identity, world.Get<Transform>(box).Rotation) > 0.9999f, "it turned");
    }

    /// <summary>A 1 m wide door whose left edge, at x 0, is hinged upright to the world, from <paramref name="min"/> to <paramref name="max"/> degrees.</summary>
    private static Entity Door(World world, float min = -180f, float max = 180f)
    {
        var door = world.Create();
        world.Add(door, Transform.At(new Vector3(0.5f, 1f, 0f)));
        world.Add(door, Collider.Box(new Vector3(1f, 2f, 0.1f)));
        world.Add(door, Body.Dynamic(drag: 0f));
        world.Add(door, Joint.Hinge(new Vector3(-0.5f, 0f, 0f), Vector3.UnitY, minAngle: min, maxAngle: max));
        return door;
    }

    /// <summary>How far the door has swung about its hinge, in degrees, towards -z.</summary>
    private static float Swing(World world, Entity door)
    {
        var centre = At(world, door);
        return MathF.Atan2(-centre.Z, centre.X) * 180f / MathF.PI;
    }

    [Fact]
    public void A_hinged_door_turns_about_its_hinge_and_doesn_t_fall()
    {
        var world = new World();
        using var physics = new Physics(world);
        var door = Door(world);
        physics.Sync();
        physics.Push(door, new Vector3(0f, 0f, -2f), new Vector3(1f, 1f, 0f));

        Run(physics, 0.5f);

        var centre = At(world, door);
        Assert.True(Swing(world, door) > 20f, $"it swung only {Swing(world, door)} degrees");
        Assert.Equal(1f, centre.Y, 0.02f);
        Assert.Equal(0.5f, new Vector2(centre.X, centre.Z).Length(), 0.02f);
    }

    [Fact]
    public void A_hinge_stops_at_its_limit()
    {
        var world = new World();
        using var physics = new Physics(world);
        var door = Door(world, min: -30f, max: 30f);
        physics.Sync();
        physics.Push(door, new Vector3(0f, 0f, -1f), new Vector3(1f, 1f, 0f));

        float widest = 0f;
        for (int i = 0; i < 60; i++)
        {
            physics.Step(Tick);
            widest = MathF.Max(widest, MathF.Abs(Swing(world, door)));
        }

        Assert.True(widest is > 28f and < 32.5f, $"it swung to {widest} degrees");
    }

    [Fact]
    public void A_rod_keeps_two_bodies_its_length_apart()
    {
        var world = new World();
        using var physics = new Physics(world);
        physics.Gravity = Vector3.Zero;
        var left = Ball(world, new Vector3(-1f, 5f, 0f));
        var right = Ball(world, new Vector3(1f, 5f, 0f));
        world.Add(right, Joint.Rod(Vector3.Zero, left, Vector3.Zero, 2f));
        physics.Sync();
        physics.SetVelocity(right, new Vector3(-3f, 1f, 0f));

        float shortest = float.MaxValue;
        float longest = 0f;
        for (int i = 0; i < 60; i++)
        {
            physics.Step(Tick);
            float apart = Vector3.Distance(At(world, left), At(world, right));
            shortest = MathF.Min(shortest, apart);
            longest = MathF.Max(longest, apart);
        }

        Assert.True(shortest > 1.95f && longest < 2.05f, $"they were from {shortest} to {longest} m apart");
        Assert.True(physics.Velocity(left).Length() > 0.5f, "the rod didn't push the other ball along");
    }

    [Fact]
    public void A_ball_joint_holds_the_point_but_lets_it_turn()
    {
        var world = new World();
        using var physics = new Physics(world);
        var stick = world.Create();
        world.Add(stick, Transform.At(new Vector3(0f, 4f, 0f)));
        world.Add(stick, Collider.Box(new Vector3(0.1f, 2f, 0.1f)));
        world.Add(stick, Body.Dynamic(drag: 0f));
        world.Add(stick, Joint.Ball(new Vector3(0f, 1f, 0f)));
        physics.Sync();
        physics.Push(stick, new Vector3(1f, 0f, 1f), new Vector3(0f, 3f, 0f));

        Run(physics, 1f);

        // The top, 1 m above the middle, stays at (0, 5, 0) however the stick has swung.
        var transform = world.Get<Transform>(stick);
        var top = transform.Position + Vector3.Transform(Vector3.UnitY, transform.Rotation);
        Assert.True(Vector3.Distance(new Vector3(0f, 5f, 0f), top) < 0.02f, $"the top moved to {top}");
        Assert.True(transform.Position.Y > 4.0f || MathF.Abs(transform.Position.X) + MathF.Abs(transform.Position.Z) > 0.2f, $"it didn't swing: {transform.Position}");
    }

    [Fact]
    public void Removing_the_joint_lets_go()
    {
        var world = new World();
        using var physics = new Physics(world);
        var ball = Ball(world, new Vector3(0f, 8f, 0f));
        world.Add(ball, Joint.Rope(Vector3.Zero, null, new Vector3(0f, 10f, 0f), 2f));
        Run(physics, 0.5f);
        Assert.True(At(world, ball).Y > 7.9f);

        world.Remove<Joint>(ball);
        Run(physics, 0.5f);

        Assert.True(At(world, ball).Y < 7f, $"it's still at {At(world, ball)}");
    }

    [Fact]
    public void Destroying_what_it_holds_on_to_lets_go()
    {
        var world = new World();
        using var physics = new Physics(world);
        var hook = world.Create();
        world.Add(hook, Transform.At(new Vector3(0f, 10f, 0f)));
        world.Add(hook, Collider.Ball(0.1f));
        world.Add(hook, Body.Static);
        var ball = Ball(world, new Vector3(0f, 8f, 0f));
        world.Add(ball, Joint.Rope(Vector3.Zero, hook, Vector3.Zero, 2f));
        Run(physics, 0.5f);
        Assert.True(At(world, ball).Y > 7.9f);

        world.Destroy(hook);
        Run(physics, 0.5f);

        Assert.True(At(world, ball).Y < 7f, $"it's still at {At(world, ball)}");
    }

    [Fact]
    public void A_joint_without_a_collider_says_it_needs_one()
    {
        var world = new World();
        using var physics = new Physics(world);
        var entity = world.Create();
        world.Add(entity, Transform.At(Vector3.Zero));
        world.Add(entity, Joint.Fixed());

        var error = Assert.Throws<InvalidOperationException>(() => physics.Step(Tick));

        Assert.Contains("needs a Collider", error.Message);
    }

    [Fact]
    public void A_joint_to_itself_is_refused()
    {
        var world = new World();
        using var physics = new Physics(world);
        var ball = Ball(world, Vector3.Zero);
        world.Add(ball, Joint.Fixed(ball));

        var error = Assert.Throws<InvalidOperationException>(() => physics.Step(Tick));

        Assert.Contains("joined to itself", error.Message);
    }

    [Fact]
    public void A_hinge_limit_past_half_a_turn_is_refused()
    {
        var world = new World();
        using var physics = new Physics(world);
        Door(world, min: -200f, max: 30f);

        var error = Assert.Throws<InvalidOperationException>(() => physics.Step(Tick));

        Assert.Contains("from -200 to 30 degrees", error.Message);
    }
}
