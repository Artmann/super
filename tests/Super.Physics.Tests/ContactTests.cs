using System.Numerics;
using Super.Ecs;

namespace Super.Physics.Tests;

public class ContactTests
{
    private const float Tick = 1f / 60f;

    /// <summary>A world with a wide floor whose top is at height 0, made first so it comes first in entity order.</summary>
    private static (World World, Physics Physics, Entity Floor) Make() => Make(new World());

    private static (World World, Physics Physics, Entity Floor) Make(World world)
    {
        var floor = world.Create();
        world.Add(floor, Transform.At(new Vector3(0f, -0.5f, 0f)));
        world.Add(floor, Collider.Box(new Vector3(40f, 1f, 40f)));
        world.Add(floor, Body.Static);
        return (world, new Physics(world), floor);
    }

    private static Entity Ball(World world, Vector3 position)
    {
        var ball = world.Create();
        world.Add(ball, Transform.At(position));
        world.Add(ball, Collider.Ball(0.5f));
        world.Add(ball, Body.Dynamic(drag: 0f));
        return ball;
    }

    /// <summary>Steps for the given time and keeps every contact reported, with the step it came in.</summary>
    private static List<(int Step, Contact Contact)> Run(Physics physics, float seconds)
    {
        var contacts = new List<(int, Contact)>();
        for (int step = 0; step < (int)MathF.Round(seconds / Tick); step++)
        {
            physics.Step(Tick);
            contacts.AddRange(physics.Contacts.Select(c => (step, c)));
        }

        return contacts;
    }

    [Fact]
    public void A_ball_landing_reports_one_contact_at_the_speed_it_fell()
    {
        var (world, physics, floor) = Make();
        using var _ = physics;
        var ball = Ball(world, new Vector3(1f, 5.5f, 2f));

        var contacts = Run(physics, 3f);

        // Falling 5 m from rest under 9.81 m/s²: √(2 × 9.81 × 5) ≈ 9.9 m/s.
        var landing = Assert.Single(contacts).Contact;
        Assert.Equal(floor, landing.Entity);
        Assert.Equal(ball, landing.Other);
        Assert.Equal(9.9f, landing.Speed, 0.4f);
        Assert.True(Vector3.Distance(Vector3.UnitY, landing.Normal) < 0.01f, $"normal {landing.Normal}");
        Assert.True(Vector3.Distance(new Vector3(1f, 0f, 2f), landing.Point) < 0.1f, $"point {landing.Point}");
    }

    [Fact]
    public void A_ball_made_before_the_floor_comes_first_with_the_normal_pointing_down()
    {
        // The ball's entity comes first, but its collider is added after the floor's body, so Jolt sees it second.
        var world = new World();
        var ball = world.Create();
        var (_, physics, floor) = Make(world);
        using var _ = physics;
        physics.Sync();
        world.Add(ball, Transform.At(new Vector3(0f, 2.5f, 0f)));
        world.Add(ball, Collider.Ball(0.5f));
        world.Add(ball, Body.Dynamic(drag: 0f));

        var landing = Assert.Single(Run(physics, 2f)).Contact;

        Assert.Equal((ball, floor), (landing.Entity, landing.Other));
        Assert.True(Vector3.Distance(-Vector3.UnitY, landing.Normal) < 0.01f, $"normal {landing.Normal}");
    }

    [Fact]
    public void A_body_resting_on_the_floor_reports_nothing_more()
    {
        var (world, physics, _) = Make();
        using var __ = physics;
        Ball(world, new Vector3(0f, 0.5f, 0f));
        Run(physics, 0.5f);

        Assert.Empty(Run(physics, 2f));
    }

    [Fact]
    public void Two_balls_meeting_report_the_closing_speed_once_with_the_lower_entity_first()
    {
        var (world, physics, _) = Make();
        using var __ = physics;
        physics.Gravity = Vector3.Zero;
        var left = Ball(world, new Vector3(-2f, 3f, 0f));
        var right = Ball(world, new Vector3(2f, 3f, 0f));
        physics.Sync();
        physics.SetVelocity(right, new Vector3(-3f, 0f, 0f));
        physics.SetVelocity(left, new Vector3(1f, 0f, 0f));

        var contacts = Run(physics, 2f).Where(c => c.Contact.Other == right).ToList();

        var meeting = Assert.Single(contacts).Contact;
        Assert.Equal(left, meeting.Entity);
        Assert.Equal(4f, meeting.Speed, 0.1f);
        Assert.True(Vector3.Distance(Vector3.UnitX, meeting.Normal) < 0.01f, $"normal {meeting.Normal}");
    }

    [Fact]
    public void Falling_through_a_trigger_is_not_a_contact()
    {
        var (world, physics, _) = Make();
        using var __ = physics;
        var area = world.Create();
        world.Add(area, Transform.At(new Vector3(0f, 3f, 0f)));
        world.Add(area, Collider.Box(new Vector3(4f, 2f, 4f)));
        world.Add(area, new Trigger());
        Ball(world, new Vector3(0f, 6f, 0f));

        var contacts = Run(physics, 3f);

        Assert.DoesNotContain(contacts, c => c.Contact.Entity == area || c.Contact.Other == area);
        Assert.Single(contacts);
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void A_ragdoll_reports_one_contact_per_step_with_the_floor_and_none_with_itself()
    {
        var (world, physics, floor) = Make();
        using var _ = physics;
        var (body, animator) = TestHumanoid.Place(world, Transform.At(new Vector3(0f, 2f, 0f)), TestHumanoid.Limp(RagdollBody.Humanoid(TestHumanoid.Rig)));

        var contacts = new List<(int Step, Contact Contact)>();
        for (int step = 0; step < 180; step++)
        {
            physics.Step(Tick);
            animator.Advance(Tick);
            contacts.AddRange(physics.Contacts.Select(c => (step, c)));
        }

        Assert.NotEmpty(contacts);
        Assert.All(contacts, c => Assert.Equal((floor, body), (c.Contact.Entity, c.Contact.Other)));
        Assert.Equal(contacts.Count, contacts.Select(c => c.Step).Distinct().Count());
    }
}
