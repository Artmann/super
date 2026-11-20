using System.Numerics;

namespace Super.Physics.Tests;

public class JumperTests
{
    private const float Tick = 1f / 60f;

    private readonly World _world = new();

    private Entity Place(Vector3 position, Vector3 size)
    {
        var entity = _world.Create();
        _world.Add(entity, Transform.At(position));
        _world.Add(entity, Collider.Box(size));
        return entity;
    }

    /// <summary>A hero standing on a floor whose top is at 0, after enough ticks to have landed.</summary>
    private Entity Hero(Physics physics, Jumper jumper, bool floor = true)
    {
        if (floor)
        {
            Place(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));
        }

        var hero = _world.Create();
        _world.Add(hero, Transform.At(new Vector3(0f, 0.05f, 0f)));
        _world.Add(hero, Collider.Capsule(1.8f, 0.4f));
        _world.Add(hero, new Character());
        _world.Add(hero, jumper);
        Walk(physics, hero, Vector3.Zero, jump: false, ticks: 20);
        return hero;
    }

    private CharacterMove Walk(Physics physics, Entity hero, Vector3 velocity, bool jump, int ticks)
    {
        CharacterMove move = default;
        for (int i = 0; i < ticks; i++)
        {
            physics.Step(Tick);
            move = physics.Characters.Walk(hero, velocity, jump, Tick);
        }

        return move;
    }

    /// <summary>Walks until the hero is back on the ground, and gives the highest it got.</summary>
    private float Peak(Physics physics, Entity hero, Func<int, bool> jump)
    {
        float peak = 0f;
        for (int tick = 0; tick < 120; tick++)
        {
            physics.Step(Tick);
            physics.Characters.Walk(hero, Vector3.Zero, jump(tick), Tick);
            peak = MathF.Max(peak, _world.Get<Transform>(hero).Position.Y);
        }

        return peak;
    }

    [Fact]
    public void A_jumper_falls_to_the_ground_and_stands_there()
    {
        using var physics = new Physics(_world);
        var hero = Hero(physics, new Jumper());

        var move = Walk(physics, hero, Vector3.Zero, jump: false, ticks: 1);

        Assert.True(move.Grounded);
        Assert.Equal(0f, _world.Get<Transform>(hero).Position.Y, 0.02f);
        Assert.Equal(0f, _world.Get<Jumper>(hero).Rise);
        Assert.Equal(0f, _world.Get<Jumper>(hero).AirTime);
    }

    [Fact]
    public void A_held_jump_rises_as_high_as_its_speed_and_gravity_give()
    {
        using var physics = new Physics(_world);
        var hero = Hero(physics, new Jumper(JumpSpeed: 6f, Gravity: 12f));

        float peak = Peak(physics, hero, _ => true);

        // Speed squared over twice the gravity: 36 / 24 is 1.5 m.
        Assert.Equal(1.5f, peak, 0.1f);
        Assert.True(Walk(physics, hero, Vector3.Zero, jump: true, ticks: 1).Grounded);
    }

    [Fact]
    public void Holding_the_button_after_landing_doesnt_jump_again()
    {
        using var physics = new Physics(_world);
        var hero = Hero(physics, new Jumper(JumpSpeed: 6f, Gravity: 12f));
        Peak(physics, hero, _ => true);

        float second = Peak(physics, hero, _ => true);

        Assert.Equal(0f, second, 0.02f);
    }

    [Fact]
    public void Letting_go_on_the_way_up_makes_a_lower_hop()
    {
        using var physics = new Physics(_world);
        var hero = Hero(physics, new Jumper(JumpSpeed: 6f, Gravity: 12f, ShortHop: 0.5f));

        // Held for six ticks, a tenth of a second: it has risen 0.54 m and still rises at 4.8 m/s. Letting go
        // halves that to 2.4 m/s, which is worth 0.24 m more.
        float peak = Peak(physics, hero, tick => tick < 6);

        Assert.Equal(0.78f, peak, 0.1f);
    }

    [Fact]
    public void A_jump_still_works_just_after_walking_off_an_edge_but_not_long_after()
    {
        using var physics = new Physics(_world);
        var hero = Hero(physics, new Jumper(JumpSpeed: 6f, Gravity: 12f, Forgiveness: 0.1f), floor: false);
        _world.Teleport(hero, Transform.At(new Vector3(0f, 10f, 0f)));
        _world.Get<Jumper>(hero) = new Jumper(JumpSpeed: 6f, Gravity: 12f, Forgiveness: 0.1f);

        // Three ticks in the air is 0.05 s, inside the 0.1 s it forgives.
        Walk(physics, hero, Vector3.Zero, jump: false, ticks: 3);
        Walk(physics, hero, Vector3.Zero, jump: true, ticks: 1);
        Assert.Equal(6f, _world.Get<Jumper>(hero).Rise, 0.01f);

        // Dropped again with no jump behind it, half a second in the air is too late.
        _world.Get<Jumper>(hero) = new Jumper(JumpSpeed: 6f, Gravity: 12f, Forgiveness: 0.1f);
        Walk(physics, hero, Vector3.Zero, jump: false, ticks: 30);
        Walk(physics, hero, Vector3.Zero, jump: true, ticks: 1);
        Assert.True(_world.Get<Jumper>(hero).Rise < 0f);
        Assert.Equal(31f / 60f, _world.Get<Jumper>(hero).AirTime, 0.001f);
    }

    [Fact]
    public void A_button_already_down_at_the_first_walk_doesnt_jump()
    {
        using var physics = new Physics(_world);
        var hero = Hero(physics, new Jumper(JumpSpeed: 6f, Gravity: 12f));
        _world.Get<Jumper>(hero) = new Jumper(JumpSpeed: 6f, Gravity: 12f);

        // Held from before the jumper ever walked: it stays on the ground.
        Walk(physics, hero, Vector3.Zero, jump: true, ticks: 5);
        Assert.Equal(0f, _world.Get<Jumper>(hero).Rise);

        // Let go and pressed again, it jumps.
        Walk(physics, hero, Vector3.Zero, jump: false, ticks: 1);
        Walk(physics, hero, Vector3.Zero, jump: true, ticks: 1);
        Assert.Equal(6f, _world.Get<Jumper>(hero).Rise, 0.01f);
    }

    [Fact]
    public void A_second_press_in_the_air_doesnt_jump_again()
    {
        using var physics = new Physics(_world);
        var hero = Hero(physics, new Jumper(JumpSpeed: 6f, Gravity: 12f, Forgiveness: 0.5f, ShortHop: 1f));

        // Pressed, let go, and pressed again three ticks on, well inside the half second it forgives.
        Walk(physics, hero, Vector3.Zero, jump: true, ticks: 1);
        Walk(physics, hero, Vector3.Zero, jump: false, ticks: 1);
        Walk(physics, hero, Vector3.Zero, jump: true, ticks: 1);

        // Still slowing from the first jump: 6 m/s less two ticks of gravity is 5.6 m/s.
        Assert.Equal(5.6f, _world.Get<Jumper>(hero).Rise, 0.01f);
    }

    [Fact]
    public void A_jumper_rides_the_platform_it_stands_on()
    {
        using var physics = new Physics(_world);
        var lift = Place(new Vector3(0f, -0.5f, 0f), new Vector3(6f, 1f, 6f));
        var hero = Hero(physics, new Jumper(), floor: false);
        float from = _world.Get<Transform>(hero).Position.X;

        // The lift slides 0.05 m a tick, and the first tick is spent learning its speed.
        for (int i = 0; i < 31; i++)
        {
            _world.Get<Transform>(lift).Position += new Vector3(0.05f, 0f, 0f);
            Walk(physics, hero, Vector3.Zero, jump: false, ticks: 1);
        }

        Assert.Equal(1.5f, _world.Get<Transform>(hero).Position.X - from, 0.06f);
        Assert.Equal(0f, _world.Get<Jumper>(hero).Rise);
    }

    [Fact]
    public void Walking_an_entity_with_no_jumper_says_what_it_needs()
    {
        using var physics = new Physics(_world);
        var hero = _world.Create();
        _world.Add(hero, new Character());

        var error = Assert.Throws<InvalidOperationException>(() => physics.Characters.Walk(hero, Vector3.Zero, false, Tick));

        Assert.Equal($"Entity {hero.Id} can't Walk: it needs a Jumper. Use Move for a Character without one.", error.Message);
    }
}
