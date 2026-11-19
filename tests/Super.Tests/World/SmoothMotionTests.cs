using System.Numerics;
using Rendition;
using Super.Testing;

namespace Super.Tests;

public class SmoothMotionTests
{
    private sealed class Slider(World world) : IGame
    {
        public Entity Entity { get; private set; }

        public void Load() => Entity = world.Spawn(Transform.At(Vector3.Zero), new Model(ModelRef.Cube));

        public void SpawnAt(float x) => world.Spawn(Transform.At(new Vector3(x, 0f, 0f)), new Model(ModelRef.Cube));

        public void Update(GameTime time) => world.Get<Transform>(Entity).Position += new Vector3(2f, 0f, 0f);

        public void Draw(View view) => view.Models(world);
    }

    [Fact]
    public void An_entity_is_drawn_between_its_last_two_ticks_without_asking()
    {
        using var run = HeadlessGame.Create<Slider>(seed: 1);
        run.Ticks(2); // X was 2, is 4

        run.Frame(0.25f);

        Assert.Equal(2.5f, run.Drawn.Single().Transform.Translation.X, 4);
    }

    [Fact]
    public void A_new_entity_is_drawn_where_it_is_on_its_first_frame()
    {
        using var run = HeadlessGame.Create<Slider>(seed: 1);
        run.Ticks(1);
        run.Game.SpawnAt(10f); // Spawned after the tick's remembering.

        run.Frame(0.25f);

        Assert.Contains(run.Drawn, drawn => drawn.Transform.Translation.X == 10f);
    }

    [Fact]
    public void A_teleported_entity_does_not_glide()
    {
        using var run = HeadlessGame.Create<Slider>(seed: 1);
        run.Ticks(2);
        run.World.Teleport(run.Game.Entity, Transform.At(new Vector3(100f, 0f, 0f)));

        run.Frame(0.25f);

        Assert.Equal(100f, run.Drawn.Single().Transform.Translation.X);
    }

    [Fact]
    public void A_teleport_lands_the_transform_it_was_given()
    {
        using var run = HeadlessGame.Create<Slider>(seed: 1);
        var turned = new Transform(new Vector3(5f, 1f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f), Vector3.One);

        run.World.Teleport(run.Game.Entity, turned);

        Assert.Equal(turned, run.World.Get<Transform>(run.Game.Entity));
    }

    [Fact]
    public void A_static_entity_is_drawn_where_it_is()
    {
        using var run = HeadlessGame.Create<Slider>(seed: 1);
        run.Ticks(2);
        run.World.Add(run.Game.Entity, new Static()); // Made static after it moved.

        run.Frame(0.25f);

        Assert.Equal(4f, run.Drawn.Single().Transform.Translation.X);
    }

    [Fact]
    public void A_child_follows_its_parent_between_ticks()
    {
        using var run = HeadlessGame.Create<Slider>(seed: 1);
        var child = run.World.Spawn(Transform.At(new Vector3(0f, 1f, 0f)), new Model(ModelRef.Cube), new Parent(run.Game.Entity));
        run.Ticks(2);

        run.Frame(0.25f);

        Assert.Equal(2.5f, run.Drawn.Single(drawn => drawn.Key == child.Id).Transform.Translation.X, 4);
    }
}
