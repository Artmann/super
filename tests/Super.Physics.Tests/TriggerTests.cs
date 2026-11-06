using System.Numerics;
using Super.Ecs;

namespace Super.Physics.Tests;

public class TriggerTests
{
    private const float Tick = 1f / 60f;

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

    private static Entity Area(World world, Vector3 position, Vector3 size, uint detects = Physics.AllLayers)
    {
        var area = Place(world, position, Collider.Box(size));
        world.Add(area, new Trigger(detects));
        return area;
    }

    [Fact]
    public void A_falling_ball_enters_once_and_leaves_once()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var area = Area(world, new Vector3(0f, 5f, 0f), new Vector3(4f, 2f, 4f));
        var ball = Place(world, new Vector3(0f, 10f, 0f), Collider.Ball(0.5f));
        world.Add(ball, Body.Dynamic(drag: 0f));
        var entered = new List<(int Tick, TriggerEvent Event)>();
        var exited = new List<(int Tick, TriggerEvent Event)>();
        bool wasInside = false;

        for (int tick = 0; tick < 120; tick++)
        {
            physics.Step(Tick);
            entered.AddRange(physics.Entered.Select(e => (tick, e)));
            exited.AddRange(physics.Exited.Select(e => (tick, e)));
            wasInside |= physics.Inside(area).Contains(ball);
        }

        Assert.Equal([new TriggerEvent(area, ball)], entered.Select(e => e.Event));
        Assert.Equal([new TriggerEvent(area, ball)], exited.Select(e => e.Event));
        Assert.True(entered[0].Tick < exited[0].Tick);
        Assert.True(wasInside);
        Assert.Empty(physics.Inside(area));
    }

    [Fact]
    public void A_dynamic_body_falls_through_a_trigger()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Area(world, new Vector3(0f, 0f, 0f), new Vector3(10f, 1f, 10f));
        var ball = Place(world, new Vector3(0f, 3f, 0f), Collider.Ball(0.5f));
        world.Add(ball, Body.Dynamic());

        for (int tick = 0; tick < 60; tick++)
        {
            physics.Step(Tick);
        }

        Assert.True(world.Get<Transform>(ball).Position.Y < -1f);
    }

    [Fact]
    public void A_trigger_notices_only_the_layers_it_detects_and_reports_in_entity_order()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var area = Area(world, Vector3.Zero, new Vector3(4f), detects: 1);
        var b = Place(world, new Vector3(1f, 0f, 0f), Collider.Ball(0.5f));
        Place(world, Vector3.Zero, Collider.Ball(0.5f, layers: 2));
        var a = Place(world, new Vector3(-1f, 0f, 0f), Collider.Ball(0.5f));

        physics.Step(Tick);

        Assert.Equal([new TriggerEvent(area, b), new TriggerEvent(area, a)], physics.Entered);
        Assert.Equal([b, a], physics.Inside(area));
    }

    [Fact]
    public void Moving_a_kinematic_collider_in_and_out_reports_it()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var area = Area(world, Vector3.Zero, new Vector3(2f));
        var door = Place(world, new Vector3(5f, 0f, 0f), Collider.Box(Vector3.One));
        physics.Step(Tick);
        Assert.Empty(physics.Entered);

        world.Get<Transform>(door).Position = Vector3.Zero;
        physics.Step(Tick);
        Assert.Equal([new TriggerEvent(area, door)], physics.Entered);

        physics.Step(Tick);
        Assert.Empty(physics.Entered);
        Assert.Empty(physics.Exited);

        world.Get<Transform>(door).Position = new Vector3(5f, 0f, 0f);
        physics.Step(Tick);
        Assert.Equal([new TriggerEvent(area, door)], physics.Exited);
    }

    [Fact]
    public void An_entity_destroyed_inside_leaves()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var area = Area(world, Vector3.Zero, new Vector3(2f));
        var coin = Place(world, Vector3.Zero, Collider.Ball(0.2f));
        physics.Step(Tick);

        world.Destroy(coin);
        physics.Step(Tick);

        Assert.Equal([new TriggerEvent(area, coin)], physics.Exited);
    }

    [Fact]
    public void Triggers_do_not_notice_each_other()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Area(world, Vector3.Zero, new Vector3(2f));
        Area(world, Vector3.Zero, new Vector3(1f));

        physics.Step(Tick);

        Assert.Empty(physics.Entered);
    }
}
