using System.Numerics;

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

    private record struct Gem;

    private record struct Lava;

    private record struct Hero;

    /// <summary>A gem area and a lava area side by side, with a hero and a plain rock in each.</summary>
    private static (Entity Gem, Entity Lava, Entity[] Heroes, Entity[] Rocks) Field(World world)
    {
        var gem = Area(world, new Vector3(-10f, 0f, 0f), new Vector3(4f));
        world.Add(gem, new Gem());
        var lava = Area(world, new Vector3(10f, 0f, 0f), new Vector3(4f));
        world.Add(lava, new Lava());
        Entity[] heroes = [Place(world, new Vector3(-10f, 0f, 1f), Collider.Ball(0.5f)), Place(world, new Vector3(10f, 0f, 1f), Collider.Ball(0.5f))];
        Entity[] rocks = [Place(world, new Vector3(-10f, 0f, -1f), Collider.Ball(0.5f)), Place(world, new Vector3(10f, 0f, -1f), Collider.Ball(0.5f))];
        foreach (var hero in heroes)
        {
            world.Add(hero, new Hero());
        }

        return (gem, lava, heroes, rocks);
    }

    private static List<TriggerEvent> Listed<TTrigger, TOther>(TriggerPairs<TTrigger, TOther> pairs)
        where TTrigger : struct
        where TOther : struct
    {
        var listed = new List<TriggerEvent>();
        foreach (var pair in pairs)
        {
            listed.Add(pair);
        }

        return listed;
    }

    [Fact]
    public void Entered_by_component_keeps_only_the_pairs_with_both_components()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var (gem, lava, heroes, rocks) = Field(world);

        physics.Step(Tick);

        Assert.Equal(4, physics.Triggers.Entered.Count);
        Assert.Equal([new TriggerEvent(gem, heroes[0])], Listed(physics.Triggers.Entered<Gem, Hero>()));
        Assert.Equal([new TriggerEvent(lava, heroes[1])], Listed(physics.Triggers.Entered<Lava, Hero>()));
        Assert.Equal([new TriggerEvent(gem, heroes[0]), new TriggerEvent(gem, rocks[0])], Listed(physics.Triggers.Entered<Gem>()));
        Assert.True(physics.Triggers.Entered<Gem, Hero>().Any());
        Assert.False(physics.Triggers.Entered<Hero, Gem>().Any());
    }

    [Fact]
    public void Inside_by_component_lists_who_is_still_in_after_entering()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var (gem, _, heroes, _) = Field(world);

        physics.Step(Tick);
        physics.Step(Tick);

        Assert.Empty(physics.Triggers.Entered);
        Assert.Equal([new TriggerEvent(gem, heroes[0])], Listed(physics.Triggers.Inside<Gem, Hero>()));
    }

    [Fact]
    public void Exited_by_component_lists_who_left_and_skips_the_destroyed_unless_anyone_is_asked_for()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var (gem, lava, heroes, rocks) = Field(world);
        physics.Step(Tick);

        world.Get<Transform>(heroes[1]).Position = new Vector3(10f, 50f, 0f);
        world.Destroy(heroes[0]);
        physics.Step(Tick);

        Assert.Equal([new TriggerEvent(lava, heroes[1])], Listed(physics.Triggers.Exited<Lava, Hero>()));
        Assert.Empty(Listed(physics.Triggers.Exited<Gem, Hero>()));
        Assert.Equal([new TriggerEvent(gem, heroes[0])], Listed(physics.Triggers.Exited<Gem>()));
        Assert.Equal([new TriggerEvent(gem, rocks[0])], Listed(physics.Triggers.Inside<Gem, Anyone>()));
    }

    [Fact]
    public void An_area_destroyed_while_walking_its_pairs_drops_out_of_the_rest()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var (gem, _, _, _) = Field(world);
        physics.Step(Tick);
        int seen = 0;

        foreach (var pair in physics.Triggers.Entered<Gem>())
        {
            seen++;
            world.Destroy(pair.Trigger);
        }

        Assert.Equal(1, seen);
        Assert.False(world.IsAlive(gem));
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
            entered.AddRange(physics.Triggers.Entered.Select(e => (tick, e)));
            exited.AddRange(physics.Triggers.Exited.Select(e => (tick, e)));
            wasInside |= physics.Triggers.Inside(area).Contains(ball);
        }

        Assert.Equal([new TriggerEvent(area, ball)], entered.Select(e => e.Event));
        Assert.Equal([new TriggerEvent(area, ball)], exited.Select(e => e.Event));
        Assert.True(entered[0].Tick < exited[0].Tick);
        Assert.True(wasInside);
        Assert.Empty(physics.Triggers.Inside(area));
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

        Assert.Equal([new TriggerEvent(area, b), new TriggerEvent(area, a)], physics.Triggers.Entered);
        Assert.Equal([b, a], physics.Triggers.Inside(area));
    }

    [Fact]
    public void Moving_a_kinematic_collider_in_and_out_reports_it()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var area = Area(world, Vector3.Zero, new Vector3(2f));
        var door = Place(world, new Vector3(5f, 0f, 0f), Collider.Box(Vector3.One));
        physics.Step(Tick);
        Assert.Empty(physics.Triggers.Entered);

        world.Get<Transform>(door).Position = Vector3.Zero;
        physics.Step(Tick);
        Assert.Equal([new TriggerEvent(area, door)], physics.Triggers.Entered);

        physics.Step(Tick);
        Assert.Empty(physics.Triggers.Entered);
        Assert.Empty(physics.Triggers.Exited);

        world.Get<Transform>(door).Position = new Vector3(5f, 0f, 0f);
        physics.Step(Tick);
        Assert.Equal([new TriggerEvent(area, door)], physics.Triggers.Exited);
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

        Assert.Equal([new TriggerEvent(area, coin)], physics.Triggers.Exited);
    }

    [Fact]
    public void Triggers_do_not_notice_each_other()
    {
        var (world, physics) = Make();
        using var _ = physics;
        Area(world, Vector3.Zero, new Vector3(2f));
        Area(world, Vector3.Zero, new Vector3(1f));

        physics.Step(Tick);

        Assert.Empty(physics.Triggers.Entered);
    }
}
